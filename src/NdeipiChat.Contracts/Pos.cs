namespace NdeipiChat.Contracts;

/// <summary>
/// The Point of Sale sub-app (SRS "Ndeipi Super App — Point of Sale (POS) Module"): a merchant's
/// stores, staff and catalogue; a till with PIN sign-in, shifts, a cart with tax and discounts,
/// split tenders and receipts; stock per store; and a tamper-evident audit trail.
/// </summary>
public static class PosContract
{
    public const string AppId = "pos";
    public const string BasePath = "api/pos";

    /// <summary>The header carrying a till session (after a staff PIN) on till operations.</summary>
    public const string TillHeader = "X-Pos-Till";

    public const int MinPinLength = 4;
    public const int MaxPinLength = 6;

    /// <summary>Wrong PINs at one store before it stops trying for <see cref="PinLockoutMinutes"/>.</summary>
    public const int MaxPinAttempts = 5;
    public const int PinLockoutMinutes = 5;

    /// <summary>FR-AUTH-02: the till locks after this long without a touch; the cart survives.</summary>
    public const int DefaultLockSeconds = 180;

    /// <summary>FR-PAY-02: a sale sent again with the same key within this window returns the first.</summary>
    public const int IdempotencyMinutes = 60;

    /// <summary>FR-OFF-03: an offline sale can be uploaded up to this long after it was rung up.</summary>
    public const int OfflineUploadHours = 72;

    public const int MaxCartLines = 200;
}

/// <summary>SRS §3. Admins run the merchant; managers a store; cashiers the till; auditors read.</summary>
public static class PosRoles
{
    public const string Admin = "admin";
    public const string Manager = "manager";
    public const string Cashier = "cashier";
    public const string Auditor = "auditor";

    public static readonly IReadOnlyList<string> All = [Admin, Manager, Cashier, Auditor];

    public static bool CanSell(string role) => role is Admin or Manager or Cashier;
    public static bool CanManage(string role) => role is Admin or Manager;
    public static bool CanAudit(string role) => role is Admin or Auditor;

    public static string Label(string role) => role switch
    {
        Admin => "Merchant admin",
        Manager => "Store manager",
        Cashier => "Cashier",
        Auditor => "Auditor",
        _ => role
    };
}

public static class PosTenders
{
    public const string Cash = "cash";

    /// <summary>A card on the merchant's own certified terminal: only its approval reference is kept (NFR-SEC-02).</summary>
    public const string Card = "card";

    /// <summary>A gift voucher or store credit, by its code.</summary>
    public const string Voucher = "voucher";

    /// <summary>
    /// Ndeipi Pay: the customer scans the till's QR code and pays from their Ndeipi wallet. Its
    /// reference is the <see cref="PosQrPaymentDto"/>'s id, and the server only accepts it once paid.
    /// </summary>
    public const string Qr = "qr";

    public static readonly IReadOnlyList<string> All = [Cash, Card, Voucher, Qr];

    /// <summary>Tenders that need the till to be online.</summary>
    public static bool NeedsConnection(string tender) => tender != Cash;

    public static string Label(string tender) => tender switch
    {
        Cash => "Cash",
        Card => "Card",
        Voucher => "Voucher",
        Qr => "Ndeipi Pay",
        _ => tender
    };
}

public static class PosSaleStatuses
{
    public const string Completed = "completed";
    public const string Voided = "voided";
    public const string Refunded = "refunded";
}

public static class PosDiscountKinds
{
    public const string Percent = "percent";
    public const string Amount = "amount";
}

// ---- Merchant, stores, staff ----

/// <param name="TaxInclusive">Prices include tax (VAT style) rather than having it added (sales-tax style).</param>
/// <param name="DiscountLimitPercent">Discounts above this share of a line or cart need a manager's PIN (FR-CAT-04).</param>
public sealed record PosMerchantDto(
    Guid Id,
    string Name,
    string Currency,
    bool TaxInclusive,
    decimal DiscountLimitPercent,
    int LockSeconds,
    IReadOnlyList<PosStoreDto> Stores,
    string MyRole,
    Guid MyStaffId,
    bool MyPinSet);

public sealed record PosStoreDto(Guid Id, string Name, string? Address, decimal TaxRatePercent, int ReceiptCount);

public sealed record CreateMerchantRequest(string Name, string Currency, bool TaxInclusive, string StoreName, decimal TaxRatePercent);

public sealed record SaveMerchantRequest(string Name, string Currency, bool TaxInclusive, decimal DiscountLimitPercent, int LockSeconds);

public sealed record SaveStoreRequest(string Name, string? Address, decimal TaxRatePercent);

/// <param name="StoreId">The one store they work at; null for all of them.</param>
public sealed record PosStaffDto(Guid Id, UserDto User, string Role, Guid? StoreId, bool PinSet, DateTimeOffset AddedAt);

public sealed record AddStaffRequest(string Email, string Role, Guid? StoreId);

public sealed record SaveStaffRequest(string Role, Guid? StoreId);

public sealed record SetPinRequest(string Pin);

// ---- Till sign-in ----

/// <param name="Terminal">This device's name, for the audit trail (NFR-SEC-03).</param>
public sealed record UnlockTillRequest(string Pin, string? Terminal = null);

/// <param name="Token">Sent as <see cref="PosContract.TillHeader"/> on till operations until the till locks.</param>
public sealed record TillSessionDto(string Token, Guid StoreId, Guid StaffId, string StaffName, string Role, DateTimeOffset ExpiresAt, PosShiftDto? Shift);

// ---- Catalogue ----

public sealed record PosCategoryDto(Guid Id, string Name, string Icon, string Tone, int Order);

public sealed record SaveCategoryRequest(string Name, string? Icon, string? Tone, int Order);

/// <summary>A size, colour or the like, with its own price and codes (FR-CAT-02).</summary>
public sealed record PosVariantDto(string Name, decimal Price, string? Sku, string? Barcode);

/// <summary>An add-on or alteration that changes the price, e.g. "Extra cheese +0.50".</summary>
public sealed record PosModifierDto(string Name, decimal Price);

/// <param name="TaxRatePercent">Null: the store's rate.</param>
/// <param name="TrackStock">
/// Whether the store counts this product. Counted products can't be sold past what's in stock;
/// uncounted ones (a meal, a service) have no stock at all.
/// </param>
/// <param name="Stock">At the store asked about: base product first, then each variant by name. Empty if not counted.</param>
public sealed record PosProductDto(
    Guid Id,
    Guid? CategoryId,
    string Name,
    string Icon,
    string? Sku,
    string? Barcode,
    decimal Price,
    decimal? TaxRatePercent,
    bool IsActive,
    int SafetyStock,
    IReadOnlyList<PosVariantDto> Variants,
    IReadOnlyList<PosModifierDto> Modifiers,
    IReadOnlyList<PosStockLevelDto> Stock,
    bool TrackStock = true)
{
    /// <summary>How many of the product (<paramref name="variant"/> "") or a variant are in stock; null if it isn't counted.</summary>
    public decimal? InStock(string variant) =>
        TrackStock ? Stock.FirstOrDefault(s => s.Variant == variant)?.Quantity ?? 0 : null;
}

public sealed record SaveProductRequest(
    Guid? CategoryId,
    string Name,
    string? Icon,
    string? Sku,
    string? Barcode,
    decimal Price,
    decimal? TaxRatePercent,
    bool IsActive,
    int SafetyStock,
    IReadOnlyList<PosVariantDto>? Variants,
    IReadOnlyList<PosModifierDto>? Modifiers,
    bool TrackStock = true);

/// <param name="Variant">"" for the product itself.</param>
/// <param name="IsOut">None left: the till won't sell it until more is stocked in.</param>
public sealed record PosStockLevelDto(string Variant, decimal Quantity, bool IsLow, bool IsOut);

/// <summary>Everything a till needs to sell offline: the catalogue with this store's stock.</summary>
public sealed record PosCatalogDto(IReadOnlyList<PosCategoryDto> Categories, IReadOnlyList<PosProductDto> Products, DateTimeOffset LoadedAt);

/// <summary>What a scanned or typed code matched: the product, and the variant if the code was a variant's.</summary>
public sealed record PosLookupDto(PosProductDto Product, string? Variant);

/// <summary>
/// Stock arriving or counted (FR-INV-03); a negative quantity takes stock out, but never more than
/// the store has.
/// </summary>
public sealed record StockAdjustRequest(Guid ProductId, string? Variant, decimal Quantity, string? Reason);

public sealed record PosStockAlertDto(Guid ProductId, string Name, string Variant, decimal Quantity, int SafetyStock, bool IsOut);

// ---- Shifts ----

public sealed record PosShiftDto(
    Guid Id,
    Guid StoreId,
    string CashierName,
    DateTimeOffset OpenedAt,
    decimal OpeningFloat,
    DateTimeOffset? ClosedAt,
    decimal? CountedCash,
    PosShiftTotalsDto Totals);

/// <summary>An X report while the shift is open; a Z report once it's closed (FR-AUTH-03).</summary>
/// <param name="ExpectedCash">Float, plus cash taken, less change, drops and cash refunds.</param>
/// <param name="Variance">Counted less expected, once counted.</param>
public sealed record PosShiftTotalsDto(
    int Sales,
    decimal Gross,
    decimal Discounts,
    decimal Tax,
    decimal Net,
    IReadOnlyDictionary<string, decimal> ByTender,
    int Voids,
    int Refunds,
    decimal Refunded,
    decimal Drops,
    decimal PaidIn,
    int NoSales,
    decimal ExpectedCash,
    decimal? Variance);

public sealed record OpenShiftRequest(decimal OpeningFloat);

public sealed record CloseShiftRequest(decimal CountedCash);

/// <param name="Kind">"drop" (cash out to the safe), "payin" (cash added), or "nosale" (drawer opened, no sale).</param>
public sealed record CashMovementRequest(string Kind, decimal Amount, string? Note);

// ---- Sales ----

public sealed record CartLineRequest(
    Guid ProductId,
    string? Variant,
    IReadOnlyList<string>? Modifiers,
    decimal Quantity,
    string? DiscountKind,
    decimal DiscountValue);

public sealed record TenderRequest(string Tender, decimal Amount, string? Reference);

/// <param name="ClientSaleId">The idempotency key (FR-PAY-02): made on the till, the same on every retry.</param>
/// <param name="OccurredAt">When it was rung up: for sales made offline and uploaded later (FR-OFF-03).</param>
/// <param name="ApprovalPin">A manager's PIN, when a discount is over the limit (FR-CAT-04).</param>
public sealed record CheckoutRequest(
    Guid ClientSaleId,
    IReadOnlyList<CartLineRequest> Lines,
    string? DiscountKind,
    decimal DiscountValue,
    IReadOnlyList<TenderRequest> Tenders,
    DateTimeOffset? OccurredAt,
    bool Offline,
    string? ApprovalPin);

/// <summary>A cart priced by the server: what the till shows before charging.</summary>
public sealed record PosQuoteDto(
    IReadOnlyList<PosSaleLineDto> Lines,
    decimal Subtotal,
    decimal Discount,
    decimal Tax,
    decimal Total,
    bool NeedsApproval,
    string? ApprovalReason);

public sealed record PosSaleLineDto(
    Guid ProductId,
    string Name,
    string Variant,
    IReadOnlyList<string> Modifiers,
    decimal Quantity,
    decimal UnitPrice,
    decimal Discount,
    decimal TaxRatePercent,
    decimal Tax,
    decimal Total);

public sealed record PosPaymentDto(string Tender, decimal Amount, decimal Change, string? Reference);

public sealed record PosSaleDto(
    Guid Id,
    Guid ClientSaleId,
    string ReceiptNumber,
    Guid StoreId,
    string StoreName,
    string MerchantName,
    string Currency,
    bool TaxInclusive,
    string CashierName,
    string Status,
    DateTimeOffset OccurredAt,
    bool Offline,
    IReadOnlyList<PosSaleLineDto> Lines,
    decimal Subtotal,
    decimal Discount,
    decimal Tax,
    decimal Total,
    IReadOnlyList<PosPaymentDto> Payments,
    decimal Change,
    string? StatusReason);

/// <param name="Reason">Why, for the audit trail.</param>
/// <param name="ApprovalPin">A manager's PIN: voids and refunds always need one (SRS §3).</param>
public sealed record ReverseSaleRequest(string Reason, string ApprovalPin);

/// <summary>Sends the receipt to a customer's Ndeipi chats (FR-PAY-03), by their account's email or phone.</summary>
public sealed record SendReceiptRequest(string EmailOrPhone);

// ---- Ndeipi Pay: paying at the till by scanning a QR code ----

/// <summary>
/// Ndeipi Pay. The till asks for a payment and shows its QR code. The customer scans it, which
/// opens <see cref="PayPath"/> in Ndeipi, and confirms. The money goes from their wallet to the
/// merchant owner's through Bridge, and the till completes the sale once Bridge confirms it.
/// </summary>
public static class PosPay
{
    /// <summary>The customer's side of the API: any signed-in Ndeipi user, not only POS staff.</summary>
    public const string BasePath = "api/pay";

    /// <summary>The page a QR code opens, relative to the Ndeipi site: "pay/{code}". "pay" alone scans one.</summary>
    public const string PayPath = "pay";

    /// <summary>How long a customer has to scan and confirm before the till has to ask again.</summary>
    public const int ExpiryMinutes = 10;

    /// <summary>The page a payment's QR code opens, for a site like "https://chat.ndeipi.com/".</summary>
    public static string PayUrl(Uri site, string code) => new Uri(site, $"{PayPath}/{code}").ToString();

    /// <summary>The code in a scanned pay link (or a code typed in), or null if it isn't one.</summary>
    public static string? CodeFrom(string? scanned)
    {
        var text = scanned?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            text = parts.Length >= 2 && parts[^2] == PayPath ? parts[^1] : null;
        }
        return text is { Length: >= 16 and <= 64 } && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? text : null;
    }
}

public static class PosQrStatuses
{
    /// <summary>Shown on the till, waiting for someone to scan and confirm.</summary>
    public const string Waiting = "waiting";

    /// <summary>Confirmed by the customer; the transfer is on its way.</summary>
    public const string Paying = "paying";

    /// <summary>The money arrived: the till can complete the sale with it.</summary>
    public const string Paid = "paid";

    public const string Failed = "failed";
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";

    public static bool IsFinal(string status) => status is Paid or Failed or Expired or Cancelled;
}

/// <summary>The till asks for <paramref name="Amount"/> by Ndeipi Pay.</summary>
public sealed record StartQrPaymentRequest(decimal Amount);

/// <param name="PayUrl">What the QR code holds.</param>
/// <param name="PayerName">Who confirmed it, once someone has.</param>
/// <param name="SaleId">The sale it paid for, once the till completed it.</param>
public sealed record PosQrPaymentDto(
    Guid Id,
    string Code,
    string PayUrl,
    decimal Amount,
    string Currency,
    string Status,
    string? Error,
    string? PayerName,
    Guid? SaleId,
    DateTimeOffset ExpiresAt);

/// <summary>A payment request as the customer sees it after scanning.</summary>
/// <param name="Currency">What their wallet pays in, e.g. "usdc".</param>
/// <param name="IsMine">Whether the signed-in user is the one who confirmed it.</param>
public sealed record PayRequestDto(
    string Code,
    string MerchantName,
    string StoreName,
    decimal Amount,
    string Currency,
    string Status,
    string? Error,
    DateTimeOffset ExpiresAt,
    bool IsMine);

// ---- Receipts in chat ----

/// <summary>
/// A "pos.receipt" message. The till only sends the sale's id; the server fills in the rest from
/// the recorded sale, so a receipt in a chat is always a real one.
/// </summary>
public sealed record PosReceiptPayload(
    Guid SaleId,
    string MerchantName = "",
    string StoreName = "",
    string ReceiptNumber = "",
    DateTimeOffset OccurredAt = default,
    string Currency = "",
    IReadOnlyList<PosReceiptLine>? Lines = null,
    decimal Subtotal = 0,
    decimal Discount = 0,
    decimal Tax = 0,
    bool TaxInclusive = false,
    decimal Total = 0,
    IReadOnlyList<PosReceiptPayment>? Payments = null,
    decimal Change = 0,
    string CashierName = "");

/// <param name="Name">With its variant and modifiers, e.g. "Burger (Double) + Cheese".</param>
public sealed record PosReceiptLine(decimal Quantity, string Name, decimal Total);

public sealed record PosReceiptPayment(string Tender, decimal Amount);

// ---- Audit ----

/// <summary>One entry in the tamper-evident trail (NFR-SEC-03): each carries the hash of the one before.</summary>
public sealed record PosAuditDto(long Sequence, DateTimeOffset At, string Action, string StaffName, string? StoreName, string Terminal, string Details, string Hash);

/// <param name="BrokenAt">The first entry whose hash doesn't match its contents and the one before; null if the chain holds.</param>
public sealed record PosAuditCheckDto(int Entries, bool Intact, long? BrokenAt);

/// <summary>Today at a store, for the manager's dashboard.</summary>
public sealed record PosDashboardDto(PosShiftTotalsDto Today, IReadOnlyList<PosShiftDto> OpenShifts, IReadOnlyList<PosStockAlertDto> StockAlerts, IReadOnlyList<PosSaleDto> RecentSales);

/// <summary>
/// How a cart is priced (FR-CAT-02/03/04), shared by the server and the till, so a sale rung up
/// offline comes to exactly what the server will record: variants and modifiers, line then cart
/// discounts (the cart's shared across lines by what's left of each), and tax per line, inside the
/// price or on top of it. Throws <see cref="PosPricingException"/> for a cart that can't be sold.
/// </summary>
public static class PosPricing
{
    public static PosQuoteDto Quote(
        IReadOnlyList<(PosProductDto Product, CartLineRequest Line)> cart,
        string? discountKind,
        decimal discountValue,
        bool taxInclusive,
        decimal storeTaxRate,
        decimal discountLimitPercent)
    {
        if (cart.Count == 0)
            throw new PosPricingException("The cart is empty.");
        if (cart.Count > PosContract.MaxCartLines)
            throw new PosPricingException($"A sale can have up to {PosContract.MaxCartLines} lines.");

        string? approvalReason = null;
        var drafts = new List<(PosProductDto Product, string Variant, List<string> Modifiers, decimal Quantity, decimal Unit, decimal Gross, decimal LineDiscount)>();
        foreach (var (product, line) in cart)
        {
            if (!product.IsActive)
                throw new PosPricingException($"{product.Name} isn't for sale any more.");
            if (line.Quantity <= 0 || line.Quantity > 10_000 || decimal.Round(line.Quantity, 3) != line.Quantity)
                throw new PosPricingException($"Check the quantity of {product.Name}.");
            var variant = line.Variant ?? "";
            var basePrice = product.Price;
            if (variant.Length > 0)
                basePrice = (product.Variants.FirstOrDefault(v => v.Name == variant) ?? throw new PosPricingException($"{product.Name} has no {variant}.")).Price;
            var chosen = (line.Modifiers ?? []).Distinct().ToList();
            var extras = chosen.Sum(name => (product.Modifiers.FirstOrDefault(m => m.Name == name) ?? throw new PosPricingException($"{product.Name} has no \"{name}\" option.")).Price);
            var unit = basePrice + extras;
            var gross = Money(unit * line.Quantity);
            var lineDiscount = Discount(line.DiscountKind, line.DiscountValue, gross, product.Name);
            if (gross > 0 && lineDiscount * 100 / gross > discountLimitPercent)
                approvalReason ??= $"The discount on {product.Name} is over {discountLimitPercent:0.##}%.";
            drafts.Add((product, variant, chosen, line.Quantity, unit, gross, lineDiscount));
        }

        var net = drafts.Sum(d => d.Gross - d.LineDiscount);
        var cartDiscount = Discount(discountKind, discountValue, net, "the cart");
        if (net > 0 && cartDiscount * 100 / net > discountLimitPercent)
            approvalReason ??= $"The cart discount is over {discountLimitPercent:0.##}%.";
        var shares = Share(cartDiscount, drafts.Select(d => d.Gross - d.LineDiscount).ToList());

        var lines = new List<PosSaleLineDto>();
        for (var i = 0; i < drafts.Count; i++)
        {
            var d = drafts[i];
            var discount = d.LineDiscount + shares[i];
            var taxable = d.Gross - discount;
            var rate = d.Product.TaxRatePercent ?? storeTaxRate;
            var tax = taxInclusive ? Money(taxable - taxable / (1 + rate / 100)) : Money(taxable * rate / 100);
            lines.Add(new PosSaleLineDto(d.Product.Id, d.Product.Name, d.Variant, d.Modifiers, d.Quantity, d.Unit, discount, rate, tax, taxInclusive ? taxable : taxable + tax));
        }
        return new PosQuoteDto(lines, drafts.Sum(d => d.Gross), lines.Sum(l => l.Discount), lines.Sum(l => l.Tax), lines.Sum(l => l.Total), approvalReason is not null, approvalReason);
    }

    public static decimal Money(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    static decimal Discount(string? kind, decimal value, decimal of, string what)
    {
        if (string.IsNullOrEmpty(kind) || value == 0)
            return 0;
        if (value < 0)
            throw new PosPricingException($"A discount on {what} can't be negative.");
        return kind switch
        {
            PosDiscountKinds.Percent when value <= 100 => Money(of * value / 100),
            PosDiscountKinds.Percent => throw new PosPricingException($"A discount on {what} can't be over 100%."),
            PosDiscountKinds.Amount => Math.Min(Money(value), of),
            _ => throw new PosPricingException("A discount is a percentage or an amount.")
        };
    }

    /// <summary>Splits an amount across weights to the cent, the last taking the rounding.</summary>
    static List<decimal> Share(decimal amount, List<decimal> weights)
    {
        var total = weights.Sum();
        var shares = weights.Select(w => total == 0 ? 0 : Money(amount * w / total)).ToList();
        if (shares.Count > 0)
            shares[^1] += amount - shares.Sum();
        return shares;
    }
}

public sealed class PosPricingException(string message) : Exception(message);
