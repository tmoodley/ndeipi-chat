using System.Globalization;
using System.Text.Json;
using NdeipiChat.Contracts;

namespace NdeipiChat.Client.Extensions;

/// <summary>A shop's receipt in a chat: who sold what, the totals, and how it was paid.</summary>
public sealed class PosReceiptMessageViewModel : MessageViewModel
{
    public PosReceiptMessageViewModel(MessageDto message, MessageRenderContext context, PosReceiptPayload receipt) : base(message, context)
    {
        Receipt = receipt;
        Lines = (receipt.Lines ?? []).Select(l => new PosReceiptRow($"{Quantity(l.Quantity)} × {l.Name}", Money(l.Total))).ToList();
        var totals = new List<PosReceiptRow> { new("Subtotal", Money(receipt.Subtotal)) };
        if (receipt.Discount > 0)
            totals.Add(new PosReceiptRow("Discount", "−" + Money(receipt.Discount)));
        totals.Add(new PosReceiptRow(receipt.TaxInclusive ? "Tax (included)" : "Tax", Money(receipt.Tax)));
        Totals = totals;
        Payments = (receipt.Payments ?? []).Select(p => new PosReceiptRow(p.Tender, Money(p.Amount)))
            .Concat(receipt.Change > 0 ? [new PosReceiptRow("Change", Money(receipt.Change))] : [])
            .ToList();
    }

    public PosReceiptPayload Receipt { get; }

    public string MerchantName => Receipt.MerchantName;
    public string StoreName => Receipt.StoreName;
    public string ReceiptNumber => Receipt.ReceiptNumber;
    public string WhenText => Receipt.OccurredAt.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture);
    public string TotalText => Money(Receipt.Total);
    public string CashierText => Receipt.CashierName.Length > 0 ? $"Served by {Receipt.CashierName}" : "";
    public bool HasCashier => Receipt.CashierName.Length > 0;

    public IReadOnlyList<PosReceiptRow> Lines { get; }
    public IReadOnlyList<PosReceiptRow> Totals { get; }
    public IReadOnlyList<PosReceiptRow> Payments { get; }

    public override string Preview => $"🧾 Receipt from {MerchantName} · {TotalText}";

    /// <summary>"$45.00" for USD; "45.00 ZWG" for anything else.</summary>
    public string Money(decimal amount) =>
        Receipt.Currency.Equals("USD", StringComparison.OrdinalIgnoreCase)
            ? "$" + amount.ToString("N2", CultureInfo.InvariantCulture)
            : $"{amount.ToString("N2", CultureInfo.InvariantCulture)} {Receipt.Currency.ToUpperInvariant()}";

    static string Quantity(decimal quantity) => quantity.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>One line of a receipt: a label and its amount.</summary>
public sealed record PosReceiptRow(string Label, string Value);

public sealed class PosReceiptRenderer : IMessageRenderer
{
    public string Kind => MessageKinds.PosReceipt;

    public MessageViewModel Create(MessageDto message, MessageRenderContext context) =>
        new PosReceiptMessageViewModel(message, context, ContractJson.Read<PosReceiptPayload>(message.Payload)
            ?? throw new JsonException("Empty receipt payload."));
}
