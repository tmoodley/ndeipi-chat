using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Data;
using Ndeipi.Payments.Transfers;
using Ndeipi.Payments.Webhooks;

namespace Ndeipi.Payments.Users;

public sealed record UserCreateRequest(
    UserType? Type,
    string? ExternalReference,
    string? Email,
    string? Phone,
    string? FirstName,
    string? LastName,
    string? BusinessName,
    string? Country,
    Dictionary<string, string>? Metadata);

public sealed record UserUpdateRequest(string? Email, string? Phone, Dictionary<string, string>? Metadata);

public sealed record ReasonDto(string Code, string Message);

/// <summary>The <c>User</c> object (openapi.yaml).</summary>
public sealed record UserDto(
    string Id,
    UserType Type,
    string ExternalReference,
    string? Email,
    string? Phone,
    string? FirstName,
    string? LastName,
    string? BusinessName,
    string? Country,
    UserStatus Status,
    KycStatus KycStatus,
    TermsStatus TermsStatus,
    IReadOnlyList<ReasonDto> RejectionReasons,
    IReadOnlyDictionary<string, string> Metadata,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public string Object => "user";

    public static UserDto From(PaymentUser u) => new(
        u.Id, u.Type, u.ExternalReference, u.Email, u.Phone, u.FirstName, u.LastName, u.BusinessName, u.Country,
        u.Status, u.KycStatus, u.TermsStatus,
        JsonSerializer.Deserialize<List<ReasonDto>>(u.RejectionReasonsJson, PaymentsJson.Options) ?? [],
        JsonSerializer.Deserialize<Dictionary<string, string>>(u.MetadataJson, PaymentsJson.Options) ?? [],
        u.Version, u.CreatedAt, u.UpdatedAt);
}

/// <summary>
/// User accounts (SRS §4.1): create, get, list and update (M1), hosted onboarding links and KYC
/// status changes (M2, <see cref="OnboardingService"/>, <see cref="UserStatusService"/>).
/// Deactivation (M3) is refused while the user's wallets hold funds or a transfer is open.
/// </summary>
public static class UsersModule
{
    public static IServiceCollection AddPaymentUsers(this IServiceCollection services)
    {
        services.AddScoped<UserService>();
        services.AddScoped<OnboardingService>();
        services.AddScoped<UserStatusService>();
        services.AddSingleton<SimulatedKycProvider>();
        services.AddSingleton<IKycProvider>(sp =>
            sp.GetRequiredService<IOptions<PaymentsOptions>>().Value.KycProvider switch
            {
                "simulated" => sp.GetRequiredService<SimulatedKycProvider>(),
                var other => throw new InvalidOperationException($"No KYC provider '{other}' is built yet (SRS §11).")
            });
        return services;
    }

    public static void MapUsers(this RouteGroupBuilder v1)
    {
        v1.MapPost("/users", async (UserCreateRequest request, UserService users, CancellationToken ct) =>
            Results.Json(await users.CreateAsync(request, ct), PaymentsJson.Options, statusCode: 201));

        v1.MapGet("/users", async (HttpRequest http, UserService users, string? external_reference, string? kyc_status, string? status, CancellationToken ct) =>
            Results.Json(await users.ListAsync(
                PageRequest.From(http),
                external_reference,
                WireEnum.Parse<KycStatus>(kyc_status, "kyc_status"),
                WireEnum.Parse<UserStatus>(status, "status"),
                ct), PaymentsJson.Options));

        v1.MapGet("/users/{user_id}", async (string user_id, UserService users, CancellationToken ct) =>
            Results.Json(await users.GetAsync(user_id, ct), PaymentsJson.Options));

        v1.MapPatch("/users/{user_id}", async (string user_id, UserUpdateRequest request, UserService users, CancellationToken ct) =>
            Results.Json(await users.UpdateAsync(user_id, request, ct), PaymentsJson.Options));

        v1.MapPost("/users/{user_id}/deactivate", async (string user_id, UserService users, CancellationToken ct) =>
            Results.Json(await users.DeactivateAsync(user_id, ct), PaymentsJson.Options));

        v1.MapPost("/users/{user_id}/onboarding_links", async (string user_id, OnboardingLinksRequest? request, OnboardingService onboarding, CancellationToken ct) =>
            Results.Json(await onboarding.CreateAsync(user_id, request ?? new(null), ct), PaymentsJson.Options, statusCode: 201));
    }
}

public sealed partial class UserService(PaymentsDbContext db, EventOutbox events, TimeProvider clock)
{
    public async Task<UserDto> CreateAsync(UserCreateRequest request, CancellationToken ct)
    {
        Validate(request);
        var reference = request.ExternalReference!.Trim();

        if (await db.Users.AsNoTracking().Where(u => u.ExternalReference == reference).Select(u => u.Id).FirstOrDefaultAsync(ct) is { } existing)
            throw ExternalReferenceExists(existing);

        var now = clock.GetUtcNow();
        var user = new PaymentUser
        {
            Id = Ids.New(Ids.User, clock),
            Type = request.Type!.Value,
            ExternalReference = reference,
            Email = request.Email,
            Phone = request.Phone,
            FirstName = request.FirstName,
            LastName = request.LastName,
            BusinessName = request.BusinessName,
            Country = request.Country,
            Status = UserStatus.Active,
            KycStatus = KycStatus.NotStarted,
            TermsStatus = TermsStatus.Pending,
            MetadataJson = JsonSerializer.Serialize(request.Metadata ?? [], PaymentsJson.Options),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Users.Add(user);
        var dto = UserDto.From(user);
        await events.AddAsync(EventTypes.UserCreated, "user", user.Id, user.Version, dto, null, ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The same reference, created at the same moment by another request.
            db.ChangeTracker.Clear();
            var winner = await db.Users.AsNoTracking().Where(u => u.ExternalReference == reference).Select(u => u.Id).FirstOrDefaultAsync(ct);
            if (winner is null)
                throw;
            throw ExternalReferenceExists(winner);
        }
        return dto;
    }

    public async Task<UserDto> GetAsync(string id, CancellationToken ct) =>
        UserDto.From(await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw PaymentsException.NotFound("user"));

    public Task<Page<UserDto>> ListAsync(PageRequest page, string? externalReference, KycStatus? kycStatus, UserStatus? status, CancellationToken ct)
    {
        var query = db.Users.AsNoTracking();
        if (externalReference is not null)
            query = query.Where(u => u.ExternalReference == externalReference);
        if (kycStatus is not null)
            query = query.Where(u => u.KycStatus == kycStatus);
        if (status is not null)
            query = query.Where(u => u.Status == status);
        return query.PageAsync(u => u.Id, page, UserDto.From, ct);
    }

    /// <summary>
    /// Deactivates a user (FR-USER-07): refused while any wallet holds funds in any bucket or a
    /// transfer involving the user is still open, since a deactivated user can neither send nor
    /// receive. Freezes the user's wallets and emits <c>user.deactivated</c>. Deactivating twice
    /// returns the user unchanged.
    /// </summary>
    public async Task<UserDto> DeactivateAsync(string id, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw PaymentsException.NotFound("user");
        if (user.Status == UserStatus.Deactivated)
            return UserDto.From(user);

        var wallets = await db.Wallets.Where(w => w.UserId == id).ToListAsync(ct);
        var walletIds = wallets.Select(w => w.Id).ToList();
        if (await db.LedgerAccounts.AnyAsync(a => a.WalletId != null && walletIds.Contains(a.WalletId) && a.Balance != 0, ct))
            throw PaymentsException.Conflict("user_has_balance", "This user's wallets still hold funds. Pay them out or move them first.");

        var unsettled = await db.Transfers.AsNoTracking()
            .Where(t => (t.SourceUserId == id || t.DestinationUserId == id) &&
                        t.State != TransferState.Completed && t.State != TransferState.Failed &&
                        t.State != TransferState.Canceled && t.State != TransferState.Refunded)
            .Select(t => new { t.Kind, t.State })
            .ToListAsync(ct);
        if (unsettled.Any(t => !TransferStates.IsFinal(t.Kind, t.State)))
            throw PaymentsException.Conflict("user_has_open_transfers", "A transfer involving this user is still open.");

        var previous = user.Status;
        user.Status = UserStatus.Deactivated;
        user.Version++;
        user.UpdatedAt = clock.GetUtcNow();
        foreach (var wallet in wallets)
        {
            wallet.Status = WalletStatus.Frozen;
            wallet.Version++;
        }
        var snapshot = UserDto.From(user);
        await events.AddAsync(EventTypes.UserDeactivated, "user", user.Id, user.Version, snapshot, new { status = previous }, ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw PaymentsException.Conflict("invalid_state", "The user changed while this ran. Try again.");
        }
        return snapshot;
    }

    public async Task<UserDto> UpdateAsync(string id, UserUpdateRequest request, CancellationToken ct)
    {
        if (request is { Email: null, Phone: null, Metadata: null })
            throw PaymentsException.BadRequest("Send at least one field to update.");
        var errors = new List<ApiErrorDetail>();
        ValidateContact(request.Email, request.Phone, errors);
        ValidateMetadata(request.Metadata, errors);
        if (errors.Count > 0)
            throw PaymentsException.Validation(errors);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw PaymentsException.NotFound("user");
        if (request.Email is not null)
            user.Email = request.Email;
        if (request.Phone is not null)
            user.Phone = request.Phone;
        if (request.Metadata is not null)
            user.MetadataJson = JsonSerializer.Serialize(request.Metadata, PaymentsJson.Options);
        user.Version++;
        user.UpdatedAt = clock.GetUtcNow();

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw PaymentsException.Conflict("invalid_state", "The user changed while this update ran. Fetch it and try again.");
        }
        return UserDto.From(user);
    }

    static PaymentsException ExternalReferenceExists(string existingId) =>
        PaymentsException.Conflict("external_reference_exists", "A user with this external_reference already exists.", existingId, "external_reference");

    static void Validate(UserCreateRequest r)
    {
        var errors = new List<ApiErrorDetail>();
        if (r.Type is null)
            errors.Add(new("type", "required", "type is required: individual or business."));
        if (string.IsNullOrWhiteSpace(r.ExternalReference))
            errors.Add(new("external_reference", "required", "external_reference is required."));
        else if (r.ExternalReference.Trim().Length > 128)
            errors.Add(new("external_reference", "too_long", "external_reference is at most 128 characters."));

        if (r.Type == UserType.Business)
        {
            if (string.IsNullOrWhiteSpace(r.BusinessName))
                errors.Add(new("business_name", "required", "business_name is required for business users."));
            if (r.FirstName is not null || r.LastName is not null)
                errors.Add(new(r.FirstName is not null ? "first_name" : "last_name", "not_allowed", "Business users have business_name, not first and last names."));
        }
        else if (r.Type == UserType.Individual && r.BusinessName is not null)
            errors.Add(new("business_name", "not_allowed", "Individuals have first and last names, not business_name."));

        Limit(r.FirstName, "first_name", 100, errors);
        Limit(r.LastName, "last_name", 100, errors);
        Limit(r.BusinessName, "business_name", 200, errors);
        if (r.Country is not null && !CountryCode().IsMatch(r.Country))
            errors.Add(new("country", "invalid_format", "country is an ISO 3166-1 alpha-2 code, like ZW."));
        ValidateContact(r.Email, r.Phone, errors);
        ValidateMetadata(r.Metadata, errors);

        if (errors.Count > 0)
            throw PaymentsException.Validation(errors);
    }

    static void ValidateContact(string? email, string? phone, List<ApiErrorDetail> errors)
    {
        if (email is not null && (email.Length > 320 || !EmailShape().IsMatch(email)))
            errors.Add(new("email", "invalid_format", "email is not a valid address."));
        if (phone is not null && !E164().IsMatch(phone))
            errors.Add(new("phone", "invalid_format", "phone is in E.164 form, like +263771234567."));
    }

    /// <summary>Up to 50 keys of at most 40 characters, values of at most 500 (openapi.yaml, <c>Metadata</c>).</summary>
    public static void ValidateMetadata(Dictionary<string, string>? metadata, List<ApiErrorDetail> errors)
    {
        if (metadata is null)
            return;
        if (metadata.Count > 50)
            errors.Add(new("metadata", "too_many_keys", "metadata holds at most 50 keys."));
        foreach (var (key, value) in metadata)
        {
            if (key.Length is 0 or > 40)
                errors.Add(new($"metadata.{key}", "invalid_key", "metadata keys are 1 to 40 characters."));
            if (value is null || value.Length > 500)
                errors.Add(new($"metadata.{key}", "invalid_value", "metadata values are strings of at most 500 characters."));
        }
    }

    static void Limit(string? value, string field, int max, List<ApiErrorDetail> errors)
    {
        if (value is not null && value.Length > max)
            errors.Add(new(field, "too_long", $"{field} is at most {max} characters."));
    }

    [GeneratedRegex("^[A-Z]{2}$")]
    private static partial Regex CountryCode();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailShape();

    [GeneratedRegex(@"^\+[1-9]\d{6,14}$")]
    private static partial Regex E164();
}
