using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Pos;

/// <summary>
/// "pos.receipt" messages: a shop's receipt as a card in the customer's chat (FR-PAY-03). The till
/// sends only the sale's id. This fills in everything else from the recorded sale, and only for
/// someone who works at that store, so nobody can post a receipt that doesn't match a real sale.
/// </summary>
public sealed class PosReceiptHandler(ChatDbContext db) : IMessageKindHandler
{
    public string Kind => MessageKinds.PosReceipt;

    public async Task<PreparedMessage> PrepareAsync(MessageContext context, JsonElement payload, CancellationToken ct)
    {
        var saleId = MessagePayload.Read<PosReceiptPayload>(payload).SaleId;
        var sale = await db.PosSales.AsNoTracking().Include(s => s.Lines).Include(s => s.Payments).FirstOrDefaultAsync(s => s.Id == saleId, ct)
            ?? throw new ChatRejectedException("That sale doesn't exist.");
        var staff = await db.PosStaff.AsNoTracking().FirstOrDefaultAsync(s => s.MerchantId == sale.MerchantId && s.UserId == context.Sender.Id, ct);
        if (staff is null || (staff.StoreId is { } only && only != sale.StoreId))
            throw new ChatRejectedException("Only the shop that made a sale can send its receipt.");

        var store = await db.PosStores.AsNoTracking().FirstAsync(s => s.Id == sale.StoreId, ct);
        var merchant = await db.PosMerchants.AsNoTracking().FirstAsync(m => m.Id == sale.MerchantId, ct);
        var cashier = await db.PosStaff.Where(s => s.Id == sale.StaffId).Select(s => s.User.DisplayName).FirstOrDefaultAsync(ct) ?? "";
        var lines = sale.Lines.OrderBy(l => l.Position)
            .Select(l => new PosReceiptLine(l.Quantity, l.Name + (l.Variant.Length > 0 ? $" ({l.Variant})" : "") + (l.Modifiers is { Length: > 0 } m ? $" + {m}" : ""), l.Total))
            .ToList();
        var payments = sale.Payments.Select(p => new PosReceiptPayment(PosTenders.Label(p.Tender), p.Amount)).ToList();

        return new PreparedMessage(ContractJson.ToElement(new PosReceiptPayload(
            sale.Id,
            merchant.Name,
            store.Name,
            PosService.Receipt(store, sale.ReceiptNumber),
            sale.OccurredAt,
            merchant.Currency,
            lines,
            sale.Subtotal,
            sale.Discount,
            sale.Tax,
            merchant.TaxInclusive,
            sale.Total,
            payments,
            sale.Change,
            cashier)));
    }
}
