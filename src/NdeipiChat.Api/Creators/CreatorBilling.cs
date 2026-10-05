using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NdeipiChat.Api.Banking;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Creators;

/// <summary>
/// Paying creators (FR-CR-04..07, 11, 12): subscribing and unlocking, renewals with a grace period,
/// and the creator's earnings. Every payment is a Bridge transfer from the payer's Ndeipi wallet to
/// the creator's, with the platform fee kept by Bridge as its developer fee: one transfer, split.
/// Access starts as soon as the payment is on its way and is taken back if it fails.
/// </summary>
public sealed class CreatorBilling(
    ChatDbContext db,
    BankingService banking,
    CreatorService creators,
    IOptions<CreatorsOptions> options,
    IOptions<BridgeOptions> bridge,
    TimeProvider clock,
    ILogger<CreatorBilling> log)
{
    DateTimeOffset Now => clock.GetUtcNow();

    static DateTimeOffset Advance(DateTimeOffset from, string period) =>
        period == BillingPeriods.Annual ? from.AddYears(1) : from.AddMonths(1);

    decimal FeeOf(decimal amount) => Math.Round(amount * options.Value.FeePercent / 100m, 2, MidpointRounding.AwayFromZero);

    async Task EnsureWalletsAsync(Guid payerId, Guid creatorId, CancellationToken ct)
    {
        if (!bridge.Value.IsConfigured)
            throw new ChatRejectedException("Payments aren't set up on this server.");
        var profiles = await db.BankingProfiles.AsNoTracking().Where(p => p.UserId == payerId || p.UserId == creatorId).ToListAsync(ct);
        if (profiles.FirstOrDefault(p => p.UserId == payerId) is not { CanTransfer: true })
            throw new ChatRejectedException("Verify your identity under Me > Wallet before paying a creator.");
        if (profiles.FirstOrDefault(p => p.UserId == creatorId) is not { CanTransfer: true })
            throw new ChatRejectedException("This creator can't take payments right now: their wallet isn't ready.");
    }

    /// <summary>Creates the transfer and its ledger row, ready to submit.</summary>
    CreatorPayment NewPayment(string kind, Guid payerId, Guid creatorId, decimal amount, string memo, Guid? subscriptionId, Guid? postId, DateTimeOffset? periodEnd)
    {
        var fee = FeeOf(amount);
        var transfer = new BankTransfer
        {
            Id = Guid.NewGuid(),
            SenderId = payerId,
            RecipientId = creatorId,
            Amount = amount,
            Currency = bridge.Value.Currency,
            Memo = memo.Length > 280 ? memo[..280] : memo,
            FeeAmount = fee,
            CreatedAt = Now,
            UpdatedAt = Now
        };
        db.BankTransfers.Add(transfer);
        var payment = new CreatorPayment
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            PayerId = payerId,
            CreatorId = creatorId,
            SubscriptionId = subscriptionId,
            PostId = postId,
            Amount = amount,
            Fee = fee,
            FeePercent = options.Value.FeePercent,
            Currency = transfer.Currency,
            BankTransferId = transfer.Id,
            PeriodEnd = periodEnd,
            CreatedAt = Now
        };
        db.CreatorPayments.Add(payment);
        return payment;
    }

    // ---- Subscribing (FR-CR-04, 06) ----

    public async Task<CreatorSubscriptionDto> SubscribeAsync(User user, Guid creatorId, CreatorSubscribeRequest request, CancellationToken ct)
    {
        if (creatorId == user.Id)
            throw new ChatRejectedException("You can't subscribe to yourself.");
        var tier = await db.CreatorTiers.AsNoTracking().FirstOrDefaultAsync(t => t.Id == request.TierId && t.CreatorId == creatorId && t.Active, ct)
            ?? throw new ChatRejectedException("That tier isn't on offer.");
        var price = request.Period switch
        {
            BillingPeriods.Monthly => tier.MonthlyPrice,
            BillingPeriods.Annual => tier.AnnualPrice ?? throw new ChatRejectedException("This tier is monthly only."),
            _ => throw new ChatRejectedException("Choose monthly or annual.")
        };
        await EnsureWalletsAsync(user.Id, creatorId, ct);

        // Changing tier: the old subscription ends now and the new one starts in full (no proration).
        var current = await db.CreatorSubscriptions
            .Where(s => s.SubscriberId == user.Id && s.CreatorId == creatorId && (s.Status == SubscriptionStatuses.Active || s.Status == SubscriptionStatuses.Grace))
            .ToListAsync(ct);
        if (current.Any(s => s.TierId == tier.Id && s.Period == request.Period && !s.CancelAtPeriodEnd))
            throw new ChatRejectedException("You're already subscribed to this tier.");
        foreach (var old in current)
            (old.Status, old.UpdatedAt, old.NextAttemptAt) = (SubscriptionStatuses.Ended, Now, null);

        var name = await db.CreatorProfiles.Where(c => c.UserId == creatorId).Select(c => c.Name).FirstAsync(ct);
        var subscription = new CreatorSubscription
        {
            Id = Guid.NewGuid(),
            SubscriberId = user.Id,
            CreatorId = creatorId,
            TierId = tier.Id,
            Period = request.Period,
            Price = price,
            Status = SubscriptionStatuses.Active,
            StartedAt = Now,
            CurrentPeriodEnd = Advance(Now, request.Period),
            ShareProfile = request.ShareProfile,
            UpdatedAt = Now
        };
        subscription.NextAttemptAt = subscription.CurrentPeriodEnd;
        db.CreatorSubscriptions.Add(subscription);
        var payment = NewPayment(CreatorPaymentKinds.Subscription, user.Id, creatorId, price,
            $"{name}: {tier.Name} ({request.Period})", subscription.Id, null, subscription.CurrentPeriodEnd);
        await db.SaveChangesAsync(ct);

        await banking.SubmitTransferAsync(payment.BankTransferId, ct);
        var fresh = await db.CreatorSubscriptions.AsNoTracking().FirstAsync(s => s.Id == subscription.Id, ct);
        return (await creators.SubscriptionDtosAsync([fresh], ct))[0];
    }

    /// <summary>Stops it renewing; access lasts to the end of the paid period. In grace, it ends now.</summary>
    public async Task<CreatorSubscriptionDto?> CancelAsync(User user, Guid id, CancellationToken ct)
    {
        var s = await db.CreatorSubscriptions.FirstOrDefaultAsync(x => x.Id == id && x.SubscriberId == user.Id, ct);
        if (s is null)
            return null;
        if (s.Status == SubscriptionStatuses.Grace)
            (s.Status, s.NextAttemptAt) = (SubscriptionStatuses.Ended, null);
        else if (s.Status == SubscriptionStatuses.Active)
            s.CancelAtPeriodEnd = true;
        s.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        return (await creators.SubscriptionDtosAsync([s], ct))[0];
    }

    public async Task<CreatorSubscriptionDto?> ResumeAsync(User user, Guid id, CancellationToken ct)
    {
        var s = await db.CreatorSubscriptions.FirstOrDefaultAsync(x => x.Id == id && x.SubscriberId == user.Id, ct);
        if (s is null)
            return null;
        if (s.Status != SubscriptionStatuses.Active)
            throw new ChatRejectedException("This subscription has ended. Subscribe again from the creator's page.");
        (s.CancelAtPeriodEnd, s.UpdatedAt) = (false, Now);
        await db.SaveChangesAsync(ct);
        return (await creators.SubscriptionDtosAsync([s], ct))[0];
    }

    // ---- Pay-per-view (FR-CR-05) ----

    public async Task<CreatorPostDto?> UnlockAsync(User user, Guid postId, CancellationToken ct)
    {
        var post = await db.CreatorPosts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == postId && !p.Deleted && p.PublishAt <= Now, ct);
        if (post is null)
            return null;
        if (post.Access != PostAccess.PayPerView || post.Price is not { } price)
            throw new ChatRejectedException("This post isn't pay-per-view.");
        if (post.CreatorId == user.Id)
            throw new ChatRejectedException("It's your own post.");
        if (await db.CreatorUnlocks.AnyAsync(u => u.PostId == postId && u.UserId == user.Id, ct))
            return await creators.PostDtoAsync(user, post, ct);
        await EnsureWalletsAsync(user.Id, post.CreatorId, ct);

        var name = await db.CreatorProfiles.Where(c => c.UserId == post.CreatorId).Select(c => c.Name).FirstAsync(ct);
        var payment = NewPayment(CreatorPaymentKinds.Unlock, user.Id, post.CreatorId, price, $"{name}: {post.Title}", null, post.Id, null);
        db.CreatorUnlocks.Add(new CreatorUnlock { PostId = post.Id, UserId = user.Id, PaymentId = payment.Id, CreatedAt = Now });
        await db.SaveChangesAsync(ct);

        await banking.SubmitTransferAsync(payment.BankTransferId, ct);
        return await creators.PostDtoAsync(user, post, ct);
    }

    // ---- Renewals and grace (FR-CR-06, 07) ----

    /// <summary>
    /// Renews subscriptions that are due, ends cancelled ones at their period's end, and ends those
    /// still unpaid after the grace period. Run on a timer (<see cref="CreatorsWorker"/>).
    /// </summary>
    public async Task<int> RenewDueAsync(CancellationToken ct)
    {
        var now = Now;
        var due = await db.CreatorSubscriptions
            .Where(s => (s.Status == SubscriptionStatuses.Active || s.Status == SubscriptionStatuses.Grace) && s.NextAttemptAt != null && s.NextAttemptAt <= now)
            .Take(200).ToListAsync(ct);
        var renewed = 0;
        foreach (var s in due)
        {
            if (s.CancelAtPeriodEnd || (s.Status == SubscriptionStatuses.Grace && s.GraceUntil <= now))
            {
                (s.Status, s.NextAttemptAt, s.UpdatedAt) = (SubscriptionStatuses.Ended, null, now);
                await db.SaveChangesAsync(ct);
                continue;
            }
            // Not twice for the same period: a renewal still on its way is left to finish.
            if (await db.CreatorPayments.AnyAsync(p => p.SubscriptionId == s.Id && p.Kind == CreatorPaymentKinds.Renewal
                    && (p.Status == TransferStatuses.Pending || p.Status == TransferStatuses.Processing), ct))
                continue;

            var name = await db.CreatorProfiles.Where(c => c.UserId == s.CreatorId).Select(c => c.Name).FirstAsync(ct);
            var tier = await db.CreatorTiers.Where(t => t.Id == s.TierId).Select(t => t.Name).FirstAsync(ct);
            var payment = NewPayment(CreatorPaymentKinds.Renewal, s.SubscriberId, s.CreatorId, s.Price,
                $"{name}: {tier} renewal", s.Id, null, Advance(s.CurrentPeriodEnd, s.Period));
            (s.NextAttemptAt, s.UpdatedAt) = (null, now);
            await db.SaveChangesAsync(ct);
            try
            {
                await banking.SubmitTransferAsync(payment.BankTransferId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Couldn't submit renewal {PaymentId}; the transfer poller retries it", payment.Id);
            }
            renewed++;
        }
        return renewed;
    }

    // ---- Earnings (FR-CR-12) ----

    public async Task<CreatorEarningsDto> EarningsAsync(User user, CancellationToken ct)
    {
        if (!await db.CreatorProfiles.AnyAsync(c => c.UserId == user.Id, ct))
            throw new ChatRejectedException("Set up your creator page first.");
        var confirmed = await db.CreatorPayments.AsNoTracking()
            .Where(p => p.CreatorId == user.Id && p.Status == TransferStatuses.Confirmed).ToListAsync(ct);
        var monthStart = new DateTimeOffset(Now.Year, Now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var month = confirmed.Where(p => p.CreatedAt >= monthStart).ToList();
        var active = await db.CreatorSubscriptions.AsNoTracking()
            .Where(s => s.CreatorId == user.Id && (s.Status == SubscriptionStatuses.Active || s.Status == SubscriptionStatuses.Grace)).ToListAsync(ct);
        var mrr = active.Where(s => !s.CancelAtPeriodEnd).Sum(s => s.Period == BillingPeriods.Annual ? s.Price / 12m : s.Price) * (1 - options.Value.FeePercent / 100m);
        var (balance, onHold, withdrawable) = await WithdrawableAsync(user.Id, ct);

        var recent = await db.CreatorPayments.AsNoTracking().Where(p => p.CreatorId == user.Id).OrderByDescending(p => p.CreatedAt).Take(20).ToListAsync(ct);
        var shared = await db.CreatorSubscriptions.AsNoTracking().Where(s => s.CreatorId == user.Id && s.ShareProfile).Select(s => s.SubscriberId).Distinct().ToListAsync(ct);
        var payerIds = recent.Select(p => p.PayerId).Where(shared.Contains).Distinct().ToList();
        var payers = await db.Users.AsNoTracking().Where(u => payerIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var postIds = recent.Where(p => p.PostId != null).Select(p => p.PostId!.Value).ToList();
        var titles = await db.CreatorPosts.AsNoTracking().Where(p => postIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Title, ct);

        return new CreatorEarningsDto(
            month.Sum(p => p.Amount), month.Sum(p => p.Fee), month.Sum(p => p.Amount - p.Fee),
            confirmed.Sum(p => p.Amount), confirmed.Sum(p => p.Amount - p.Fee),
            Math.Round(mrr, 2), active.Count, balance, onHold, withdrawable, options.Value.FeePercent,
            recent.Select(p => new CreatorPaymentDto(p.Id, p.Kind, payers.GetValueOrDefault(p.PayerId), p.Amount, p.Fee, p.Amount - p.Fee, p.Status, p.CreatedAt,
                p.PostId is { } post ? titles.GetValueOrDefault(post) : null)).ToList());
    }

    /// <summary>
    /// FR-CR-13: the wallet balance, the earnings still on hold (confirmed within
    /// <see cref="CreatorsContract.HoldDays"/> days), and what's left to withdraw after those and any
    /// withdrawal still on its way.
    /// </summary>
    internal async Task<(decimal Balance, decimal OnHold, decimal Withdrawable)> WithdrawableAsync(Guid userId, CancellationToken ct)
    {
        decimal balance = 0;
        try
        {
            balance = (await banking.GetBalancesAsync(userId, ct))
                .Where(b => b.Currency.Equals(bridge.Value.Currency, StringComparison.OrdinalIgnoreCase))
                .Sum(b => decimal.TryParse(b.Amount, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var a) ? a : 0);
        }
        catch (Exception ex) when (ex is BridgeApiException or HttpRequestException)
        {
            log.LogWarning(ex, "Couldn't read the wallet balance of {UserId}", userId);
        }
        var holdStart = Now.AddDays(-CreatorsContract.HoldDays);
        var onHold = await db.CreatorPayments.AsNoTracking()
            .Where(p => p.CreatorId == userId && p.Status == TransferStatuses.Confirmed && p.ConfirmedAt >= holdStart)
            .SumAsync(p => p.Amount - p.Fee, ct);
        var inFlight = await db.Withdrawals.AsNoTracking()
            .Where(w => w.UserId == userId && (w.Status == PayoutStatuses.Pending || w.Status == PayoutStatuses.Processing))
            .SumAsync(w => w.Amount, ct);
        return (balance, onHold, Math.Max(0, Math.Round(balance - onHold - inFlight, 2)));
    }
}

/// <summary>
/// Keeps creator payments in step with their transfers (no dependency on BankingService, which calls
/// it). A first payment that fails ends the subscription; a renewal that fails starts the grace period
/// (FR-CR-07) and is retried daily; an unlock that fails is taken back.
/// </summary>
public sealed class CreatorSettlement(ChatDbContext db, ChatNotifier notifier, TimeProvider clock) : IBankTransferListener
{
    public async Task TransferChangedAsync(BankTransfer transfer, CancellationToken ct)
    {
        var payment = await db.CreatorPayments.FirstOrDefaultAsync(p => p.BankTransferId == transfer.Id, ct);
        if (payment is null || payment.Status == transfer.Status)
            return;
        var now = clock.GetUtcNow();
        payment.Status = transfer.Status;
        if (transfer.Status == TransferStatuses.Confirmed)
            payment.ConfirmedAt = now;

        var subscription = payment.SubscriptionId is { } sid ? await db.CreatorSubscriptions.FirstOrDefaultAsync(s => s.Id == sid, ct) : null;
        switch (payment.Kind, transfer.Status)
        {
            case (CreatorPaymentKinds.Renewal, TransferStatuses.Confirmed) when subscription is not null:
                subscription.CurrentPeriodEnd = payment.PeriodEnd ?? subscription.CurrentPeriodEnd;
                (subscription.Status, subscription.GraceUntil, subscription.NextAttemptAt) = (SubscriptionStatuses.Active, null, subscription.CurrentPeriodEnd);
                break;
            case (CreatorPaymentKinds.Renewal, TransferStatuses.Failed) when subscription is { Status: SubscriptionStatuses.Active or SubscriptionStatuses.Grace }:
                if (subscription.Status == SubscriptionStatuses.Active)
                    (subscription.Status, subscription.GraceUntil) = (SubscriptionStatuses.Grace,
                        (now > subscription.CurrentPeriodEnd ? now : subscription.CurrentPeriodEnd).AddDays(CreatorsContract.GraceDays));
                subscription.NextAttemptAt = now.AddDays(1) < subscription.GraceUntil ? now.AddDays(1) : subscription.GraceUntil;
                break;
            case (CreatorPaymentKinds.Subscription, TransferStatuses.Failed) when subscription is not null:
                (subscription.Status, subscription.NextAttemptAt) = (SubscriptionStatuses.Ended, null);
                break;
            case (CreatorPaymentKinds.Unlock, TransferStatuses.Failed) when payment.PostId is { } post:
                await db.CreatorUnlocks.Where(u => u.PostId == post && u.PaymentId == payment.Id).ExecuteDeleteAsync(ct);
                break;
        }
        if (subscription is not null)
            subscription.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        await notifier.PublishAsync(CreatorsContract.Topic(payment.PayerId), new CreatorNewsDto(payment.Kind + "." + transfer.Status, payment.SubscriptionId ?? payment.PostId));
        await notifier.PublishAsync(CreatorsContract.Topic(payment.CreatorId), new CreatorNewsDto("payment." + transfer.Status, payment.Id));
    }
}
