using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Data;

namespace Ndeipi.Payments.Webhooks;

public sealed record WebhookEndpointCreateRequest(string? Url, string[]? EventTypes, string? Description);

public sealed record WebhookEndpointUpdateRequest(string? Url, string[]? EventTypes, WebhookEndpointStatus? Status, string? Description);

/// <summary>The <c>WebhookEndpoint</c> object (openapi.yaml).</summary>
public sealed record WebhookEndpointDto(
    string Id,
    string Url,
    WebhookEndpointStatus Status,
    IReadOnlyList<string> EventTypes,
    string? Description,
    string PublicKeyPem,
    int Version,
    DateTimeOffset CreatedAt)
{
    public string Object => "webhook_endpoint";

    public static WebhookEndpointDto From(WebhookEndpoint w) => new(
        w.Id, w.Url, w.Status, JsonSerializer.Deserialize<string[]>(w.EventTypesJson) ?? [], w.Description, w.PublicKeyPem, w.Version, w.CreatedAt);
}

/// <summary>
/// Webhook endpoints (FR-WH-01): HTTPS URLs, each with its own Ed25519 key pair. The public key is
/// returned with the endpoint; the private key is encrypted at rest and only the dispatcher reads it.
/// New endpoints start disabled (FR-WH-07) and are enabled with an update.
/// </summary>
public sealed class WebhookEndpointService(PaymentsDbContext db, IDataProtectionProvider protection, TimeProvider clock)
{
    public const string KeyPurpose = "Ndeipi.Payments.WebhookKeys.v1";

    public async Task<WebhookEndpointDto> CreateAsync(WebhookEndpointCreateRequest request, CancellationToken ct)
    {
        var errors = new List<ApiErrorDetail>();
        var url = ValidateUrl(request.Url, required: true, errors);
        var types = ValidateTypes(request.EventTypes, errors);
        ValidateDescription(request.Description, errors);
        if (errors.Count > 0)
            throw PaymentsException.Validation(errors);

        var keys = WebhookSigner.GenerateKeyPair();
        var endpoint = new WebhookEndpoint
        {
            Id = Ids.New(Ids.WebhookEndpoint, clock),
            Url = url!,
            Status = WebhookEndpointStatus.Disabled,
            EventTypesJson = JsonSerializer.Serialize(types ?? []),
            Description = request.Description,
            PublicKeyPem = keys.PublicKeyPem,
            ProtectedPrivateKey = Convert.ToBase64String(protection.CreateProtector(KeyPurpose).Protect(keys.PrivateKey)),
            CreatedAt = clock.GetUtcNow()
        };
        db.WebhookEndpoints.Add(endpoint);
        await db.SaveChangesAsync(ct);
        return WebhookEndpointDto.From(endpoint);
    }

    public Task<Page<WebhookEndpointDto>> ListAsync(PageRequest page, CancellationToken ct) =>
        db.WebhookEndpoints.AsNoTracking().Where(w => w.DeletedAt == null).PageAsync(w => w.Id, page, WebhookEndpointDto.From, ct);

    public async Task<WebhookEndpointDto> GetAsync(string id, CancellationToken ct) =>
        WebhookEndpointDto.From(await FindAsync(id, tracked: false, ct));

    public async Task<WebhookEndpointDto> UpdateAsync(string id, WebhookEndpointUpdateRequest request, CancellationToken ct)
    {
        if (request is { Url: null, EventTypes: null, Status: null, Description: null })
            throw PaymentsException.BadRequest("Send at least one field to update.");
        var errors = new List<ApiErrorDetail>();
        var url = ValidateUrl(request.Url, required: false, errors);
        var types = ValidateTypes(request.EventTypes, errors);
        ValidateDescription(request.Description, errors);
        if (errors.Count > 0)
            throw PaymentsException.Validation(errors);

        var endpoint = await FindAsync(id, tracked: true, ct);
        if (url is not null)
            endpoint.Url = url;
        if (types is not null)
            endpoint.EventTypesJson = JsonSerializer.Serialize(types);
        if (request.Status is { } status)
            endpoint.Status = status;
        if (request.Description is not null)
            endpoint.Description = request.Description;
        endpoint.Version++;
        await SaveAsync(ct);
        return WebhookEndpointDto.From(endpoint);
    }

    /// <summary>Stops deliveries to the endpoint; pending ones are canceled by the dispatcher.</summary>
    public async Task<WebhookEndpointDto> DeleteAsync(string id, CancellationToken ct)
    {
        var endpoint = await FindAsync(id, tracked: true, ct);
        endpoint.DeletedAt = clock.GetUtcNow();
        endpoint.Status = WebhookEndpointStatus.Disabled;
        endpoint.Version++;
        await SaveAsync(ct);
        return WebhookEndpointDto.From(endpoint);
    }

    /// <summary>The endpoint's private key, for signing. Only the dispatcher calls this.</summary>
    public static byte[] PrivateKey(WebhookEndpoint endpoint, IDataProtectionProvider protection) =>
        protection.CreateProtector(KeyPurpose).Unprotect(Convert.FromBase64String(endpoint.ProtectedPrivateKey));

    async Task<WebhookEndpoint> FindAsync(string id, bool tracked, CancellationToken ct)
    {
        var query = tracked ? db.WebhookEndpoints : db.WebhookEndpoints.AsNoTracking();
        return await query.FirstOrDefaultAsync(w => w.Id == id && w.DeletedAt == null, ct) ?? throw PaymentsException.NotFound("webhook endpoint");
    }

    async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw PaymentsException.Conflict("invalid_state", "The endpoint changed while this update ran. Fetch it and try again.");
        }
    }

    /// <summary>
    /// An absolute HTTPS URL with a host and no credentials. Where it resolves is checked at delivery
    /// time (<see cref="WebhookHttp"/>), since DNS can change after registration.
    /// </summary>
    static string? ValidateUrl(string? value, bool required, List<ApiErrorDetail> errors)
    {
        if (value is null)
        {
            if (required)
                errors.Add(new("url", "required", "url is required."));
            return null;
        }
        if (value.Length > 2000 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            errors.Add(new("url", "invalid_format", "url is an absolute HTTPS URL without credentials, at most 2,000 characters."));
            return null;
        }
        return uri.ToString();
    }

    static string[]? ValidateTypes(string[]? types, List<ApiErrorDetail> errors)
    {
        if (types is null)
            return null;
        foreach (var type in types.Where(t => !EventTypes.All.Contains(t)).Distinct())
            errors.Add(new("event_types", "unknown_event_type", $"{type} is not an event type."));
        return [.. types.Distinct().Order(StringComparer.Ordinal)];
    }

    static void ValidateDescription(string? description, List<ApiErrorDetail> errors)
    {
        if (description is { Length: > 200 })
            errors.Add(new("description", "too_long", "description is at most 200 characters."));
    }
}
