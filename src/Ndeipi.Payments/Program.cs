using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Data;
using Ndeipi.Payments.Ledger;
using Ndeipi.Payments.Ops;
using Ndeipi.Payments.Ramps;
using Ndeipi.Payments.Reconciliation;
using Ndeipi.Payments.Sandbox;
using Ndeipi.Payments.Transfers;
using Ndeipi.Payments.Users;
using Ndeipi.Payments.Webhooks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<PaymentsOptions>(builder.Configuration.GetSection(PaymentsOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IntegratorScope>();
builder.Services.AddDbContext<PaymentsDbContext>((sp, o) =>
    o.UseSqlServer(sp.GetRequiredService<IConfiguration>().GetConnectionString("Payments"),
        sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", PaymentsDbContext.Schema)));

// Encrypts webhook signing keys at rest; the key ring lives in the payments database so every
// instance can read what another wrote.
builder.Services.AddDataProtection()
    .SetApplicationName("Ndeipi.Payments")
    .PersistKeysToDbContext<PaymentsDbContext>();

builder.Services.Configure<JsonOptions>(o => PaymentsJson.Configure(o.SerializerOptions));
// Malformed bodies throw, so they leave as the contract's 400 rather than an empty response.
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);

builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, OperatorAuthenticationHandler>(OperatorAuthenticationHandler.SchemeName, null);
// The operator API accepts operator keys only; integrator keys never reach it (SRV-OPS-04).
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(OperatorAuthenticationHandler.Policy, policy => policy
        .AddAuthenticationSchemes(OperatorAuthenticationHandler.SchemeName)
        .RequireClaim(OperatorAuthenticationHandler.NameClaim));
builder.Services.AddScoped<ApiKeyService>();

// Per-key rate limit, answered with 429 and Retry-After (SRV-OPS-06).
builder.Services.AddRateLimiter(o =>
{
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
    {
        var key = http.User.FindFirst(ApiKeyAuthenticationHandler.KeyClaim)?.Value;
        if (key is null)
            return RateLimitPartition.GetNoLimiter("unauthenticated");
        var limits = http.RequestServices.GetRequiredService<IOptions<PaymentsOptions>>().Value.RateLimit;
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limits.PermitLimit,
            Window = limits.Window,
            QueueLimit = 0
        });
    });
    o.OnRejected = (context, ct) =>
    {
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : TimeSpan.FromSeconds(1);
        context.HttpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        return new ValueTask(ApiErrors.WriteAsync(context.HttpContext, 429, new ApiError("rate_limited", "Too many requests for this API key. Retry after the Retry-After interval.")));
    };
});

builder.Services.AddPaymentUsers();
builder.Services.AddLedger();
builder.Services.AddTransfers();
builder.Services.AddRamps();
builder.Services.AddWebhooks();
builder.Services.AddOps();
builder.Services.AddScoped<ReconciliationService>();

var app = builder.Build();

var options = app.Services.GetRequiredService<IOptions<PaymentsOptions>>().Value;
if (options.Environment == PaymentsEnvironment.Production && options.UsesSimulatedProviders)
    throw new InvalidOperationException("Production cannot run on simulated providers (SC-06). Set Payments:FiatRails, Payments:Exchange and Payments:KycProvider.");

if (app.Configuration.GetValue("Database:MigrateOnStartup", false))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Database.MigrateAsync();
}

// `dotnet run -- issue-key "<integrator name>"`: creates an integrator and prints its key once
// (SRV-OPS-01). Stands in for the operator console until it exists.
if (args is ["issue-key", var integratorName])
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
    var integrator = new Integrator { Id = Guid.NewGuid(), Name = integratorName, CreatedAt = TimeProvider.System.GetUtcNow() };
    db.Integrators.Add(integrator);
    await db.SaveChangesAsync();
    var (_, key) = await scope.ServiceProvider.GetRequiredService<ApiKeyService>().IssueAsync(integrator.Id, options.Environment, default);
    Console.WriteLine($"Integrator {integrator.Id} ({integratorName})");
    Console.WriteLine($"API key (shown once): {key}");
    return;
}

// `dotnet run -- record-otc-trade <buy|sell> <coin amount> <usd amount> <absa reference> <desk reference> <recorded by> [executed at, ISO 8601]`:
// records a settled trade with Blockfinex's manual OTC desk, which sets the NdeipiCoin price.
// Stands in for the operator console until it exists.
if (args is ["record-otc-trade", var side, var coin, var usd, var absaReference, var deskReference, var recordedBy, .. var rest])
{
    await using var scope = app.Services.CreateAsyncScope();
    var trade = await scope.ServiceProvider.GetRequiredService<Ndeipi.Payments.Treasury.OtcDesk>().RecordAsync(new(
        Enum.Parse<OtcSide>(side, ignoreCase: true),
        decimal.Parse(coin, System.Globalization.CultureInfo.InvariantCulture),
        decimal.Parse(usd, System.Globalization.CultureInfo.InvariantCulture),
        absaReference,
        deskReference,
        recordedBy,
        rest is [var executedAt] ? DateTimeOffset.Parse(executedAt, System.Globalization.CultureInfo.InvariantCulture) : TimeProvider.System.GetUtcNow()), default);
    Console.WriteLine($"Recorded OTC {trade.Side.ToString().ToLowerInvariant()} of {trade.CoinAmount} NdeipiCoin for {trade.UsdAmount} USD: {trade.UsdPerCoin} USD per coin.");
    return;
}

// `dotnet run -- issue-operator-key "<operator name>"`: a key for one named operator, printed once.
if (args is ["issue-operator-key", var operatorName])
{
    await using var scope = app.Services.CreateAsyncScope();
    var (_, key) = await scope.ServiceProvider.GetRequiredService<OperatorKeyService>().IssueAsync(operatorName, default);
    Console.WriteLine($"Operator key for {operatorName} (shown once): {key}");
    return;
}

// `dotnet run -- treasury-deposit <rail> <currency> <amount> "<description>"`: Ndeipi's own money
// arriving in its account on a rail, into the treasury.
if (args is ["treasury-deposit", var depositRail, var depositCurrency, var depositAmount, var depositDescription])
{
    await using var scope = app.Services.CreateAsyncScope();
    var provider = scope.ServiceProvider.GetRequiredService<Ndeipi.Payments.Providers.ProviderRegistry>().RailFor(depositRail).Name;
    await scope.ServiceProvider.GetRequiredService<Ndeipi.Payments.Treasury.TreasuryService>().DepositAsync(
        provider, depositCurrency, decimal.Parse(depositAmount, System.Globalization.CultureInfo.InvariantCulture), depositDescription, default);
    Console.WriteLine($"Treasury received {depositAmount} {depositCurrency} on {depositRail}.");
    return;
}

// `dotnet run -- treasury-buy-points <currency> <amount>`: the treasury buys points for itself, so
// it can pay users who sell NdeipiCoin back.
if (args is ["treasury-buy-points", var pointsCurrency, var pointsFiat])
{
    await using var scope = app.Services.CreateAsyncScope();
    var bought = await scope.ServiceProvider.GetRequiredService<Ndeipi.Payments.Treasury.TreasuryService>().BuyPointsAsync(
        pointsCurrency, decimal.Parse(pointsFiat, System.Globalization.CultureInfo.InvariantCulture), default);
    Console.WriteLine($"Treasury bought {bought} points for {pointsFiat} {pointsCurrency}.");
    return;
}

app.UseRequestIds();
app.UsePaymentsErrors();
app.UseMiddleware<AuditMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseMiddleware<IdempotencyMiddleware>();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// The version lives in the path (FR-CORE-09); every operation needs an API key.
var v1 = app.MapGroup("/v1").RequireAuthorization();
v1.MapUsers();
v1.MapWallets();
v1.MapTransfers();
v1.MapRamps();
v1.MapWebhooks();
v1.MapSandbox();
app.MapOps();

app.Run();

public partial class Program;
