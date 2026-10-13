using Ndeipi.Payments.Api;

namespace Ndeipi.Payments.Webhooks;

/// <summary>
/// Webhook endpoints, events and delivery (SRS §4.9, built in M2). Every change writes its event
/// and deliveries through <see cref="EventOutbox"/>; <see cref="WebhookDispatcher"/> sends them,
/// signed with <see cref="WebhookSigner"/>. The test delivery call follows in M6.
/// </summary>
public static class WebhooksModule
{
    public static IServiceCollection AddWebhooks(this IServiceCollection services)
    {
        services.AddScoped<EventOutbox>();
        services.AddScoped<WebhookEndpointService>();
        services.AddScoped<EventService>();
        services.AddWebhookHttp();
        services.AddSingleton<WebhookDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<WebhookDispatcher>());
        return services;
    }

    public static void MapWebhooks(this RouteGroupBuilder v1)
    {
        v1.MapPost("/webhook_endpoints", async (WebhookEndpointCreateRequest request, WebhookEndpointService endpoints, CancellationToken ct) =>
            Results.Json(await endpoints.CreateAsync(request, ct), PaymentsJson.Options, statusCode: 201));
        v1.MapGet("/webhook_endpoints", async (HttpRequest http, WebhookEndpointService endpoints, CancellationToken ct) =>
            Results.Json(await endpoints.ListAsync(PageRequest.From(http), ct), PaymentsJson.Options));
        v1.MapGet("/webhook_endpoints/{webhook_endpoint_id}", async (string webhook_endpoint_id, WebhookEndpointService endpoints, CancellationToken ct) =>
            Results.Json(await endpoints.GetAsync(webhook_endpoint_id, ct), PaymentsJson.Options));
        v1.MapPatch("/webhook_endpoints/{webhook_endpoint_id}", async (string webhook_endpoint_id, WebhookEndpointUpdateRequest request, WebhookEndpointService endpoints, CancellationToken ct) =>
            Results.Json(await endpoints.UpdateAsync(webhook_endpoint_id, request, ct), PaymentsJson.Options));
        v1.MapDelete("/webhook_endpoints/{webhook_endpoint_id}", async (string webhook_endpoint_id, WebhookEndpointService endpoints, CancellationToken ct) =>
            Results.Json(await endpoints.DeleteAsync(webhook_endpoint_id, ct), PaymentsJson.Options));
        v1.MapPost("/webhook_endpoints/{webhook_endpoint_id}/test", Stubs.Milestone("M6"));

        v1.MapGet("/events", async (HttpRequest http, EventService events, string? type, string? object_id, DateTimeOffset? created_after, DateTimeOffset? created_before, CancellationToken ct) =>
            Results.Json(await events.ListAsync(PageRequest.From(http), type, object_id, created_after, created_before, ct), PaymentsJson.Options));
        v1.MapGet("/events/{event_id}", async (string event_id, EventService events, CancellationToken ct) =>
            Results.Json(await events.GetAsync(event_id, ct), PaymentsJson.Options));
        v1.MapPost("/events/{event_id}/redeliver", async (string event_id, RedeliverRequest? request, EventService events, CancellationToken ct) =>
            Results.Json(await events.RedeliverAsync(event_id, request ?? new(null), ct), PaymentsJson.Options, statusCode: 202));
    }
}
