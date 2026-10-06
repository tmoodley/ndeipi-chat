using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ndeipi.Payments.Tests.Infrastructure;

namespace Ndeipi.Payments.Tests;

/// <summary>Keeps every log line the server writes, at debug level, so tests can search them.</summary>
public sealed class LoggedApp : PaymentsApp
{
    public ConcurrentQueue<string> Lines { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Debug).AddProvider(new Capture(Lines)));
    }

    sealed class Capture(ConcurrentQueue<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string category) => new Logger(category, lines);
        public void Dispose() { }
    }

    sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue($"{category} {formatter(state, exception)} {exception}");
    }
}

/// <summary>
/// Identity data and secrets (SRV-KYC-05, NFR-SEC-01, NFR-SEC-03, FR-API-07): names and contact
/// details are encrypted at rest, every read of them is audited, and neither they nor an API key
/// ever reach the server's logs.
/// </summary>
public sealed class IdentityProtectionTests(LoggedApp app) : IClassFixture<LoggedApp>
{
    const string Email = "thandiwe.distinct.address@example.com";
    const string FirstName = "Thandiwe-Distinctname";

    async Task<string> CreateUserAsync(TestIntegrator integrator) =>
        (await (await integrator.PostAsync("/v1/users", new
        {
            type = "individual", external_reference = $"crm-{Guid.NewGuid():N}", email = Email, first_name = FirstName, last_name = "Moyo", phone = "+263771234567"
        })).JsonAsync()).GetProperty("id").GetString()!;

    [Fact]
    public async Task Names_and_contact_details_are_stored_encrypted_and_read_back_in_clear()
    {
        var acme = await app.CreateIntegratorAsync();
        var user = await CreateUserAsync(acme);

        await using var sql = new SqlConnection(app.ConnectionString);
        await sql.OpenAsync();
        await using var command = new SqlCommand("SELECT [Email], [FirstName], [Phone] FROM [payments].[Users] WHERE [Id] = @id", sql);
        command.Parameters.AddWithValue("@id", user);
        await using var row = await command.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync());
        Assert.DoesNotContain("example.com", row.GetString(0));
        Assert.DoesNotContain("Thandiwe", row.GetString(1));
        Assert.DoesNotContain("771234567", row.GetString(2));

        var read = await (await acme.GetAsync($"/v1/users/{user}")).JsonAsync();
        Assert.Equal(Email, read.GetProperty("email").GetString());
        Assert.Equal(FirstName, read.GetProperty("first_name").GetString());
    }

    [Fact]
    public async Task Every_read_of_a_user_is_audited_and_other_reads_are_not()
    {
        var acme = await app.CreateIntegratorAsync();
        var user = await CreateUserAsync(acme);

        var userRead = await acme.GetAsync($"/v1/users/{user}");
        var routesRead = await acme.GetAsync("/v1/routes");

        var requestId = userRead.Headers.GetValues("Request-Id").Single();
        var entry = await app.DbAsync(db => db.AuditLog.SingleAsync(a => a.RequestId == requestId));
        Assert.Equal("GET", entry.Method);
        Assert.Equal(acme.KeyId, entry.ApiKeyId);
        var routesId = routesRead.Headers.GetValues("Request-Id").Single();
        Assert.False(await app.DbAsync(db => db.AuditLog.AnyAsync(a => a.RequestId == routesId)));
    }

    [Fact]
    public async Task No_api_key_or_identity_data_reaches_the_logs()
    {
        var acme = await app.CreateIntegratorAsync();
        var user = await CreateUserAsync(acme);
        await acme.PatchAsync($"/v1/users/{user}", new { email = "second." + Email });
        await acme.GetAsync($"/v1/users/{user}");
        await acme.PostAsync("/v1/users", new { type = "individual", external_reference = "bad", email = "not-an-email-" + FirstName });

        var wrongKey = app.CreateClient();
        wrongKey.DefaultRequestHeaders.Add("Api-Key", acme.Key[..^4] + "XXXX");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrongKey.GetAsync("/v1/users")).StatusCode);

        var lines = app.Lines.ToArray();
        Assert.NotEmpty(lines);
        Assert.DoesNotContain(lines, l => l.Contains(acme.Key[8..20], StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("distinct.address", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("Distinctname", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("771234567", StringComparison.Ordinal));
    }
}
