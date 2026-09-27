using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api.Auth;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Api.Launcher;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Inventory;

/// <summary>
/// The Inventory sub-app's API: a user's stock lines. The app itself is a Razor Class Library the
/// shell loads on first use; it calls these with the shell's sign-in, never its own.
/// </summary>
public static class InventoryModule
{
    public const int MaxItems = 1000;

    public static void MapInventory(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/" + InventoryContract.ItemsPath).RequireAuthorization().RequireSubApp(InventoryContract.AppId);

        api.MapGet("", async (HttpContext http, CurrentUserService users, ChatDbContext db) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return Results.Ok(await db.InventoryItems.AsNoTracking()
                .Where(i => i.OwnerId == me.Id)
                .OrderByDescending(i => i.UpdatedAt)
                .Select(i => ToDto(i))
                .ToListAsync(http.RequestAborted));
        });

        api.MapPost("", async (SaveInventoryItemRequest request, HttpContext http, CurrentUserService users, ChatDbContext db, TimeProvider clock) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            if (await db.InventoryItems.CountAsync(i => i.OwnerId == me.Id, http.RequestAborted) >= MaxItems)
                throw new ChatRejectedException($"Inventory is limited to {MaxItems} lines.");

            var now = clock.GetUtcNow();
            var item = new InventoryItem { Id = Guid.NewGuid(), OwnerId = me.Id, Name = "", CreatedAt = now };
            Apply(item, request, now);
            db.InventoryItems.Add(item);
            await db.SaveChangesAsync(http.RequestAborted);
            return Results.Ok(ToDto(item));
        });

        api.MapPut("/{id:guid}", async (Guid id, SaveInventoryItemRequest request, HttpContext http, CurrentUserService users, ChatDbContext db, TimeProvider clock) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            var item = await db.InventoryItems.FirstOrDefaultAsync(i => i.Id == id && i.OwnerId == me.Id, http.RequestAborted);
            if (item is null)
                return Results.NotFound();
            Apply(item, request, clock.GetUtcNow());
            await db.SaveChangesAsync(http.RequestAborted);
            return Results.Ok(ToDto(item));
        });

        api.MapDelete("/{id:guid}", async (Guid id, HttpContext http, CurrentUserService users, ChatDbContext db) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            await db.InventoryItems.Where(i => i.Id == id && i.OwnerId == me.Id).ExecuteDeleteAsync(http.RequestAborted);
            return Results.NoContent();
        });
    }

    static void Apply(InventoryItem item, SaveInventoryItemRequest request, DateTimeOffset now)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length is 0 or > InventoryContract.MaxNameLength)
            throw new ChatRejectedException($"Give the item a name of up to {InventoryContract.MaxNameLength} characters.");
        if (request.Quantity is < 0 or > 10_000_000)
            throw new ChatRejectedException("Quantity must be between 0 and 10,000,000.");

        item.Name = name;
        item.Sku = Trimmed(request.Sku, 64, "SKU");
        item.Location = Trimmed(request.Location, 120, "Location");
        item.Quantity = request.Quantity;
        item.UpdatedAt = now;
    }

    static string? Trimmed(string? value, int max, string field)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        return text.Length <= max ? text : throw new ChatRejectedException($"{field} is limited to {max} characters.");
    }

    static InventoryItemDto ToDto(InventoryItem i) => new(i.Id, i.Name, i.Sku, i.Location, i.Quantity, i.UpdatedAt);
}
