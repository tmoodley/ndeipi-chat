using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Ndeipi.Payments.Webhooks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Data;

namespace Ndeipi.Payments.Tests.Infrastructure;

/// <summary>
/// The payments server on a TestServer, against its own database, in sandbox mode with the
/// simulated providers. Integrators and keys are created through the real <see cref="ApiKeyService"/>.
///
/// The database is on LocalDB, or on the SQL Server in <c>PAYMENTS_TEST_SQL</c> (a connection string
/// without a database, as CI sets for its SQL Server container).
/// </summary>
public class PaymentsApp : WebApplicationFactory<Program>, IAsyncLifetime
{
    const string LocalDb = "Server=(localdb)\\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True";

    readonly string _database = $"NdeipiPaymentsTests_{Guid.NewGuid():N}";

    public string ConnectionString => new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("PAYMENTS_TEST_SQL") is { Length: > 0 } server ? server : LocalDb)
    {
        InitialCatalog = _database
    }.ConnectionString;

    /// <summary>Settings on top of the defaults below, for apps that test other configurations.</summary>
    protected virtual IDictionary<string, string?> Overrides => new Dictionary<string, string?>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(config =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Payments"] = ConnectionString,
                ["Database:MigrateOnStartup"] = "true",
                ["Payments:Environment"] = "sandbox",
                ["Payments:FiatRails:0"] = "simulated",
                ["Payments:Exchange"] = "none",
                ["Payments:KycProvider"] = "simulated",
                ["Payments:RateLimit:PermitLimit"] = "100000",
                // Fast enough that retries and giving up happen within a test.
                ["Payments:Webhooks:PollInterval"] = "00:00:00.050",
                ["Payments:Webhooks:RetryBase"] = "00:00:00.100",
                ["Payments:Webhooks:MaxRetryDelay"] = "00:00:00.300",
                ["Payments:Webhooks:RetryWindow"] = "00:00:02",
                ["Payments:RampPollInterval"] = "00:00:00.050"
            });
            config.AddInMemoryCollection(Overrides);
        });
        builder.ConfigureTestServices(services =>
            services.AddHttpClient(WebhookHttp.ClientName)
                .ConfigurePrimaryHttpMessageHandler(() => Webhooks)
                .SetHandlerLifetime(Timeout.InfiniteTimeSpan));
    }

    /// <summary>Every integrator's webhook endpoint: deliveries land here.</summary>
    public WebhookReceiver Webhooks { get; } = new();

    /// <summary>A new integrator with one key for this app's environment.</summary>
    public async Task<TestIntegrator> CreateIntegratorAsync(string name = "Acme Remit", PaymentsEnvironment environment = PaymentsEnvironment.Sandbox)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var integrator = new Integrator { Id = Guid.NewGuid(), Name = name, CreatedAt = DateTimeOffset.UtcNow };
        db.Integrators.Add(integrator);
        await db.SaveChangesAsync();
        var (record, key) = await scope.ServiceProvider.GetRequiredService<ApiKeyService>().IssueAsync(integrator.Id, environment, default);

        var client = CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, key);
        return new TestIntegrator(this, integrator.Id, record.Id, key, client);
    }

    /// <summary>Runs <paramref name="work"/> in a service scope acting as <paramref name="integratorId"/>, as a request would.</summary>
    public async Task<T> AsIntegratorAsync<T>(Guid integratorId, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IntegratorScope>().Set(integratorId, apiKeyId: null);
        return await work(scope.ServiceProvider);
    }

    /// <summary>A client acting as the named Ndeipi operator, with a key issued for them.</summary>
    public async Task<HttpClient> OperatorAsync(string name)
    {
        var key = await AsHouseAsync(async sp => (await sp.GetRequiredService<Ops.OperatorKeyService>().IssueAsync(name, default)).Key);
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(Ops.OperatorAuthenticationHandler.Header, key);
        return client;
    }

    /// <summary>Runs <paramref name="work"/> with no integrator scope, as the treasury's own operations do.</summary>
    public async Task<T> AsHouseAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = Services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    /// <summary>Reads the database with no integrator filter, as an operator would.</summary>
    public async Task<T> DbAsync<T>(Func<PaymentsDbContext, Task<T>> read)
    {
        await using var scope = Services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<PaymentsDbContext>());
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
    }
}

public sealed record TestIntegrator(PaymentsApp App, Guid Id, Guid KeyId, string Key, HttpClient Client)
{
    /// <summary>POSTs with an Idempotency-Key (a new one unless given), as the SDK does.</summary>
    public Task<HttpResponseMessage> PostAsync(string path, object? body, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = body is null ? null : JsonContent.Create(body)
        };
        request.Headers.Add(IdempotencyMiddleware.Header, idempotencyKey ?? Guid.NewGuid().ToString());
        return Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> GetAsync(string path) => Client.GetAsync(path);

    public Task<HttpResponseMessage> PatchAsync(string path, object body) => Client.PatchAsJsonAsync(path, body);
}

public static class ResponseExtensions
{
    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    public static async Task<string> ErrorCodeAsync(this HttpResponseMessage response) =>
        (await response.JsonAsync()).GetProperty("code").GetString()!;
}
