using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Banking;

/// <summary>
/// Bridge banking: identity verification through Bridge's hosted KYC link, a custodial Bridge
/// wallet per verified user, and stablecoin transfers between users' wallets.
/// </summary>
public sealed class BankingService(
    ChatDbContext db,
    BridgeClient bridge,
    IOptions<BridgeOptions> options,
    ChatNotifier notifier,
    MessageStateService messageState,
    TimeProvider clock,
    ILogger<BankingService> log)
{
    public const string KycLinkCategory = "kyc_link";
    public const string CustomerCategory = "customer";
    public const string TransferCategory = "transfer";

    public async Task<BankingStatusDto> GetStatusAsync(Guid userId, CancellationToken ct) =>
        ToDto(await db.BankingProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, ct));

    BankingStatusDto ToDto(BankingProfile? p) => p is null
        ? new BankingStatusDto(BankingProfile.None, BankingProfile.None, false, null, null, options.Value.Currency)
        : new BankingStatusDto(p.KycStatus, p.TosStatus, p.CanTransfer, p.WalletChain, p.WalletAddress, options.Value.Currency);

    /// <summary>Returns the user's Bridge terms and KYC links, creating them on first use.</summary>
    public async Task<KycLinkDto> StartKycAsync(User user, CancellationToken ct)
    {
        if (!options.Value.IsConfigured)
            throw new ChatRejectedException("Banking isn't set up on this server.");

        var profile = await db.BankingProfiles.FirstOrDefaultAsync(p => p.UserId == user.Id, ct);
        if (profile is null)
        {
            profile = new BankingProfile { UserId = user.Id };
            db.BankingProfiles.Add(profile);
        }

        BridgeKycLink link;
        if (profile.KycLinkId is null)
        {
            if (string.IsNullOrWhiteSpace(user.Email))
                throw new ChatRejectedException("Add an email address to your account before verifying your identity.");
            link = await bridge.CreateKycLinkAsync(
                new BridgeKycLinkRequest(user.DisplayName, user.Email, "individual", options.Value.KycRedirectUri),
                idempotencyKey: $"kyc-link-{user.Id}",
                ct);
        }
        else
        {
            link = await bridge.GetKycLinkAsync(profile.KycLinkId, ct);
        }

        await ApplyKycLinkAsync(profile, link, ct);
        return new KycLinkDto(profile.KycUrl ?? "", profile.TosUrl ?? "", ToDto(profile));
    }

    /// <summary>Re-reads KYC progress from Bridge -- for when a webhook hasn't arrived (or can't, in development).</summary>
    public async Task<BankingStatusDto> RefreshAsync(Guid userId, CancellationToken ct)
    {
        var profile = await db.BankingProfiles.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (profile?.KycLinkId is null || !options.Value.IsConfigured)
            return ToDto(profile);

        await ApplyKycLinkAsync(profile, await bridge.GetKycLinkAsync(profile.KycLinkId, ct), ct);
        return ToDto(profile);
    }

    public async Task<IReadOnlyList<BalanceDto>> GetBalancesAsync(Guid userId, CancellationToken ct)
    {
        var profile = await db.BankingProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (profile?.WalletId is null || profile.BridgeCustomerId is null)
            return [];

        var wallet = await bridge.GetWalletAsync(profile.BridgeCustomerId, profile.WalletId, ct);
        return (wallet.Balances ?? [])
            .Select(b => new BalanceDto(b.Currency ?? "", b.Balance ?? "0", b.Chain ?? wallet.Chain ?? ""))
            .ToList();
    }

    /// <summary>Sends a queued transfer to Bridge. Safe to repeat: Bridge sees the same idempotency key every time.</summary>
    public async Task SubmitTransferAsync(Guid transferId, CancellationToken ct)
    {
        var transfer = await db.BankTransfers.FirstOrDefaultAsync(t => t.Id == transferId, ct);
        if (transfer is null || transfer.BridgeTransferId is not null || TransferStatuses.IsFinal(transfer.Status))
            return;

        var profiles = await db.BankingProfiles.AsNoTracking()
            .Where(p => p.UserId == transfer.SenderId || p.UserId == transfer.RecipientId)
            .ToListAsync(ct);
        var from = profiles.FirstOrDefault(p => p.UserId == transfer.SenderId);
        var to = profiles.FirstOrDefault(p => p.UserId == transfer.RecipientId);
        if (from is not { CanTransfer: true } || to is not { CanTransfer: true })
        {
            await SetTransferAsync(transfer, TransferStatuses.Failed, transfer.ProviderState, "Both people need a verified account to send money.", ct);
            return;
        }

        try
        {
            var result = await bridge.CreateTransferAsync(
                new BridgeTransferRequest(
                    Amounts.Format(transfer.Amount),
                    OnBehalfOf: from.BridgeCustomerId!,
                    Source: new BridgeTransferEndpoint("bridge_wallet", transfer.Currency, BridgeWalletId: from.WalletId),
                    Destination: new BridgeTransferEndpoint(to.WalletChain!, transfer.Currency, ToAddress: to.WalletAddress)),
                idempotencyKey: transfer.Id.ToString(),
                ct);

            transfer.BridgeTransferId = result.Id;
            await SetTransferAsync(transfer, MapState(result.State), result.State, null, ct);
        }
        catch (BridgeApiException ex) when (ex.IsClientError)
        {
            log.LogWarning("Bridge refused transfer {TransferId}: {Status} {Body}", transfer.Id, (int)ex.StatusCode, ex.Body);
            await SetTransferAsync(transfer, TransferStatuses.Failed, transfer.ProviderState, "The bank declined this transfer.", ct);
        }
        catch (Exception ex) when (ex is BridgeApiException or HttpRequestException or TaskCanceledException)
        {
            // Outcome unknown. The transfer stays pending and the poller resubmits it with the same
            // idempotency key, so Bridge can't create it twice.
            log.LogWarning(ex, "Bridge didn't confirm transfer {TransferId}; will retry", transfer.Id);
        }
    }

    public async Task SyncTransferAsync(Guid transferId, CancellationToken ct)
    {
        var transfer = await db.BankTransfers.FirstOrDefaultAsync(t => t.Id == transferId, ct);
        if (transfer?.BridgeTransferId is null || TransferStatuses.IsFinal(transfer.Status))
            return;

        var result = await bridge.GetTransferAsync(transfer.BridgeTransferId, ct);
        await SetTransferAsync(transfer, MapState(result.State), result.State, null, ct);
    }

    public async Task HandleWebhookAsync(BridgeWebhookEvent evt, CancellationToken ct)
    {
        if (evt.EventId is null || await db.WebhookEvents.AnyAsync(e => e.EventId == evt.EventId, ct))
            return;

        var eventObject = evt.EventObject is { ValueKind: JsonValueKind.Object } obj ? obj : (JsonElement?)null;
        switch (evt.EventCategory)
        {
            case KycLinkCategory:
            {
                var profile = await db.BankingProfiles.FirstOrDefaultAsync(p => p.KycLinkId == evt.EventObjectId, ct);
                if (profile is not null && eventObject?.Deserialize<BridgeKycLink>(BridgeClient.Json) is { } link)
                    await ApplyKycLinkAsync(profile, link, ct);
                break;
            }
            case CustomerCategory:
            {
                // Customer events don't carry the KYC link, so read it fresh.
                var profile = await db.BankingProfiles.FirstOrDefaultAsync(p => p.BridgeCustomerId == evt.EventObjectId, ct);
                if (profile?.KycLinkId is not null)
                    await ApplyKycLinkAsync(profile, await bridge.GetKycLinkAsync(profile.KycLinkId, ct), ct);
                break;
            }
            case TransferCategory:
            {
                var transfer = await db.BankTransfers.FirstOrDefaultAsync(t => t.BridgeTransferId == evt.EventObjectId, ct);
                var state = eventObject?.TryGetProperty("state", out var s) == true ? s.GetString() : evt.EventObjectStatus;
                if (transfer is not null && state is not null)
                    await SetTransferAsync(transfer, MapState(state), state, null, ct);
                break;
            }
        }

        db.WebhookEvents.Add(new ProcessedWebhookEvent { EventId = evt.EventId, ReceivedAt = clock.GetUtcNow() });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A redelivery of the same event finished first; its work is already done.
        }
    }

    /// <summary>Bridge's transfer states, folded into the three the chat shows.</summary>
    public static string MapState(string? state) => state switch
    {
        "payment_processed" => TransferStatuses.Confirmed,
        "canceled" or "error" or "returned" or "refunded" or "undeliverable" => TransferStatuses.Failed,
        _ => TransferStatuses.Processing
    };

    async Task ApplyKycLinkAsync(BankingProfile profile, BridgeKycLink link, CancellationToken ct)
    {
        var before = ToDto(profile);

        profile.KycLinkId = string.IsNullOrEmpty(link.Id) ? profile.KycLinkId : link.Id;
        profile.KycUrl = link.KycLink ?? profile.KycUrl;
        profile.TosUrl = link.TosLink ?? profile.TosUrl;
        profile.KycStatus = link.KycStatus ?? profile.KycStatus;
        profile.TosStatus = link.TosStatus ?? profile.TosStatus;
        profile.BridgeCustomerId = link.CustomerId ?? profile.BridgeCustomerId;
        profile.UpdatedAt = clock.GetUtcNow();

        if (profile is { KycStatus: BankingProfile.Approved, TosStatus: BankingProfile.Approved, WalletId: null, BridgeCustomerId: not null })
        {
            var chain = options.Value.WalletChain;
            try
            {
                var wallet = await bridge.CreateWalletAsync(profile.BridgeCustomerId, chain, $"wallet-{profile.UserId}-{chain}", ct);
                profile.WalletId = wallet.Id;
                profile.WalletChain = wallet.Chain ?? chain;
                profile.WalletAddress = wallet.Address;
            }
            catch (Exception ex) when (ex is BridgeApiException or HttpRequestException)
            {
                log.LogWarning(ex, "Couldn't create a Bridge wallet for user {UserId}; will retry on the next refresh", profile.UserId);
            }
        }

        await db.SaveChangesAsync(ct);

        var after = ToDto(profile);
        if (after != before)
        {
            var clerkId = await db.Users.Where(u => u.Id == profile.UserId).Select(u => u.ClerkUserId).FirstAsync(ct);
            await notifier.ToUser(clerkId).BankingStatusChanged(after);
        }
    }

    async Task SetTransferAsync(BankTransfer transfer, string status, string? providerState, string? error, CancellationToken ct)
    {
        var changed = transfer.Status != status || transfer.ProviderState != providerState || transfer.Error != error;
        transfer.Status = status;
        transfer.ProviderState = providerState;
        transfer.Error = error;
        transfer.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        if (changed)
            await messageState.SetAsync(transfer.MessageId, new BankTransferState(transfer.Id, status, providerState, error), ct);
    }
}
