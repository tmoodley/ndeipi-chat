using NdeipiChat.Contracts;

namespace NdeipiChat.SubApps.Pos;

public enum PosView
{
    Loading,
    Setup,
    Pick,
    Locked,
    Register,
    Shift,
    BackOffice,
    Account
}

/// <summary>A line in the till's cart.</summary>
public sealed class CartItem
{
    public Guid Key { get; } = Guid.NewGuid();
    public required PosProductDto Product { get; init; }
    public string Variant { get; set; } = "";
    public List<string> Modifiers { get; set; } = [];
    public decimal Quantity { get; set; } = 1;
    public string? DiscountKind { get; set; }
    public decimal DiscountValue { get; set; }

    public string Name => Product.Name + (Variant.Length > 0 ? $" ({Variant})" : "") + (Modifiers.Count > 0 ? $" + {string.Join(", ", Modifiers)}" : "");

    public CartLineRequest ToRequest() => new(Product.Id, Variant.Length > 0 ? Variant : null, Modifiers, Quantity, DiscountKind, DiscountValue);
}

/// <summary>A cash sale made offline, with the till session it was rung up in; <see cref="Problem"/> once the server refused it.</summary>
public sealed record QueuedSale(string Token, CheckoutRequest Request, string? Problem);
