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

    /// <param name="actsForOfficers">A gateway key for the Ndeipi app's server, which names the officer it acts for.</param>
    public async Task<(OperatorKey Record, string Key)> IssueAsync(string @operator, CancellationToken ct, bool actsForOfficers = false)
    {
        if (string.IsNullOrWhiteSpace(@operator))
            throw new ArgumentException("An operator key names its operator.");
        var key = Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var record = new OperatorKey
        {
            Id = Guid.NewGuid(), Operator = @operator.Trim(), KeyHash = ApiKeyService.Hash(key), ActsForOfficers = actsForOfficers, CreatedAt = clock.GetUtcNow()
        };
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
    public const string OfficerIdClaim = "officer_id";

    /// <summary>
    /// Sent by a gateway key: <c>&lt;officer id&gt;;&lt;URL-encoded name&gt;</c>, the officer the gateway
    /// signed in. Ignored from any other key.
    /// </summary>
    public const string ActingOfficerHeader = "Acting-Officer";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var key = Request.Headers[Header].ToString();
        if (key.Length == 0)
            return AuthenticateResult.NoResult();
        var hash = ApiKeyService.Hash(key);
        var record = await db.OperatorKeys.AsNoTracking().FirstOrDefaultAsync(k => k.KeyHash == hash, Context.RequestAborted);
        if (record is null || record.RevokedAt is not null)
            return AuthenticateResult.Fail("unauthorized");
        var claims = new List<Claim>();
        if (!record.ActsForOfficers)
        {
            claims.Add(new Claim(NameClaim, record.Operator));
            claims.Add(new Claim(OfficerIdClaim, $"operator:{record.Id}"));
        }
        // The gateway vouches for the officer; naming none, it can read but not decide.
        else if (Request.Headers[ActingOfficerHeader].ToString().Split(';', 2) is [{ Length: > 0 } id, { Length: > 0 } name])
        {
            claims.Add(new Claim(NameClaim, $"{Uri.UnescapeDataString(name).Trim()} via {record.Operator}"));
            claims.Add(new Claim(OfficerIdClaim, $"officer:{id.Trim()}"));
        }
        else
            claims.Add(new Claim(NameClaim, record.Operator));
        var identity = new ClaimsIdentity(claims, SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        ApiErrors.WriteAsync(Context, 401, new ApiError("unauthorized", "Send an operator key in the Operator-Key header."));

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        ApiErrors.WriteAsync(Context, 403, new ApiError("forbidden", "This key may not use the operator API."));

    public static string Name(HttpContext http) =>
        http.User.FindFirst(NameClaim)?.Value ?? throw new InvalidOperationException("No operator on this request.");

    /// <summary>
    /// The person acting: a named operator, or the officer a gateway acts for. Null when a gateway
    /// key names no officer.
    /// </summary>
    public static Officer? OfficerOf(HttpContext http) =>
        http.User.FindFirst(OfficerIdClaim)?.Value is { } id ? new Officer(id, Name(http)) : null;
}

/// <summary>Who is acting on the operator API: <see cref="Id"/> is stable and is what four-eyes rules compare.</summary>
public sealed record Officer(string Id, string Name);
