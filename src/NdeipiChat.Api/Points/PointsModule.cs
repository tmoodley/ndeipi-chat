using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api.Auth;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Points;

/// <summary>
/// Believe Points, shared across Ndeipi (PointsContract): one ledger, one balance per person. Apps
/// award points on the server through <see cref="PointsService"/>; people read their balance at
/// api/points.
/// </summary>
public static class PointsModule
{
    public static IServiceCollection AddPoints(this IServiceCollection services) => services.AddScoped<PointsService>();

    public static void MapPoints(this IEndpointRouteBuilder app) =>
        app.MapGet("/" + PointsContract.BasePath, async (HttpContext http, CurrentUserService users, PointsService points) =>
            Results.Ok(await points.GetAsync((await users.GetAsync(http.User, http.RequestAborted)).Id, http.RequestAborted)))
            .RequireAuthorization();
}

public sealed class PointsService(ChatDbContext db, TimeProvider clock)
{
    /// <summary>
    /// Awards <paramref name="points"/> for one thing (<paramref name="source"/> + <paramref name="sourceId"/>).
    /// Awarding for the same thing again does nothing: false then.
    /// </summary>
    public async Task<bool> AwardAsync(Guid userId, int points, string source, string sourceId, string description, CancellationToken ct)
    {
        if (points == 0 || await db.Points.AnyAsync(p => p.Source == source && p.SourceId == sourceId, ct))
            return false;
        db.Points.Add(new PointsEntry
        {
            UserId = userId,
            Points = points,
            Source = source,
            SourceId = sourceId,
            Description = description.Length > 200 ? description[..200] : description,
            CreatedAt = clock.GetUtcNow()
        });
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            // The same award, made at the same moment elsewhere: that one stands.
            db.ChangeTracker.Clear();
            return false;
        }
    }

    public async Task<PointsDto> GetAsync(Guid userId, CancellationToken ct)
    {
        var balance = await db.Points.Where(p => p.UserId == userId).SumAsync(p => (int?)p.Points, ct) ?? 0;
        var recent = await db.Points.AsNoTracking().Where(p => p.UserId == userId).OrderByDescending(p => p.CreatedAt).Take(20)
            .Select(p => new PointsEntryDto(p.Source, p.Description, p.Points, p.CreatedAt)).ToListAsync(ct);
        return new PointsDto(balance, recent);
    }
}
