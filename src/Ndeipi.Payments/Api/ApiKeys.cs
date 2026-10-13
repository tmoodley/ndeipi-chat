using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ndeipi.Payments.Data;

namespace Ndeipi.Payments.Api;

/// <summary>
/// Integrator API keys: <c>nd_test_</c> for sandbox, <c>nd_live_</c> for production (FR-CORE-03),
/// then 43 characters of base64url carrying 256 random bits.
/// </summary>
public sealed class ApiKeyService(PaymentsDbContext db, TimeProvider clock)
{
    public const string SandboxPrefix = "nd_test_";
    public const string ProductionPrefix = "nd_live_";

    public static string PrefixFor(PaymentsEnvironment environment) =>
        environment == PaymentsEnvironment.Production ? ProductionPrefix : SandboxPrefix;

    public static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    /// <summary>Creates a key and returns it in plain text. This is the only time it exists outside the caller (SRV-OPS-01).</summary>
    public async Task<(ApiKey Record, string Key)> IssueAsync(Guid integratorId, PaymentsEnvironment environment, CancellationToken ct)
    {
        var key = PrefixFor(environment) + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var record = new ApiKey
        {
            Id = Guid.NewGuid(),
            IntegratorId = integratorId,
            Environment = environment,
            KeyHash = Hash(key),
            Last4 = key[^4..],
            CreatedAt = clock.GetUtcNow()
        };
        db.ApiKeys.Add(record);
        await db.SaveChangesAsync(ct);
        return (record, key);
    }

    public async Task RevokeAsync(Guid keyId, CancellationToken ct) =>
        await db.ApiKeys.Where(k => k.Id == keyId && k.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedAt, clock.GetUtcNow()), ct);
}

/// <summary>
/// Authenticates the <c>Api-Key</c> header and scopes the request to the key's integrator. Failures
/// answer with the contract's error body and never echo the key (FR-API-07).
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    PaymentsDbContext db,
    IntegratorScope integrator,
    IOptions<PaymentsOptions> payments)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string Header = "Api-Key";
    public const string IntegratorClaim = "integrator_id";
    public const string KeyClaim = "api_key_id";
    const string FailureCode = "payments.auth_failure";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var key = Request.Headers[Header].ToString();
        if (key.Length == 0)
            return Fail("unauthorized", "Send your API key in the Api-Key header.");

        var environment = payments.Value.Environment;
        var otherPrefix = ApiKeyService.PrefixFor(environment == PaymentsEnvironment.Production ? PaymentsEnvironment.Sandbox : PaymentsEnvironment.Production);
        if (key.StartsWith(otherPrefix, StringComparison.Ordinal))
            return Fail("key_environment_mismatch", $"This is the {environment.ToString().ToLowerInvariant()} environment; that key belongs to the other one.");

        var hash = ApiKeyService.Hash(key);
        var record = await db.ApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.KeyHash == hash, Context.RequestAborted);
        if (record is null || record.RevokedAt is not null || record.Environment != environment)
            return Fail("unauthorized", "The API key is invalid or has been revoked.");

        integrator.Set(record.IntegratorId, record.Id);
        var identity = new ClaimsIdentity(
            [new Claim(IntegratorClaim, record.IntegratorId.ToString()), new Claim(KeyClaim, record.Id.ToString())], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    AuthenticateResult Fail(string code, string message)
    {
        Context.Items[FailureCode] = new ApiError(code, message);
        return AuthenticateResult.Fail(code);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        ApiErrors.WriteAsync(Context, 401, Context.Items[FailureCode] as ApiError ?? new ApiError("unauthorized", "Send your API key in the Api-Key header."));

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        ApiErrors.WriteAsync(Context, 403, new ApiError("forbidden", "This key may not perform that action."));
}
