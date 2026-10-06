using Ndeipi.Payments.Api;

namespace Ndeipi.Payments.Webhooks;

/// <summary>
/// Webhook endpoints, events and delivery (SRS §4.9). Events are already written to the outbox with
/// every change (<see cref="EventOutbox"/>); endpoint management, the dispatcher and redelivery are
/// built in M2, signing with <see cref="WebhookSigner"/>.
/// </summary>
public static class WebhooksModule
{
    public static IServiceCollection AddWebhooks(this IServiceCollection services) => services.AddScoped<EventOutbox>();

    public static void MapWebhooks(this RouteGroupBuilder v1)
    {
        v1.MapPost("/webhook_endpoints", Stubs.Milestone("M2"));
        v1.MapGet("/webhook_endpoints", Stubs.Milestone("M2"));
        v1.MapGet("/webhook_endpoints/{webhook_endpoint_id}", Stubs.Milestone("M2"));
        v1.MapPatch("/webhook_endpoints/{webhook_endpoint_id}", Stubs.Milestone("M2"));
        v1.MapDelete("/webhook_endpoints/{webhook_endpoint_id}", Stubs.Milestone("M2"));
        v1.MapPost("/webhook_endpoints/{webhook_endpoint_id}/test", Stubs.Milestone("M6"));

        v1.MapGet("/events", Stubs.Milestone("M2"));
        v1.MapGet("/events/{event_id}", Stubs.Milestone("M2"));
        v1.MapPost("/events/{event_id}/redeliver", Stubs.Milestone("M2"));
    }
}
