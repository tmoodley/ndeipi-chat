using System.Text.Json;
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
}

/// <summary>
/// Adds an event to the change tracker next to the change it reports, so one SaveChanges (and one
/// transaction) commits both or neither (SC-04). Delivery happens later, from the table.
/// </summary>
public sealed class EventOutbox(PaymentsDbContext db, TimeProvider clock)
{
    public EventRecord Add<T>(string type, string objectType, string objectId, int objectVersion, T snapshot, object? previousAttributes = null)
    {
        var record = new EventRecord
        {
            Id = Ids.New(Ids.Event, clock),
            Type = type,
            ObjectType = objectType,
            ObjectId = objectId,
            ObjectVersion = objectVersion,
            DataJson = JsonSerializer.Serialize(snapshot, PaymentsJson.Options),
            PreviousAttributesJson = previousAttributes is null ? null : JsonSerializer.Serialize(previousAttributes, PaymentsJson.Options),
            CreatedAt = clock.GetUtcNow()
        };
        db.Events.Add(record);
        return record;
    }
}
