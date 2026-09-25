using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NdeipiChat.Api.Auth;
using NdeipiChat.Api.Banking;
using NdeipiChat.Api.Data;
using NdeipiChat.Api.Livestock;
using NdeipiChat.Contracts;

namespace NdeipiChat.Tests.Infrastructure;

/// <summary>
/// The real API on a TestServer, against its own LocalDB database, with Clerk's Backend API and
/// Bridge replaced by fakes. Session tokens are signed with a local key the API is told to trust,
/// so the token validation itself -- issuer, lifetime, azp -- is the production code path.
/// </summary>
public sealed class TestApp : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Issuer = "https://clerk.test";
    public const string TrustedOrigin = "https://chat.test";
    public const string RedirectUri = "ndeipichat://auth";
    public const string KnownTokenContract = "0x1111111111111111111111111111111111111111";

    /// <summary>Stands in for the key pair behind Bridge's webhook signatures.</summary>
    public static readonly RSA WebhookKey = TestTokens.CreateRsa();

    readonly string _database = $"NdeipiChatTests_{Guid.NewGuid():N}";

    public FakeClerk Clerk { get; } = new();
    public StubBridge Bridge { get; } = new();
    public StubClaude Claude { get; } = new();

    /// <summary>Photos the API stores, and the app's capture queue in client tests.</summary>
    public string FilesDirectory { get; } = Path.Combine(Path.GetTempPath(), "ndeipi-tests", Guid.NewGuid().ToString("N"));

    public string ConnectionString =>
        $"Server=(localdb)\\MSSQLLocalDB;Database={_database};Trusted_Connection=True;TrustServerCertificate=True";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Chat"] = ConnectionString,
            ["Database:MigrateOnStartup"] = "true",
            ["Clerk:Authority"] = Issuer,
            ["Clerk:PublishableKey"] = "pk_test_publishable",
            ["Clerk:AuthorizedParties:0"] = TrustedOrigin,
            ["MobileAuth:RedirectUris:0"] = RedirectUri,
            ["Tokens:QueuePollInterval"] = "00:00:00.200",
            ["Tokens:Known:0:Chain"] = "polygon",
            ["Tokens:Known:0:Symbol"] = "NMX",
            ["Tokens:Known:0:Standard"] = "erc20",
            ["Tokens:Known:0:ContractAddress"] = KnownTokenContract,
            ["Tokens:Known:0:Decimals"] = "6",
            ["Bridge:BaseUrl"] = "https://bridge.test/v0/",
            ["Bridge:ApiKey"] = "bridge-test-key",
            ["Bridge:WebhookPublicKey"] = WebhookKey.ExportSubjectPublicKeyInfoPem(),
            ["Bridge:WalletChain"] = "solana",
            ["Bridge:Currency"] = "usdc",
            ["Bridge:PollInterval"] = "00:00:00",
            ["Livestock:ImageStoragePath"] = Path.Combine(FilesDirectory, "livestock-images"),
            ["Livestock:Claude:ApiKey"] = "claude-test-key",
            ["Livestock:Claude:MaxAttempts"] = "1",
            ["Livestock:Muzzle:ModelPath"] = TinyMuzzleModel.Path,
            ["Livestock:Muzzle:ModelId"] = "test-grid-pool"
        }));

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IClerkBackendApi>(Clerk);
            services.AddHttpClient<BridgeClient>()
                .ConfigurePrimaryHttpMessageHandler(() => Bridge)
                .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
            services.AddHttpClient<ICattleVisionAssessor, ClaudeCattleAssessor>()
                .ConfigurePrimaryHttpMessageHandler(() => Claude)
                .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                // Trust the test signing key instead of fetching Clerk's JWKS.
                o.ConfigurationManager = null;
                o.TokenValidationParameters.IssuerSigningKey = TestTokens.Key;
            });
        });
    }

    public Task InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        SqlConnection.ClearAllPools();
        await using var master = new SqlConnection(new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" }.ConnectionString);
        await master.OpenAsync();
        await using var drop = master.CreateCommand();
        drop.CommandText = $"IF DB_ID('{_database}') IS NOT NULL BEGIN ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]; END";
        await drop.ExecuteNonQueryAsync();

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(FilesDirectory))
            Directory.Delete(FilesDirectory, recursive: true);
    }

    public HttpClient ClientWithToken(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>A Clerk user who has signed in once, so the API knows them.</summary>
    public async Task<TestUser> CreateUserAsync(string name, string? email = null, string? phone = null, bool verified = true)
    {
        var clerkId = "user_" + Guid.NewGuid().ToString("N")[..16];
        var parts = name.Split(' ', 2);
        var verification = new ClerkVerification(verified ? "verified" : "unverified");
        Clerk.AddUser(new ClerkUser(clerkId, parts[0], parts.ElementAtOrDefault(1), null, null, "idn_1",
            [new ClerkEmailAddress("idn_1", email ?? $"{clerkId}@example.test", verification)],
            phone is null ? null : "idn_2",
            phone is null ? null : [new ClerkPhoneNumber("idn_2", phone, verification)]));

        var sessionId = Clerk.StartSession(clerkId);
        var http = ClientWithToken(TestTokens.Create(clerkId, sessionId));
        var me = await http.GetFromJsonAsync<MeDto>("api/me", ContractJson.Options);
        return new TestUser(clerkId, sessionId, me!.Id, name, http);
    }

    public HubConnection Hub(TestUser user) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(Server.BaseAddress, ChatHubContract.Path.TrimStart('/')), o =>
            {
                o.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.AccessTokenProvider = () => Task.FromResult<string?>(TestTokens.Create(user.ClerkId, user.SessionId));
            })
            .AddJsonProtocol(o => ContractJson.Configure(o.PayloadSerializerOptions))
            .Build();

    public async Task<HubConnection> ConnectAsync(TestUser user)
    {
        var hub = Hub(user);
        await hub.StartAsync();
        return hub;
    }

    public async Task<T> DbAsync<T>(Func<ChatDbContext, Task<T>> work)
    {
        await using var scope = Services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<ChatDbContext>());
    }

    public Task DbAsync(Func<ChatDbContext, Task> work) => DbAsync(async db =>
    {
        await work(db);
        return true;
    });

    /// <summary>Runs SQL as Ndeipi Enterprise Server would -- straight against the database.</summary>
    public async Task<List<Dictionary<string, object?>>> SqlAsync(string sql, params SqlParameter[] parameters)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await using var reader = await command.ExecuteReaderAsync();

        var rows = new List<Dictionary<string, object?>>();
        do
        {
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
        }
        while (await reader.NextResultAsync());
        return rows;
    }
}

public sealed record TestUser(string ClerkId, string SessionId, Guid Id, string Name, HttpClient Http)
{
    public async Task<ConversationDto> StartDirectChatAsync(TestUser other) =>
        await PostAsync<ConversationDto>("api/conversations", new CreateConversationRequest(ConversationType.Direct, [other.Id], null));

    public async Task<T> PostAsync<T>(string path, object body)
    {
        using var response = await Http.PostAsJsonAsync(path, body, ContractJson.Options);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(ContractJson.Options))!;
    }

    public async Task<T> GetAsync<T>(string path) => (await Http.GetFromJsonAsync<T>(path, ContractJson.Options))!;
}
