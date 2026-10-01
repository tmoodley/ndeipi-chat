using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NdeipiChat.Api.Banking;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Pos;

/// <summary>
/// Ndeipi Pay at the till (<see cref="PosPay"/>). The till asks for an amount and shows the
/// payment's QR code. A customer who scans it confirms, and the money goes from their Bridge
/// wallet to the merchant owner's, like a ticket purchase in Events. It counts as paid only once
/// Bridge confirms the transfer (<see cref="PosQrPaymentListener"/>). Only then will checkout
/// take it as a tender, for one sale.
/// </summary>
public sealed class PosPayService(ChatDbContext db, BankingService banking, IOptions<BridgeOptions> bridge, TimeProvider clock)
{
    DateTimeOffset Now => clock.GetUtcNow();

    // ---- The till's side ----

    public async Task<PosQrPaymentDto> StartAsync(Till till, StartQrPaymentRequest request, Uri site, CancellationToken ct)
    {
        var options = bridge.Value;
        if (!options.IsConfigured)
            throw new ChatRejectedException("Ndeipi Pay isn't set up on this server.");
        if (!PosRoles.CanSell(till.Staff.Role))
            throw new ChatRejectedException("Only staff who sell can take payments.");
        var currency = WalletCurrency(till.Merchant.Currency, options.Currency)
            ?? throw new ChatRejectedException($"Ndeipi Pay takes {options.Currency.ToUpperInvariant()}, and this shop sells in {till.Merchant.Currency}.");
        var amount = Math.Round(request.Amount, 2);
        if (amount <= 0 || amount != request.Amount)
            throw new ChatRejectedException("Enter an amount greater than zero, to the cent.");
        if (amount > options.MaxTransferAmount)
            throw new ChatRejectedException($"Ndeipi Pay takes at most {Amounts.Format(options.MaxTransferAmount)} at once.");
        if (await db.BankingProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == till.Merchant.OwnerId, ct) is not { CanTransfer: true })
            throw new ChatRejectedException("To take Ndeipi Pay, the shop's owner needs a verified wallet (Me > Wallet in Ndeipi).");

        var payment = new PosQrPayment
        {
            Id = Guid.NewGuid(),
            Code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_'),
            MerchantId = till.Merchant.Id,
            StoreId = till.Store.Id,
            StaffId = till.Staff.Id,
            RecipientId = till.Merchant.OwnerId,
            Amount = amount,
            Currency = currency,
            Status = PosQrStatuses.Waiting,
            CreatedAt = Now,
            ExpiresAt = Now.AddMinutes(PosPay.ExpiryMinutes),
            UpdatedAt = Now
        };
        db.PosQrPayments.Add(payment);
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(payment, site, ct);
    }

    public async Task<PosQrPaymentDto?> GetAsync(Till till, Guid id, Uri site, CancellationToken ct) =>
        await db.PosQrPayments.FirstOrDefaultAsync(p => p.Id == id && p.StoreId == till.Store.Id, ct) is { } payment
            ? await ToDtoAsync(await ExpireAsync(payment, ct), site, ct)
            : null;

    /// <summary>Takes the QR code off the till. Once someone has paid, it can't be: the sale needs completing (or refunding).</summary>
    public async Task<PosQrPaymentDto?> CancelAsync(Till till, Guid id, Uri site, CancellationToken ct)
    {
        await db.PosQrPayments.Where(p => p.Id == id && p.StoreId == till.Store.Id && p.Status == PosQrStatuses.Waiting)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PosQrStatuses.Cancelled).SetProperty(p => p.UpdatedAt, Now), ct);
        return await GetAsync(till, id, site, ct);
    }

    // ---- The customer's side ----

    public async Task<PayRequestDto?> RequestAsync(User me, string code, CancellationToken ct) =>
        await db.PosQrPayments.FirstOrDefaultAsync(p => p.Code == code, ct) is { } payment
            ? await ToRequestAsync(me, await ExpireAsync(payment, ct), ct)
            : null;

    /// <summary>The customer confirms: the payment is theirs, and their wallet pays the shop's.</summary>
    public async Task<PayRequestDto?> PayAsync(User me, string code, CancellationToken ct)
    {
        var payment = await db.PosQrPayments.AsNoTracking().FirstOrDefaultAsync(p => p.Code == code, ct);
        if (payment is null)
            return null;
        if (payment.PayerId == me.Id)
            return await RequestAsync(me, code, ct);
        if (payment.Status != PosQrStatuses.Waiting || payment.ExpiresAt <= Now)
            throw new ChatRejectedException(payment.Status == PosQrStatuses.Waiting || payment.Status == PosQrStatuses.Expired
                ? "This payment request has expired. Ask the shop to show a new code."
                : "This payment request has already been used.");
        if (payment.RecipientId == me.Id)
            throw new ChatRejectedException("You can't pay your own shop.");
        var profiles = await db.BankingProfiles.AsNoTracking().Where(p => p.UserId == me.Id || p.UserId == payment.RecipientId).ToListAsync(ct);
        if (profiles.FirstOrDefault(p => p.UserId == me.Id) is not { CanTransfer: true })
            throw new ChatRejectedException("Verify your identity under Me > Wallet before paying.");
        if (profiles.FirstOrDefault(p => p.UserId == payment.RecipientId) is not { CanTransfer: true })
            throw new ChatRejectedException("This shop can't take Ndeipi Pay right now. Pay another way.");

        var merchant = await db.PosMerchants.AsNoTracking().Where(m => m.Id == payment.MerchantId).Select(m => m.Name).FirstAsync(ct);
        var transfer = new BankTransfer
        {
            Id = Guid.NewGuid(),
            SenderId = me.Id,
            RecipientId = payment.RecipientId,
            Amount = payment.Amount,
            Currency = payment.Currency,
            Memo = $"Ndeipi Pay: {(merchant.Length > 60 ? merchant[..60] : merchant)}",
            CreatedAt = Now,
            UpdatedAt = Now
        };

        // Only one person can take a request, and only while it's still showing on the till.
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var claimed = await db.PosQrPayments
                .Where(p => p.Id == payment.Id && p.Status == PosQrStatuses.Waiting && p.ExpiresAt > Now)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Status, PosQrStatuses.Paying)
                    .SetProperty(p => p.PayerId, me.Id)
                    .SetProperty(p => p.BankTransferId, transfer.Id)
                    .SetProperty(p => p.UpdatedAt, Now), ct);
            if (claimed == 0)
                throw new ChatRejectedException("This payment request has already been used or has expired.");
            db.BankTransfers.Add(transfer);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        await banking.SubmitTransferAsync(transfer.Id, ct);
        return await RequestAsync(me, code, ct);
    }

    // ---- Shared ----

    /// <summary>What the wallet pays a shop's prices in: the same currency, or a US dollar stablecoin for USD prices.</summary>
    public static string? WalletCurrency(string merchantCurrency, string walletCurrency)
    {
        var wallet = walletCurrency.ToLowerInvariant();
        if (string.Equals(merchantCurrency, wallet, StringComparison.OrdinalIgnoreCase))
            return wallet;
        return string.Equals(merchantCurrency, "usd", StringComparison.OrdinalIgnoreCase) && wallet is "usdc" or "usdb" or "usdt" ? wallet : null;
    }

    async Task<PosQrPayment> ExpireAsync(PosQrPayment payment, CancellationToken ct)
    {
        if (payment.Status == PosQrStatuses.Waiting && payment.ExpiresAt <= Now)
        {
            await db.PosQrPayments.Where(p => p.Id == payment.Id && p.Status == PosQrStatuses.Waiting)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PosQrStatuses.Expired).SetProperty(p => p.UpdatedAt, Now), ct);
            payment.Status = PosQrStatuses.Expired;
        }
        return payment;
    }

    async Task<PosQrPaymentDto> ToDtoAsync(PosQrPayment p, Uri site, CancellationToken ct)
    {
        var payer = p.PayerId is { } id ? await db.Users.Where(u => u.Id == id).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) : null;
        return new PosQrPaymentDto(p.Id, p.Code, PosPay.PayUrl(site, p.Code), p.Amount, p.Currency, p.Status, p.Error, payer, p.SaleId, p.ExpiresAt);
    }

    async Task<PayRequestDto> ToRequestAsync(User me, PosQrPayment p, CancellationToken ct)
    {
        var merchant = await db.PosMerchants.AsNoTracking().Where(m => m.Id == p.MerchantId).Select(m => new { m.Name, m.OwnerId }).FirstAsync(ct);
        var store = await db.PosStores.AsNoTracking().Where(s => s.Id == p.StoreId).Select(s => s.Name).FirstAsync(ct);
        // Someone else's payment: they can see it's been used, but not how it went for the payer.
        var mine = p.PayerId == me.Id;
        return new PayRequestDto(p.Code, merchant.Name, store, p.Amount, p.Currency, p.Status, mine ? p.Error : null, p.ExpiresAt, mine, merchant.OwnerId);
    }
}

/// <summary>Moves a Ndeipi Pay request along as its Bridge transfer is confirmed or fails.</summary>
public sealed class PosQrPaymentListener(ChatDbContext db, TimeProvider clock) : IBankTransferListener
{
    public async Task TransferChangedAsync(BankTransfer transfer, CancellationToken ct)
    {
        var status = transfer.Status switch
        {
            TransferStatuses.Confirmed => PosQrStatuses.Paid,
            TransferStatuses.Failed => PosQrStatuses.Failed,
            _ => null
        };
        if (status is null)
            return;
        var error = status == PosQrStatuses.Failed ? transfer.Error ?? "The payment didn't go through." : null;
        await db.PosQrPayments.Where(p => p.BankTransferId == transfer.Id && p.Status == PosQrStatuses.Paying)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, status)
                .SetProperty(p => p.Error, error)
                .SetProperty(p => p.UpdatedAt, clock.GetUtcNow()), ct);
    }
}
