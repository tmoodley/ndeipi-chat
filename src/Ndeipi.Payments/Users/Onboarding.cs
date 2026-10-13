using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Data;
using Ndeipi.Payments.Webhooks;

namespace Ndeipi.Payments.Users;

public sealed record OnboardingLinksRequest(string? RedirectUrl);

public sealed record OnboardingLinkDto(string Url, string Status, DateTimeOffset ExpiresAt);

/// <summary>The <c>OnboardingLinks</c> object (openapi.yaml).</summary>
public sealed record OnboardingLinksDto(string UserId, OnboardingLinkDto Kyc, OnboardingLinkDto Terms)
{
    public string Object => "onboarding_links";
}

/// <summary>
/// Hosted onboarding (FR-USER-03, SRV-KYC-01): one call issues a KYC link from the verification
/// provider and a link to Ndeipi's hosted terms page, and supersedes the user's earlier links.
/// The terms link carries a signed, time-limited token naming the user, so the hosted page needs
/// no other identifier; the page itself is outside the SRS's scope (§1.2).
/// </summary>
public sealed class OnboardingService(
    PaymentsDbContext db, IKycProvider kyc, IDataProtectionProvider protection, IOptions<PaymentsOptions> options, TimeProvider clock)
{
    public const string TermsPurpose = "Ndeipi.Payments.TermsLink.v1";

    public async Task<OnboardingLinksDto> CreateAsync(string userId, OnboardingLinksRequest request, CancellationToken ct)
    {
        Uri? redirect = null;
        if (request.RedirectUrl is not null &&
            (!Uri.TryCreate(request.RedirectUrl, UriKind.Absolute, out redirect) || redirect.Scheme != Uri.UriSchemeHttps))
            throw PaymentsException.Validation([new("redirect_url", "invalid_format", "redirect_url is an absolute HTTPS URL.")]);

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw PaymentsException.NotFound("user");
        if (user.Status == UserStatus.Deactivated)
            throw PaymentsException.Forbidden("user_deactivated", "This user is deactivated.");

        var now = clock.GetUtcNow();
        var lifetime = options.Value.Onboarding.LinkLifetime;
        var kycLink = await kyc.CreateLinkAsync(user.Id, user.Type == UserType.Business ? "business" : "individual", redirect?.ToString(), ct);
        var token = protection.CreateProtector(TermsPurpose).ToTimeLimitedDataProtector().Protect(user.Id, lifetime);
        var termsUrl = $"{options.Value.Onboarding.TermsBaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(token)}";

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.OnboardingLinks.Where(l => l.UserId == user.Id && l.SupersededAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.SupersededAt, now), ct);
        db.OnboardingLinks.AddRange(
            new OnboardingLink { UserId = user.Id, Kind = OnboardingLinkKind.Kyc, Url = kycLink.Url.ToString(), ExpiresAt = kycLink.ExpiresAt, CreatedAt = now },
            new OnboardingLink { UserId = user.Id, Kind = OnboardingLinkKind.Terms, Url = termsUrl, ExpiresAt = now + lifetime, CreatedAt = now });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new OnboardingLinksDto(
            user.Id,
            new OnboardingLinkDto(kycLink.Url.ToString(), WireEnum.Name(user.KycStatus), kycLink.ExpiresAt),
            new OnboardingLinkDto(termsUrl, WireEnum.Name(user.TermsStatus), now + lifetime));
    }

    /// <summary>The user a terms link names, or null if it is forged or expired. For the hosted terms page.</summary>
    public string? UserForTermsToken(string token)
    {
        try
        {
            return protection.CreateProtector(TermsPurpose).ToTimeLimitedDataProtector().Unprotect(token);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>
/// Changes a user's KYC and terms status, from the verification provider, the hosted terms page or
/// the sandbox, and emits one event per status that changed (FR-USER-08) in the same transaction.
/// Rejected and incomplete users always carry reasons (FR-USER-06).
/// </summary>
public sealed class UserStatusService(PaymentsDbContext db, EventOutbox events, TimeProvider clock)
{
    public async Task<UserDto> SetAsync(string userId, KycStatus kyc, TermsStatus? terms, IReadOnlyList<ReasonDto>? reasons, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw PaymentsException.NotFound("user");
        if (user.Status == UserStatus.Deactivated)
            throw PaymentsException.Forbidden("user_deactivated", "This user is deactivated.");

        IReadOnlyList<ReasonDto> newReasons = kyc is KycStatus.Rejected or KycStatus.Incomplete
            ? reasons is { Count: > 0 } ? reasons : [new ReasonDto("unspecified", "Verification did not pass; no reason was given.")]
            : [];
        var newReasonsJson = JsonSerializer.Serialize(newReasons, PaymentsJson.Options);

        // New reasons for the same status are a KYC change too: the integrator needs them.
        var kycChanged = user.KycStatus != kyc || user.RejectionReasonsJson != newReasonsJson;
        var termsChanged = terms is not null && user.TermsStatus != terms;
        if (!kycChanged && !termsChanged)
            return UserDto.From(user);

        var previous = new
        {
            user.KycStatus,
            user.TermsStatus,
            RejectionReasons = JsonSerializer.Deserialize<List<ReasonDto>>(user.RejectionReasonsJson, PaymentsJson.Options)
        };
        user.KycStatus = kyc;
        if (terms is { } newTerms)
            user.TermsStatus = newTerms;
        user.RejectionReasonsJson = newReasonsJson;
        user.Version++;
        user.UpdatedAt = clock.GetUtcNow();
        var snapshot = UserDto.From(user);
        if (kycChanged)
            await events.AddAsync(EventTypes.UserKycStatusChanged, "user", user.Id, user.Version, snapshot,
                new { kyc_status = previous.KycStatus, rejection_reasons = previous.RejectionReasons }, ct);
        if (termsChanged)
            await events.AddAsync(EventTypes.UserTermsStatusChanged, "user", user.Id, user.Version, snapshot,
                new { terms_status = previous.TermsStatus }, ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw PaymentsException.Conflict("invalid_state", "The user changed while this update ran. Try again.");
        }
        return snapshot;
    }
}
