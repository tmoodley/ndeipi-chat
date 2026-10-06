using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Data;

namespace Ndeipi.Payments.Webhooks;

public sealed record RedeliverRequest(string? WebhookEndpointId);

/// <summary>The <c>Event</c> object (openapi.yaml), as the API returns it and as deliveries carry it.</summary>
public sealed record EventDto(
    string Id,
    string Type,
    string ApiVersion,
    DateTimeOffset CreatedAt,
    string ObjectType,
    int ObjectVersion,
    JsonElement? PreviousAttributes,
    JsonElement Data)
{
    public string Object => "event";

    public static EventDto From(EventRecord e) => new(
        e.Id, e.Type, "v1", e.CreatedAt, e.ObjectType, e.ObjectVersion,
        e.PreviousAttributesJson is null ? null : JsonDocument.Parse(e.PreviousAttributesJson).RootElement.Clone(),
        JsonDocument.Parse(e.DataJson).RootElement.Clone());
}

/// <summary>Events for the integrator to list, fetch and redeliver (SRV-OPS-05), kept for 30 days.</summary>
public sealed class EventService(PaymentsDbContext db, EventOutbox outbox, IOptions<PaymentsOptions> options, TimeProvider clock)
{
    DateTimeOffset Oldest => clock.GetUtcNow() - options.Value.Webhooks.Retention;

    public Task<Page<EventDto>> ListAsync(
        PageRequest page, string? type, string? objectId, DateTimeOffset? createdAfter, DateTimeOffset? createdBefore, CancellationToken ct)
    {
        var oldest = Oldest;
        var query = db.Events.AsNoTracking().Where(e => e.CreatedAt >= oldest);
        if (type is not null)
            query = query.Where(e => e.Type == type);
        if (objectId is not null)
            query = query.Where(e => e.ObjectId == objectId);
        if (createdAfter is { } after)
            query = query.Where(e => e.CreatedAt > after);
        if (createdBefore is { } before)
            query = query.Where(e => e.CreatedAt < before);
        return query.PageAsync(e => e.Id, page, EventDto.From, ct);
    }

    public async Task<EventDto> GetAsync(string id, CancellationToken ct) => EventDto.From(await FindAsync(id, ct));

    /// <summary>
    /// Queues a fresh delivery to one endpoint, or to every enabled endpoint subscribed to the
    /// event's type. Each is signed at its own attempt time, as every delivery is.
    /// </summary>
    public async Task<EventDto> RedeliverAsync(string id, RedeliverRequest request, CancellationToken ct)
    {
        var record = await FindAsync(id, ct);
        var endpoints = db.WebhookEndpoints.AsNoTracking().Where(w => w.DeletedAt == null);
        List<string> targets;
        if (request.WebhookEndpointId is { } endpointId)
        {
            if (!await endpoints.AnyAsync(w => w.Id == endpointId, ct))
                throw PaymentsException.NotFound("webhook endpoint");
            targets = [endpointId];
        }
        else
        {
            var enabled = await endpoints.Where(w => w.Status == WebhookEndpointStatus.Enabled).Select(w => new { w.Id, w.EventTypesJson }).ToListAsync(ct);
            targets = [.. enabled
                .Where(w => JsonSerializer.Deserialize<string[]>(w.EventTypesJson) is not { Length: > 0 } types || types.Contains(record.Type))
                .Select(w => w.Id)];
        }

        foreach (var target in targets)
            db.EventDeliveries.Add(outbox.Redeliver(record.Id, target));
        await db.SaveChangesAsync(ct);
        return EventDto.From(record);
    }

    async Task<EventRecord> FindAsync(string id, CancellationToken ct)
    {
        var oldest = Oldest;
        return await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id && e.CreatedAt >= oldest, ct)
            ?? throw PaymentsException.NotFound("event");
    }
}
