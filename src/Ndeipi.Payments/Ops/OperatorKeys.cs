using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Data;

namespace Ndeipi.Payments.Ops;

/// <summary>Operator keys: <c>nd_op_</c> and 256 random bits, stored only as a hash, one per named operator.</summary>
public sealed class OperatorKeyService(PaymentsDbContext db, TimeProvider clock)
{
    public const string Prefix = "nd_op_";

    public async Task<(OperatorKey Record, string Key)> IssueAsync(string @operator, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(@operator))
            throw new ArgumentException("An operator key names its operator.");
        var key = Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var record = new OperatorKey { Id = Guid.NewGuid(), Operator = @operator.Trim(), KeyHash = ApiKeyService.Hash(key), CreatedAt = clock.GetUtcNow() };
        db.OperatorKeys.Add(record);
        await db.SaveChangesAsync(ct);
        return (record, key);
    }

    public Task RevokeAsync(Guid id, CancellationToken ct) =>
        db.OperatorKeys.Where(k => k.Id == id && k.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedAt, clock.GetUtcNow()), ct);
}

/// <summary>
/// Authenticates the <c>Operator-Key</c> header for the operator API. A request carrying it is an
/// operator's, never an integrator's: it gets no integrator scope, and the audit log records the
/// operator's name against everything it changes (SRV-OPS-03).
/// </summary>
public sealed class OperatorAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, PaymentsDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Operator";
    public const string Header = "Operator-Key";
    public const string Policy = "Operator";
    public const string NameClaim = "operator";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var key = Request.Headers[Header].ToString();
        if (key.Length == 0)
            return AuthenticateResult.NoResult();
        var hash = ApiKeyService.Hash(key);
        var record = await db.OperatorKeys.AsNoTracking().FirstOrDefaultAsync(k => k.KeyHash == hash, Context.RequestAborted);
        if (record is null || record.RevokedAt is not null)
            return AuthenticateResult.Fail("unauthorized");
        var identity = new ClaimsIdentity([new Claim(NameClaim, record.Operator)], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        ApiErrors.WriteAsync(Context, 401, new ApiError("unauthorized", "Send an operator key in the Operator-Key header."));

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        ApiErrors.WriteAsync(Context, 403, new ApiError("forbidden", "This key may not use the operator API."));

    public static string Name(HttpContext http) =>
        http.User.FindFirst(NameClaim)?.Value ?? throw new InvalidOperationException("No operator on this request.");
}
