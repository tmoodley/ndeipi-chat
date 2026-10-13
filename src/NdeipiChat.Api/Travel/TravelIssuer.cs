using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api.Banking;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Travel;

/// <summary>
/// Moves rooms and seats between held, booked and free again, and carries bookings through their
/// lives. Used by checkout, by the wallet transfer listener (payments confirm after the request),
/// by the hold sweeper and by vendors' cancellations and reschedules.
/// </summary>
public sealed class TravelIssuer(
    ChatDbContext db,
    ChatNotifier notifier,
    IServiceProvider services,
    IDataProtectionProvider protection,
    TimeProvider clock,
    ILogger<TravelIssuer> log)
{
    DateTimeOffset Now => clock.GetUtcNow();

    /// <summary>Gives a hold's rooms or seats back, if it's still <paramref name="fromStatus"/>. Idempotent.</summary>
    public async Task ReleaseAsync(Guid holdId, string fromStatus, CancellationToken ct)
    {
        var hold = await db.TravelHolds.AsNoTracking().FirstOrDefaultAsync(h => h.Id == holdId, ct);
        if (hold is null)
            return;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var released = await db.TravelHolds.Where(h => h.Id == holdId && h.Status == fromStatus)
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.Status, TravelBookings.Released), ct);
            if (released == 0)
                return;
            await MoveAsync(hold.ItemType, hold.ReferenceId, hold.CheckIn, hold.CheckOut, held: -hold.Quantity, booked: 0, ct);
            await tx.CommitAsync(ct);
        }
        await NotifyAsync([hold.UserId], null, ct);
    }

    /// <summary>Adds to (or takes from) the held and booked counts of a stay's nights or a departure.</summary>
    async Task MoveAsync(string type, Guid referenceId, DateOnly? checkIn, DateOnly? checkOut, int held, int booked, CancellationToken ct)
    {
        if (type == TravelItemTypes.Stay)
            await db.RoomNights.Where(n => n.RoomTypeId == referenceId && n.Date >= checkIn && n.Date < checkOut)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.Held, n => n.Held + held).SetProperty(n => n.Booked, n => n.Booked + booked), ct);
        else
            await db.TourDepartures.Where(d => d.Id == referenceId)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Held, d => d.Held + held).SetProperty(d => d.Booked, d => d.Booked + booked), ct);
    }

    /// <summary>The payment confirmed: held becomes booked, the invoice is numbered, and the traveller and vendor can talk. Idempotent.</summary>
    public async Task CompleteAsync(Guid bookingId, CancellationToken ct)
    {
        var booking = await db.TravelBookings.AsNoTracking().FirstAsync(b => b.Id == bookingId, ct);
        var hold = await db.TravelHolds.AsNoTracking().FirstAsync(h => h.Id == booking.HoldId, ct);
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var number = $"LTI-{Now:yyyyMMdd}-{booking.Reference[3..]}";
            var confirmed = await db.TravelBookings.Where(b => b.Id == bookingId && b.Status == TravelBookingStatuses.PendingPayment)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Status, TravelBookingStatuses.Confirmed)
                    .SetProperty(b => b.ConfirmedAt, Now)
                    .SetProperty(b => b.InvoiceNumber, number), ct);
            if (confirmed == 0)
                return;
            await db.TravelHolds.Where(h => h.Id == hold.Id).ExecuteUpdateAsync(s => s.SetProperty(h => h.Status, TravelBookings.Converted), ct);
            await MoveAsync(hold.ItemType, hold.ReferenceId, hold.CheckIn, hold.CheckOut, held: -hold.Quantity, booked: hold.Quantity, ct);
            await tx.CommitAsync(ct);
        }
        log.LogInformation("Lets Travel booking {Reference} confirmed", booking.Reference);
        await OpenChatAsync(booking, ct);
        await NotifyAsync([booking.TravelerId, booking.VendorId], new TravelChangedDto(booking.Id, TravelBookingStatuses.Confirmed, null), ct);
    }

    /// <summary>The payment failed: the booking says why, the room or seats go back on sale, and spent points come back.</summary>
    public async Task FailAsync(Guid bookingId, string? error, CancellationToken ct)
    {
        var booking = await db.TravelBookings.AsNoTracking().FirstAsync(b => b.Id == bookingId, ct);
        var failed = await db.TravelBookings.Where(b => b.Id == bookingId && b.Status == TravelBookingStatuses.PendingPayment)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, TravelBookingStatuses.Failed).SetProperty(b => b.Error, error ?? "The payment didn't go through."), ct);
        if (failed == 0)
            return;
        await ReturnPointsAsync(booking, ct);
        await ReleaseAsync(booking.HoldId, TravelBookings.Paying, ct);
        await NotifyAsync([booking.TravelerId], new TravelChangedDto(booking.Id, TravelBookingStatuses.Failed, error), ct);
    }

    async Task ReturnPointsAsync(TravelBooking booking, CancellationToken ct)
    {
        if (booking.PointsRedeemed <= 0)
            return;
        var points = services.GetRequiredService<Points.PointsService>();
        await points.AwardAsync(booking.TravelerId, booking.PointsRedeemed, "travel-return", booking.Id.ToString("N"),
            $"Lets Travel {booking.Reference}: points returned", ct);
    }

    /// <summary>
    /// Cancels a confirmed booking and refunds the traveller in full from the vendor's wallet; the
    /// rooms or seats go back on sale and spent points come back.
    /// </summary>
    public async Task CancelAndRefundAsync(Guid bookingId, Guid vendorId, string notice, CancellationToken ct)
    {
        var booking = await db.TravelBookings.AsNoTracking().FirstAsync(b => b.Id == bookingId, ct);
        var item = await db.TravelBookingItems.AsNoTracking().FirstAsync(i => i.BookingId == bookingId, ct);
        var refund = new BankTransfer
        {
            Id = Guid.NewGuid(),
            SenderId = vendorId,
            RecipientId = booking.TravelerId,
            Amount = booking.Amount - booking.Discount,
            Currency = (await db.BankTransfers.AsNoTracking().Where(t => t.Id == booking.BankTransferId).Select(t => t.Currency).FirstOrDefaultAsync(ct)) ?? "usdc",
            Memo = $"Lets Travel refund {booking.Reference}",
            CreatedAt = Now,
            UpdatedAt = Now
        };
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var canceled = await db.TravelBookings.Where(b => b.Id == bookingId && b.Status == TravelBookingStatuses.Confirmed)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Status, TravelBookingStatuses.Canceled)
                    .SetProperty(b => b.CanceledAt, Now)
                    .SetProperty(b => b.Notice, notice)
                    .SetProperty(b => b.RefundTransferId, refund.Id), ct);
            if (canceled == 0)
                return;
            db.BankTransfers.Add(refund);
            await db.SaveChangesAsync(ct);
            await MoveAsync(item.ItemType, item.ReferenceId, item.CheckIn, item.CheckOut, held: 0, booked: -item.Quantity, ct);
            await tx.CommitAsync(ct);
        }
        await ReturnPointsAsync(booking, ct);
        await services.GetRequiredService<BankingService>().SubmitTransferAsync(refund.Id, ct);
        await PostAsync(booking, vendorId, $"❌ Booking {booking.Reference} was cancelled: {notice} A full refund of {TravelContract.Money(refund.Amount)} is on its way to your Ndeipi wallet.", ct);
        await NotifyAsync([booking.TravelerId, booking.VendorId], new TravelChangedDto(booking.Id, TravelBookingStatuses.Canceled, notice), ct);
    }

    /// <summary>FR-3.3: tells everyone booked on a departure that it moved, or refunds them if it was called off.</summary>
    public async Task DepartureChangedAsync(Guid departureId, DateTimeOffset previousStart, CancellationToken ct)
    {
        var d = await db.TourDepartures.AsNoTracking().FirstAsync(x => x.Id == departureId, ct);
        var title = await db.TourPackages.Where(t => t.Id == d.TourId).Select(t => t.Title).FirstAsync(ct);
        var bookingIds = await db.TravelBookingItems.AsNoTracking().Where(i => i.ItemType == TravelItemTypes.Tour && i.ReferenceId == departureId)
            .Select(i => i.BookingId).ToListAsync(ct);
        var bookings = await db.TravelBookings.AsNoTracking().Where(b => bookingIds.Contains(b.Id) && b.Status == TravelBookingStatuses.Confirmed).ToListAsync(ct);
        foreach (var b in bookings)
        {
            if (d.Status == DepartureStatuses.Canceled)
            {
                await CancelAndRefundAsync(b.Id, b.VendorId, $"The {title} departure on {previousStart:d MMM, HH:mm} UTC was called off: {d.Note}", ct);
                continue;
            }
            var notice = $"Your {title} departure moved from {previousStart:d MMM, HH:mm} to {d.StartsAt:d MMM, HH:mm} UTC: {d.Note}";
            await db.TravelBookings.Where(x => x.Id == b.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Notice, notice), ct);
            await db.TravelBookingItems.Where(i => i.BookingId == b.Id).ExecuteUpdateAsync(s => s.SetProperty(i => i.StartsAt, d.StartsAt), ct);
            await PostAsync(b, b.VendorId, $"⚠️ {notice}. Reply here if the new time doesn't suit you.", ct);
            await NotifyAsync([b.TravelerId], new TravelChangedDto(b.Id, b.Status, notice), ct);
        }
    }

    /// <summary>FR-5.3: on confirmation, a chat between traveller and vendor, opened with the booking's details.</summary>
    async Task OpenChatAsync(TravelBooking booking, CancellationToken ct)
    {
        try
        {
            var traveler = await db.Users.FirstAsync(u => u.Id == booking.TravelerId, ct);
            var conversation = await services.GetRequiredService<ConversationService>()
                .CreateAsync(traveler, new CreateConversationRequest(ConversationType.Direct, [booking.VendorId], null), ct);
            await db.TravelBookings.Where(b => b.Id == booking.Id).ExecuteUpdateAsync(s => s.SetProperty(b => b.ConversationId, conversation.Id), ct);
            var item = await db.TravelBookingItems.AsNoTracking().FirstAsync(i => i.BookingId == booking.Id, ct);
            var when = item.CheckIn is { } a ? $"{a:d MMM} to {item.CheckOut:d MMM yyyy}" : $"{item.StartsAt:d MMM yyyy, HH:mm} UTC";
            booking.ConversationId = conversation.Id;
            await PostAsync(booking, booking.TravelerId,
                $"🧳 Booking {booking.Reference} confirmed: {item.Title}, {item.Subtitle}, {when}, {item.Guests} guest{(item.Guests == 1 ? "" : "s")}. Lead guest: {booking.LeadGuestName}." +
                (booking.SpecialRequests is { } s ? $" Requests: {s}" : "") + " (Sent by Lets Travel.)", ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The booking stands without the chat; both can still find each other from the booking.
            log.LogWarning(e, "Couldn't open the chat for booking {Reference}", booking.Reference);
        }
    }

    /// <summary>Posts in the booking's chat, as <paramref name="senderId"/>; at most once per text (a retry sends nothing new).</summary>
    async Task PostAsync(TravelBooking booking, Guid senderId, string text, CancellationToken ct)
    {
        var conversationId = booking.ConversationId ?? await db.TravelBookings.Where(b => b.Id == booking.Id).Select(b => b.ConversationId).FirstAsync(ct);
        if (conversationId is null)
            return;
        try
        {
            var sender = await db.Users.FirstAsync(u => u.Id == senderId, ct);
            var key = new Guid(MD5.HashData(Encoding.UTF8.GetBytes($"{booking.Id:N}|{text}")));
            await services.GetRequiredService<MessageService>().SendAsync(sender,
                new SendMessageRequest(conversationId.Value, MessageKinds.Text, ContractJson.ToElement(new TextPayload(text)), key), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Couldn't post to the chat for booking {Reference}", booking.Reference);
        }
    }

    public async Task NotifyAsync(IEnumerable<Guid> userIds, TravelChangedDto? change, CancellationToken ct)
    {
        foreach (var id in userIds.Distinct())
            await notifier.PublishAsync(TravelContract.Topic(id), change ?? new TravelChangedDto(Guid.Empty, "", null));
    }

    // ------------------------------------------------------------------ the printed voucher's code

    IDataProtector Printed => protection.CreateProtector("travel-printed-voucher");

    /// <summary>The code on a printed voucher: the booking, sealed by the server, for a scanner that's online.</summary>
    public string PrintedCode(Guid bookingId) => "LTP." + Printed.Protect(bookingId.ToString("N"));

    public static Guid? TryReadPrinted(IDataProtectionProvider protection, string code)
    {
        if (!code.StartsWith("LTP.", StringComparison.Ordinal))
            return null;
        try
        {
            return Guid.TryParseExact(protection.CreateProtector("travel-printed-voucher").Unprotect(code[4..]), "N", out var id) ? id : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ shapes

    public async Task<IReadOnlyList<TravelBookingDto>> DtosAsync(User viewer, IReadOnlyList<TravelBooking> bookings, CancellationToken ct)
    {
        var ids = bookings.Select(b => b.Id).ToList();
        var items = (await db.TravelBookingItems.AsNoTracking().Where(i => ids.Contains(i.BookingId)).ToListAsync(ct)).ToLookup(i => i.BookingId);
        var people = bookings.SelectMany(b => new[] { b.TravelerId, b.VendorId }).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => people.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var vendorNames = await db.TravelVendors.AsNoTracking().Where(v => people.Contains(v.UserId)).ToDictionaryAsync(v => v.UserId, v => v.Name, ct);
        var listings = items.SelectMany(g => g).Select(i => i.ListingId).Distinct().ToList();
        var addresses = await db.TravelProperties.AsNoTracking().Where(p => listings.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => string.Join(", ", new[] { p.Address, p.City, p.Country }.Where(s => !string.IsNullOrWhiteSpace(s))), ct);
        var meeting = await db.TourPackages.AsNoTracking().Where(t => listings.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.MeetingPoint, ct);
        var ids2 = protection.CreateProtector("travel-id-numbers");

        return [.. bookings.Select(b =>
        {
            var lines = items[b.Id].ToList();
            var listing = lines.FirstOrDefault()?.ListingId ?? Guid.Empty;
            // The ID number is for the vendor's permits; the traveller sees their own.
            var idNumber = b.IdNumberProtected is { } p && (viewer.Id == b.VendorId || viewer.Id == b.TravelerId) ? Unprotect(ids2, p) : null;
            return new TravelBookingDto(b.Id, b.Reference, b.Status, b.TravelerId, names.GetValueOrDefault(b.TravelerId, ""), b.VendorId,
                vendorNames.GetValueOrDefault(b.VendorId, names.GetValueOrDefault(b.VendorId, "")),
                [.. lines.Select(i => new TravelBookingItemDto(i.ItemType, i.ReferenceId, i.Title, i.Subtitle, i.CheckIn, i.CheckOut, i.StartsAt, i.Quantity, i.Guests,
                    JsonSerializer.Deserialize<List<TravelLineDto>>(i.LinesJson, ContractJson.Options)!, i.Amount))],
                b.Amount, b.Fee, b.Discount, b.PointsRedeemed, b.Currency, b.LeadGuestName, b.Phone, idNumber, b.SpecialRequests, b.Error,
                addresses.GetValueOrDefault(listing), meeting.GetValueOrDefault(listing), b.ConversationId, b.TravelerId == viewer.Id ? b.HolderPublicKey : null,
                b.CreatedAt, b.ConfirmedAt, b.CheckedInAt, b.Notice, b.TravelerId == viewer.Id);
        })];
    }

    static string? Unprotect(IDataProtector protector, string value)
    {
        try
        {
            return protector.Unprotect(value);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>Confirms a booking when its wallet payment is confirmed, and releases it when the payment fails.</summary>
public sealed class TravelPaymentListener(ChatDbContext db, TravelIssuer issuer) : IBankTransferListener
{
    public async Task TransferChangedAsync(BankTransfer transfer, CancellationToken ct)
    {
        if (transfer.Status is not (TransferStatuses.Confirmed or TransferStatuses.Failed))
            return;
        var booking = await db.TravelBookings.AsNoTracking().Where(b => b.BankTransferId == transfer.Id).Select(b => (Guid?)b.Id).FirstOrDefaultAsync(ct);
        if (booking is { } id)
        {
            if (transfer.Status == TransferStatuses.Confirmed)
                await issuer.CompleteAsync(id, ct);
            else
                await issuer.FailAsync(id, transfer.Error, ct);
            return;
        }
        // A refund that failed: the booking shows it, for the vendor to sort out.
        if (transfer.Status == TransferStatuses.Failed)
            await db.TravelBookings.Where(b => b.RefundTransferId == transfer.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.Error, "The refund didn't go through: " + (transfer.Error ?? "the vendor's wallet couldn't pay it.")), ct);
    }
}

/// <summary>Each person follows their own bookings; vendors, the bookings they receive.</summary>
public sealed class TravelTopicPolicy : ITopicPolicy
{
    public string Prefix => "travel";

    public Task<bool> CanSubscribeAsync(User user, string key, CancellationToken ct) => Task.FromResult(key == user.Id.ToString("N"));
}

/// <summary>Puts rooms and seats from abandoned holds back on sale (FR-2.3; acceptance 3: within 60 seconds of expiry).</summary>
public sealed class TravelHoldSweeper(IServiceScopeFactory scopes, Microsoft.Extensions.Options.IOptions<TravelOptions> options, TimeProvider clock, ILogger<TravelHoldSweeper> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.SweepInterval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await SweepAsync(scopes, clock, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    log.LogError(ex, "Travel hold sweep failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public static async Task<int> SweepAsync(IServiceScopeFactory scopes, TimeProvider clock, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var issuer = scope.ServiceProvider.GetRequiredService<TravelIssuer>();
        var now = clock.GetUtcNow();
        var expired = await db.TravelHolds.Where(h => h.Status == TravelBookings.Active && h.ExpiresAt <= now).Select(h => h.Id).Take(500).ToListAsync(ct);
        foreach (var id in expired)
            await issuer.ReleaseAsync(id, TravelBookings.Active, ct);
        return expired.Count;
    }
}
