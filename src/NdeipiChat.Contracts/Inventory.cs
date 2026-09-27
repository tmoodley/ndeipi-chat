namespace NdeipiChat.Contracts;

/// <summary>
/// The Inventory sub-app: stock items a user keeps count of. It ships as its own Razor Class
/// Library (NdeipiChat.SubApps.Inventory), loaded by the shell only when opened -- the SRS's
/// "Dynamic Instantiation Test".
/// </summary>
public static class InventoryContract
{
    public const string AppId = "inventory";
    public const string ItemsPath = "api/inventory";
    public const int MaxNameLength = 120;
}

public sealed record InventoryItemDto(Guid Id, string Name, string? Sku, string? Location, int Quantity, DateTimeOffset UpdatedAt);

public sealed record SaveInventoryItemRequest(string Name, string? Sku, string? Location, int Quantity);
