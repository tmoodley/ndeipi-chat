using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Data;

namespace Ndeipi.Payments.Webhooks;

/// <summary>Event types (openapi.yaml, <c>EventType</c>).</summary>
public static class EventTypes
{
    public const string UserCreated = "user.created";
    public const string UserKycStatusChanged = "user.kyc_status_changed";
    public const string UserTermsStatusChanged = "user.terms_status_changed";
    public const string UserDeactivated = "user.deactivated";
    public const string TransferCreated = "transfer.created";
    public const string TransferStateChanged = "transfer.state_changed";
    public const string DepositReceived = "deposit.received";
    public const string DepositReturned = "deposit.returned";
    public const string PayoutReturned = "payout.returned";
    public const string DepositAccountDeactivated = "deposit_account.deactivated";
    public const string WebhookEndpointTest = "webhook_endpoint.test";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        UserCreated, UserKycStatusChanged, UserTermsStatusChanged, UserDeactivated,
        TransferCreated, TransferStateChanged, DepositReceived, DepositReturned, PayoutReturned,
        DepositAccountDeactivated, WebhookEndpointTest
    };
}

/// <summary>
/// Adds an event, and a delivery to each enabled endpoint subscribed to its type, to the change
/// tracker next to the change it reports. One SaveChanges then commits all of them or none (SC-04):
/// no event is lost, and none is sent for a change that rolled back. The dispatcher sends from there.
/// </summary>
public sealed class EventOutbox(PaymentsDbContext db, IOptions<PaymentsOptions> options, TimeProvider clock)
{
    public async Task<EventRecord> AddAsync<T>(
        string type, string objectType, string objectId, int objectVersion, T snapshot, object? previousAttributes, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var record = new EventRecord
        {
            Id = Ids.New(Ids.Event, clock),
            Type = type,
            ObjectType = objectType,
            ObjectId = objectId,
            ObjectVersion = objectVersion,
            DataJson = JsonSerializer.Serialize(snapshot, PaymentsJson.Options),
            PreviousAttributesJson = previousAttributes is null ? null : JsonSerializer.Serialize(previousAttributes, PaymentsJson.Options),
            CreatedAt = now
        };
        db.Events.Add(record);

        foreach (var endpoint in await SubscribersAsync(type, ct))
            db.EventDeliveries.Add(NewDelivery(record.Id, endpoint, now));
        return record;
    }

    /// <summary>A fresh delivery of an existing event, for redelivery (SRV-OPS-05).</summary>
    public EventDelivery Redeliver(string eventId, string endpointId) => NewDelivery(eventId, endpointId, clock.GetUtcNow());

    async Task<IEnumerable<string>> SubscribersAsync(string type, CancellationToken ct)
    {
        var endpoints = await db.WebhookEndpoints.AsNoTracking()
            .Where(w => w.Status == WebhookEndpointStatus.Enabled && w.DeletedAt == null)
            .Select(w => new { w.Id, w.EventTypesJson })
            .ToListAsync(ct);
        return endpoints
            .Where(w => JsonSerializer.Deserialize<string[]>(w.EventTypesJson) is not { Length: > 0 } types || types.Contains(type))
            .Select(w => w.Id);
    }

    EventDelivery NewDelivery(string eventId, string endpointId, DateTimeOffset now) => new()
    {
        EventId = eventId,
        WebhookEndpointId = endpointId,
        Status = DeliveryStatus.Pending,
        NextAttemptAt = now,
        GiveUpAt = now + options.Value.Webhooks.RetryWindow,
        CreatedAt = now
    };
}
