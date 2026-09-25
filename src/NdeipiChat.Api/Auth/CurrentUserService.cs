using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NdeipiChat.Api.Data;
using NdeipiChat.Api.Shamwaris;

namespace NdeipiChat.Api.Auth;

/// <summary>
/// Resolves the signed-in Clerk user to a local <see cref="User"/>, creating it on first sight and
/// refreshing the name, email, phone and avatar from Clerk now and then (session tokens don't carry them).
/// </summary>
public sealed class CurrentUserService(
    ChatDbContext db,
    IClerkBackendApi clerk,
    IOptions<ClerkOptions> options,
    ShamwariService shamwaris,
    TimeProvider clock,
    ILogger<CurrentUserService> log)
{
    public Task<User> GetAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var clerkId = principal.FindFirst(ClerkAuthentication.UserIdClaim)?.Value
            ?? throw new UnauthorizedAccessException("The token has no subject.");
        return GetByClerkIdAsync(clerkId, ct);
    }

    public async Task<User> GetByClerkIdAsync(string clerkId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var user = await db.Users.FirstOrDefaultAsync(u => u.ClerkUserId == clerkId, ct);
        if (user is not null && now - user.ProfileSyncedAt < options.Value.ProfileRefreshInterval)
            return user;

        ClerkUser? profile = null;
        try
        {
            profile = await clerk.GetUserAsync(clerkId, ct);
        }
        catch (HttpRequestException ex)
        {
            // Clerk being unreachable shouldn't lock people out of chat; the profile is retried next time.
            log.LogWarning(ex, "Couldn't load Clerk profile for {ClerkUserId}", clerkId);
            if (user is not null)
                return user;
        }

        var created = user is null;
        user ??= db.Users.Add(new User { Id = Guid.NewGuid(), ClerkUserId = clerkId, CreatedAt = now }).Entity;

        if (profile is not null)
        {
            user.DisplayName = profile.FullName ?? profile.Username ?? profile.PrimaryEmail?.Split('@')[0] ?? user.DisplayName;
            user.Username = profile.Username;
            user.Email = profile.PrimaryEmail;
            user.EmailVerified = profile.PrimaryEmailVerified;
            user.Phone = ShamwariContact.NormalizePhone(profile.VerifiedPhone);
            user.AvatarUrl = profile.ImageUrl;
            user.ProfileSyncedAt = now;
        }
        if (user.DisplayName.Length == 0)
            user.DisplayName = "User " + clerkId[^Math.Min(6, clerkId.Length)..];

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (created)
        {
            // Another request (usually the hub connecting alongside the first API call) created
            // this user a moment earlier.
            db.Entry(user).State = EntityState.Detached;
            return await db.Users.FirstAsync(u => u.ClerkUserId == clerkId, ct);
        }

        if (profile is not null)
            await shamwaris.ClaimInvitesAsync(user, ct);
        return user;
    }
}
