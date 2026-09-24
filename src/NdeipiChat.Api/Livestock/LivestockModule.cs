using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NdeipiChat.Api.Auth;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Livestock;

public static class LivestockModule
{
    public const int MaxActiveKeysPerUser = 20;

    public static IServiceCollection AddLivestock(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<LivestockOptions>(config.GetSection(LivestockOptions.Section));
        services.AddHttpClient<ICattleVisionAssessor, ClaudeCattleAssessor>((sp, http) =>
        {
            var claude = sp.GetRequiredService<IOptions<LivestockOptions>>().Value.Claude;
            http.BaseAddress = new Uri(claude.BaseUrl.TrimEnd('/') + "/");
            http.Timeout = TimeSpan.FromSeconds(120);
            http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
            if (claude.ApiKey.Length > 0)
                http.DefaultRequestHeaders.Add("x-api-key", claude.ApiKey);
        });
        services.AddSingleton<IMuzzleEmbedder, OnnxMuzzleEmbedder>();
        services.AddSingleton<MuzzleIndex>();
        services.AddSingleton<ILivestockImageStore, FileSystemLivestockImageStore>();
        services.AddScoped<LivestockRegistrationService>();
        return services;
    }

    public static void MapLivestock(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup(LivestockContract.BasePath).RequireAuthorization();

        api.MapPost("/operator-keys", async (OperatorKeyRequest request, HttpContext http, CurrentUserService users, ChatDbContext db, TimeProvider clock) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            var publicKey = ReadP256PublicKey(request.PublicKey);
            if (await db.OperatorKeys.CountAsync(k => k.UserId == me.Id && k.RevokedAt == null, http.RequestAborted) >= MaxActiveKeysPerUser)
                throw new ChatRejectedException($"You already have {MaxActiveKeysPerUser} devices registered.");

            var key = new OperatorSigningKey
            {
                Id = Guid.NewGuid(),
                UserId = me.Id,
                PublicKey = publicKey,
                Label = request.Label?.Trim() is { Length: > 0 } label ? label[..Math.Min(label.Length, 100)] : null,
                CreatedAt = clock.GetUtcNow()
            };
            db.OperatorKeys.Add(key);
            await db.SaveChangesAsync(http.RequestAborted);
            return Results.Ok(new OperatorKeyDto(key.Id, key.CreatedAt));
        });

        // multipart/form-data: face_image, flank_image, metadata (a text field or a JSON file part).
        api.MapPost("/register", async (HttpContext http, CurrentUserService users, LivestockRegistrationService registry) =>
        {
            var ct = http.RequestAborted;
            var me = await users.GetAsync(http.User, ct);
            if (!http.Request.HasFormContentType)
                return Results.Json(
                    LivestockRegistrationResponse.Failure(RegistrationStatuses.Rejected, ["Send multipart/form-data with face_image, flank_image and metadata."]),
                    ContractJson.Options, statusCode: StatusCodes.Status400BadRequest);

            var form = await http.Request.ReadFormAsync(ct);
            var metadataFile = form.Files.GetFile(LivestockContract.MetadataField);
            var metadata = metadataFile is null
                ? form[LivestockContract.MetadataField].ToString()
                : System.Text.Encoding.UTF8.GetString(await ReadAsync(metadataFile, ct));

            var submission = new RegistrationSubmission(
                await ReadAsync(form.Files.GetFile(LivestockContract.FaceImageField), ct),
                await ReadAsync(form.Files.GetFile(LivestockContract.FlankImageField), ct),
                metadata,
                Guid.TryParse(http.Request.Headers[LivestockContract.KeyIdHeader].ToString(), out var keyId) ? keyId : null,
                http.Request.Headers[LivestockContract.SignatureHeader].ToString());

            var outcome = await registry.RegisterAsync(me, submission, ct);
            return Results.Json(outcome.Response, ContractJson.Options, statusCode: outcome.StatusCode);
        }).WithMetadata(new RequestSizeLimitAttribute(40 * 1024 * 1024));

        api.MapGet("/cows", async (HttpContext http, CurrentUserService users, ChatDbContext db) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            var rows = await db.Livestock.AsNoTracking()
                .Where(c => c.OwnerUserId == me.Id && c.IsActive)
                .OrderByDescending(c => c.RegistrationTimestamp)
                .Select(c => new
                {
                    c.CowId, c.RanchId, c.Breed, c.Sex, c.ApproximateAgeMonths, c.RegistrationTimestamp, c.FaceImageRef,
                    Latest = db.HealthAudits.Where(a => a.CowId == c.CowId).OrderByDescending(a => a.TimestampUtc)
                        .Select(a => new { a.BodyConditionScore, a.HealthRating, a.RequiresVetInspection }).FirstOrDefault()
                })
                .ToListAsync(http.RequestAborted);

            return Results.Ok(rows.Select(r => new CowSummaryDto(
                r.CowId, r.RanchId, r.Breed, r.Sex, r.ApproximateAgeMonths, Utc(r.RegistrationTimestamp),
                (double)(r.Latest?.BodyConditionScore ?? 0), r.Latest?.HealthRating ?? "", r.Latest?.RequiresVetInspection ?? false,
                r.FaceImageRef)));
        });

        api.MapGet("/cows/{cowId}", async (string cowId, HttpContext http, CurrentUserService users, ChatDbContext db) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            var cow = await db.Livestock.AsNoTracking().FirstOrDefaultAsync(c => c.CowId == cowId && c.OwnerUserId == me.Id, http.RequestAborted);
            if (cow is null)
                return Results.NotFound();

            var audits = await db.HealthAudits.AsNoTracking()
                .Where(a => a.CowId == cowId)
                .OrderByDescending(a => a.TimestampUtc)
                .ToListAsync(http.RequestAborted);

            return Results.Ok(new CowDetailDto(
                cow.CowId, cow.RanchId, cow.Breed, (double)cow.BreedConfidence, cow.BreedSource, cow.Sex, cow.ApproximateAgeMonths,
                Utc(cow.RegistrationTimestamp), (double)cow.Latitude, (double)cow.Longitude, cow.OwnerWallet, cow.MuzzleEmbedding is not null,
                ContractJson.Read<MorphologicalTraits>(cow.MorphologyJson)!, cow.FaceImageRef,
                audits.Select(a => new HealthAuditDto(
                    a.AuditId, Utc(a.TimestampUtc), (double)a.BodyConditionScore, a.HealthRating, a.HydrationStatus,
                    a.AnomalySummary is null ? [] : ContractJson.Read<List<DetectedAnomaly>>(a.AnomalySummary)!,
                    a.RequiresVetInspection, a.FlankImageRef, "0x" + a.AttestationHash)).ToList()));
        });

        // Photos are private to the animal's owner. ?width= returns a downscaled JPEG for lists.
        api.MapGet("/images/{reference}", async (string reference, int? width, HttpContext http, CurrentUserService users, ChatDbContext db, ILivestockImageStore store) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            var owns = await db.HealthAudits.AnyAsync(a =>
                (a.FaceImageRef == reference || a.FlankImageRef == reference)
                && db.Livestock.Any(c => c.CowId == a.CowId && c.OwnerUserId == me.Id), http.RequestAborted);
            if (!owns || store.PathFor(reference) is not { } path)
                return Results.NotFound();

            if (width is not { } w)
                return Results.File(path, CattleImages.ContentType(reference));

            using var bitmap = CattleImages.DecodeUpright(await File.ReadAllBytesAsync(path, http.RequestAborted));
            return bitmap is null
                ? Results.NotFound()
                : Results.File(CattleImages.ToJpeg(bitmap, Math.Clamp(w, 64, 1600), 80), "image/jpeg");
        });
    }

    static byte[] ReadP256PublicKey(string? base64)
    {
        byte[] spki;
        try
        {
            spki = Convert.FromBase64String(base64 ?? "");
        }
        catch (FormatException)
        {
            throw new ChatRejectedException("publicKey must be base64.");
        }

        using var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportSubjectPublicKeyInfo(spki, out _);
        }
        catch (CryptographicException)
        {
            throw new ChatRejectedException("publicKey must be an EC public key in SubjectPublicKeyInfo form.");
        }

        var curve = ecdsa.ExportParameters(false).Curve;
        var isP256 = curve.Oid?.Value == "1.2.840.10045.3.1.7" || curve.Oid?.FriendlyName is "nistP256" or "ECDSA_P256";
        if (!isP256)
            throw new ChatRejectedException("publicKey must be on the P-256 curve.");
        return spki;
    }

    static async Task<byte[]> ReadAsync(IFormFile? file, CancellationToken ct)
    {
        if (file is null)
            return [];
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
