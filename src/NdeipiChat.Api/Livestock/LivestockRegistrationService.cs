using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ndeipi.Api.Data;
using NdeipiChat.Contracts;
using SkiaSharp;

namespace Ndeipi.Api.Livestock;

public sealed record RegistrationSubmission(byte[] FaceImage, byte[] FlankImage, string MetadataJson, Guid? KeyId, string? Signature);

public sealed record RegistrationOutcome(int StatusCode, LivestockRegistrationResponse Response);

/// <summary>
/// Registers a cow -- or records a health audit of one already registered -- from a face photo and a
/// flank photo: verify the operator's device signature, check the photos, have Claude grade breed
/// and health, resolve identity by muzzle print, then record the animal and the audit.
/// </summary>
public sealed partial class LivestockRegistrationService(
    ChatDbContext db,
    ICattleVisionAssessor assessor,
    IMuzzleEmbedder embedder,
    MuzzleIndex index,
    ILivestockImageStore images,
    IOptions<LivestockOptions> options,
    TimeProvider clock,
    ILogger<LivestockRegistrationService> log)
{
    public const string BreedFromModel = "MODEL";
    public const string BreedFromClaim = "CLAIMED";
    public const string BreedUnconfirmed = "MODEL_UNCONFIRMED";

    // Identity decisions happen one at a time, so two uploads of the same animal can't both look new.
    static readonly SemaphoreSlim IdentityGate = new(1, 1);

    static readonly HashSet<string> NeedsVet =
        [AnomalyTypes.LumpySkin, AnomalyTypes.CornealOpacity, AnomalyTypes.SunkenEyes, AnomalyTypes.Wound,
         AnomalyTypes.OcularDischarge, AnomalyTypes.NasalDischarge, AnomalyTypes.Lameness];

    [GeneratedRegex(@"^urn:ndeipi:ranch:(?<country>[a-z]{2})-[a-z0-9][a-z0-9-]{0,40}$")]
    private static partial Regex RanchPattern();

    [GeneratedRegex("^0x[0-9a-fA-F]{40}$")]
    private static partial Regex WalletPattern();

    LivestockOptions Options => options.Value;

    public async Task<RegistrationOutcome> RegisterAsync(User operatorUser, RegistrationSubmission submission, CancellationToken ct)
    {
        var payload = LivestockContract.SigningPayload(submission.FaceImage, submission.FlankImage, submission.MetadataJson);
        if (!await IsSignedByOperatorAsync(operatorUser, submission, payload, ct))
            // 403, not 401: the operator is signed in; it's the device signature that failed.
            return Refuse(StatusCodes.Status403Forbidden, RegistrationStatuses.Rejected, "This registration isn't signed by one of your registered devices.");

        // The same upload again -- a retry after a dropped connection -- gets the original answer.
        var submissionHash = LivestockContract.Sha256Hex(payload);
        if (await PreviousResponseAsync(submissionHash, ct) is { } previous)
            return new RegistrationOutcome(StatusCodes.Status200OK, previous);

        var problems = new List<string>();
        var metadata = ParseMetadata(submission.MetadataJson, problems);
        if (metadata is null || problems.Count > 0)
            return Refuse(StatusCodes.Status400BadRequest, RegistrationStatuses.Rejected, problems);

        using var face = DecodePhoto(submission.FaceImage, "face", Options.MinFaceShortSide, problems);
        using var flank = DecodePhoto(submission.FlankImage, "side", Options.MinFlankShortSide, problems);
        if (face is null || flank is null)
            return Refuse(StatusCodes.Status422UnprocessableEntity, RegistrationStatuses.Rejected, problems);

        if (!assessor.IsConfigured)
            return Refuse(StatusCodes.Status503ServiceUnavailable, RegistrationStatuses.Unavailable, "Livestock analysis isn't set up on this server.");

        CattleAssessment assessment;
        try
        {
            assessment = await assessor.AssessAsync(
                CattleImages.ToJpeg(face, Options.Claude.MaxImageEdge),
                CattleImages.ToJpeg(flank, Options.Claude.MaxImageEdge),
                metadata.ManualOverrides,
                ct);
        }
        catch (Exception ex) when (ex is CattleAssessmentException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogError(ex, "Cattle assessment failed");
            return Refuse(StatusCodes.Status502BadGateway, RegistrationStatuses.Unavailable, "The photo analysis service isn't available right now. Try again in a few minutes.");
        }

        if (!assessment.IsCattle)
            return Refuse(StatusCodes.Status422UnprocessableEntity, RegistrationStatuses.Rejected,
                ["These photos don't look like the same head of cattle.", .. assessment.ImageQuality.Problems ?? []]);
        if (!assessment.ImageQuality.FaceUsable || !assessment.ImageQuality.FlankUsable)
            return Refuse(StatusCodes.Status422UnprocessableEntity, RegistrationStatuses.Rejected, QualityProblems(assessment));

        var embedding = embedder.IsAvailable ? embedder.Embed(face, assessment.MuzzleBox) : null;
        var muzzleHash = LivestockContract.Sha256Hex(embedding is null ? submission.FaceImage : MuzzleVectors.ToBytes(embedding));
        var faceRef = await images.SaveAsync(submission.FaceImage, CattleImages.Extension(submission.FaceImage)!, ct);
        var flankRef = await images.SaveAsync(submission.FlankImage, CattleImages.Extension(submission.FlankImage)!, ct);

        await IdentityGate.WaitAsync(ct);
        try
        {
            var identity = await ResolveIdentityAsync(operatorUser, metadata, embedding, ct);
            if (identity.Refusal is { } refusal)
                return refusal;

            var now = clock.GetUtcNow().UtcDateTime;
            var breed = ResolveBreed(assessment, metadata.ManualOverrides);
            var health = Screen(assessment);

            var cow = identity.Cow;
            var isNew = cow is null;
            if (cow is null)
            {
                cow = new LivestockAnimal
                {
                    CowId = await NewCowIdAsync(metadata.RanchId, ct),
                    RanchId = metadata.RanchId,
                    OwnerWallet = string.IsNullOrEmpty(metadata.OperatorWallet) ? null : metadata.OperatorWallet,
                    OwnerUserId = operatorUser.Id,
                    BiometricMuzzleHash = muzzleHash,
                    MuzzleEmbedding = embedding is null ? null : MuzzleVectors.ToBytes(embedding),
                    EmbeddingModel = embedding is null ? null : embedder.ModelId,
                    Breed = breed.Recorded,
                    BreedConfidence = (decimal)Math.Round(breed.Confidence, 4),
                    BreedSource = breed.Source,
                    Sex = CattleSex.Normalise(metadata.ManualOverrides?.Sex) ?? CattleSex.Unknown,
                    ApproximateAgeMonths = metadata.ManualOverrides?.ApproximateAgeMonths,
                    RegistrationTimestamp = now,
                    Latitude = (decimal)Math.Round(metadata.GpsTelemetry.Latitude, 6),
                    Longitude = (decimal)Math.Round(metadata.GpsTelemetry.Longitude, 6),
                    GpsAccuracyMeters = (decimal)Math.Round(metadata.GpsTelemetry.AccuracyMeters, 2),
                    FaceImageRef = faceRef,
                    MorphologyJson = ContractJson.Write(assessment.Morphology)
                };
                db.Livestock.Add(cow);
            }

            var audit = new LivestockHealthAudit
            {
                AuditId = Guid.NewGuid(),
                CowId = cow.CowId,
                TimestampUtc = now,
                ClientCapturedAtUtc = DateTimeOffset.FromUnixTimeSeconds(metadata.ClientTimestampUtc).UtcDateTime,
                BodyConditionScore = (decimal)health.BodyConditionScore,
                HealthRating = health.OverallHealthRating,
                HydrationStatus = health.HydrationStatus,
                AnomalySummary = health.DetectedAnomalies.Count == 0 ? null : ContractJson.Write(health.DetectedAnomalies),
                RequiresVetInspection = health.RequiresVetInspection,
                FaceImageRef = faceRef,
                FlankImageRef = flankRef,
                OperatorUserId = operatorUser.Id,
                OperatorKeyId = submission.KeyId!.Value,
                Signature = submission.Signature!,
                SubmissionHash = submissionHash,
                AssessmentModel = assessor.ModelId,
                AttestationHash = "",
                ResponseJson = ""
            };
            audit.AttestationHash = Attest(cow, audit, muzzleHash);

            var response = new LivestockRegistrationResponse(
                RegistrationStatuses.Success,
                cow.CowId,
                new BiometricsResult(muzzleHash, identity.EnrollmentStatus, Options.Muzzle.SimilarityThreshold, identity.ClosestSimilarity is { } s ? Math.Round(s, 3) : null),
                new PhenotypeResult(breed.Detected, Math.Round(breed.Confidence, 3), breed.Confirmed, cow.Breed, assessment.Morphology),
                health,
                "0x" + audit.AttestationHash);
            audit.ResponseJson = ContractJson.Write(response);
            db.HealthAudits.Add(audit);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                db.ChangeTracker.Clear();
                if (await PreviousResponseAsync(submissionHash, ct) is { } raced)
                    return new RegistrationOutcome(StatusCodes.Status200OK, raced);
                log.LogWarning(ex, "Registration collided with an existing animal");
                return Refuse(StatusCodes.Status409Conflict, RegistrationStatuses.Duplicate, "These photos have already been used to register an animal.");
            }

            if (isNew && embedding is not null)
                index.Add(cow.CowId, embedding, operatorUser.Id);
            return new RegistrationOutcome(StatusCodes.Status200OK, response);
        }
        finally
        {
            IdentityGate.Release();
        }
    }

    sealed record IdentityDecision(LivestockAnimal? Cow, string EnrollmentStatus, double? ClosestSimilarity, RegistrationOutcome? Refusal = null)
    {
        public static IdentityDecision Refused(RegistrationOutcome outcome) => new(null, "", null, outcome);
    }

    /// <summary>
    /// Which animal this is. A muzzle matching one of the operator's own animals makes this a
    /// health audit of it; a muzzle matching someone else's is refused -- the spec's guard against
    /// double registration across pastoral boundaries.
    /// </summary>
    async Task<IdentityDecision> ResolveIdentityAsync(User operatorUser, LivestockRegistrationMetadata metadata, float[]? embedding, CancellationToken ct)
    {
        var threshold = Options.Muzzle.SimilarityThreshold;

        if (metadata.ExistingCowId is { Length: > 0 } cowId)
        {
            var cow = await db.Livestock.FirstOrDefaultAsync(c => c.CowId == cowId && c.OwnerUserId == operatorUser.Id && c.IsActive, ct);
            if (cow is null)
                return IdentityDecision.Refused(Refuse(StatusCodes.Status422UnprocessableEntity, RegistrationStatuses.Rejected, $"You don't have an animal registered as {cowId}."));
            if (embedding is null || cow.MuzzleEmbedding is null || cow.EmbeddingModel != embedder.ModelId)
                return new IdentityDecision(cow, EnrollmentStatuses.UnverifiedIdentity, null);

            var similarity = MuzzleVectors.Cosine(embedding, MuzzleVectors.FromBytes(cow.MuzzleEmbedding));
            return similarity >= threshold
                ? new IdentityDecision(cow, EnrollmentStatuses.ExistingAnimal, similarity)
                : IdentityDecision.Refused(Refuse(StatusCodes.Status422UnprocessableEntity, RegistrationStatuses.Rejected,
                    $"This muzzle doesn't match {cowId}. Check you photographed the right animal."));
        }

        if (embedding is null)
            return new IdentityDecision(null, EnrollmentStatuses.UnverifiedIdentity, null);

        await index.SyncAsync(db, embedder.ModelId, ct);
        if (index.BestMatch(embedding) is not { } match)
            return new IdentityDecision(null, EnrollmentStatuses.NewRegistration, null);
        if (match.Similarity < threshold)
            return new IdentityDecision(null, EnrollmentStatuses.NewRegistration, match.Similarity);
        if (match.OwnerId != operatorUser.Id)
            return IdentityDecision.Refused(Refuse(StatusCodes.Status409Conflict, RegistrationStatuses.Duplicate,
                "This animal is already registered to another owner."));

        var existing = await db.Livestock.FirstAsync(c => c.CowId == match.CowId, ct);
        return new IdentityDecision(existing, EnrollmentStatuses.ExistingAnimal, match.Similarity);
    }

    (string Detected, double Confidence, bool Confirmed, string Recorded, string Source) ResolveBreed(CattleAssessment assessment, ManualOverrides? claims)
    {
        var confirmed = assessment.BreedConfidence >= Options.BreedConfidenceThreshold;
        var claimed = CattleBreeds.Normalise(claims?.ClaimedBreed);
        var (recorded, source) = confirmed ? (assessment.DetectedBreed, BreedFromModel)
            : claimed is not null ? (claimed, BreedFromClaim)
            : (assessment.DetectedBreed, BreedUnconfirmed);
        return (assessment.DetectedBreed, assessment.BreedConfidence, confirmed, recorded, source);
    }

    /// <summary>
    /// The model's health reading, with registry rules on top: some signs always call for a vet, and
    /// an animal that needs one is never rated better than FAIR.
    /// </summary>
    static HealthScreeningResult Screen(CattleAssessment a)
    {
        var requiresVet = a.RequiresVetInspection
            || a.Anomalies.Any(x => NeedsVet.Contains(x.Type) && x.Confidence >= 0.5)
            || a.HydrationStatus == HydrationStatuses.Severe
            || a.BodyConditionScore < 3
            || a.BodyConditionScore > 8;

        var rating = requiresVet && HealthRatings.Rank(a.OverallHealthRating) < HealthRatings.Rank(HealthRatings.Fair)
            ? HealthRatings.Fair
            : a.OverallHealthRating;

        return new HealthScreeningResult(a.BodyConditionScore, "1-9", a.HydrationStatus, a.Anomalies, requiresVet, rating);
    }

    async Task<bool> IsSignedByOperatorAsync(User operatorUser, RegistrationSubmission submission, byte[] payload, CancellationToken ct)
    {
        if (submission.KeyId is not { } keyId || string.IsNullOrEmpty(submission.Signature))
            return false;

        var key = await db.OperatorKeys.AsNoTracking()
            .FirstOrDefaultAsync(k => k.Id == keyId && k.UserId == operatorUser.Id && k.RevokedAt == null, ct);
        if (key is null)
            return false;

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(submission.Signature);
        }
        catch (FormatException)
        {
            return false;
        }

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(key.PublicKey, out _);
        return ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256);
    }

    async Task<LivestockRegistrationResponse?> PreviousResponseAsync(string submissionHash, CancellationToken ct)
    {
        var json = await db.HealthAudits.AsNoTracking()
            .Where(a => a.SubmissionHash == submissionHash)
            .Select(a => a.ResponseJson)
            .FirstOrDefaultAsync(ct);
        return json is null ? null : ContractJson.Read<LivestockRegistrationResponse>(json);
    }

    LivestockRegistrationMetadata? ParseMetadata(string json, List<string> problems)
    {
        LivestockRegistrationMetadata? metadata;
        try
        {
            metadata = string.IsNullOrWhiteSpace(json) ? null : ContractJson.Read<LivestockRegistrationMetadata>(json);
        }
        catch (JsonException)
        {
            metadata = null;
        }
        if (metadata is null)
        {
            problems.Add("The metadata part is missing or isn't valid JSON.");
            return null;
        }

        if (metadata.RanchId is null || !RanchPattern().IsMatch(metadata.RanchId))
            problems.Add("ranchId must look like urn:ndeipi:ranch:zm-st-041.");
        if (metadata.OperatorWallet is { Length: > 0 } wallet && !WalletPattern().IsMatch(wallet))
            problems.Add("operatorWallet must be a 0x address of 40 hex digits.");

        var now = clock.GetUtcNow();
        if (metadata.ClientTimestampUtc is < 0 or > 253402300799)
        {
            problems.Add("clientTimestampUtc isn't a valid Unix time.");
        }
        else
        {
            var captured = DateTimeOffset.FromUnixTimeSeconds(metadata.ClientTimestampUtc);
            if (captured > now + Options.ClockTolerance)
                problems.Add("The capture time is in the future. Check the phone's date and time.");
            else if (captured < now - Options.MaxCaptureAge)
                problems.Add($"Photos must be sent within {Options.MaxCaptureAge.TotalDays:0} days of being taken. Take new ones.");
        }

        if (metadata.GpsTelemetry is not { } gps
            || !double.IsFinite(gps.Latitude) || gps.Latitude is < -90 or > 90
            || !double.IsFinite(gps.Longitude) || gps.Longitude is < -180 or > 180
            || !double.IsFinite(gps.AccuracyMeters) || gps.AccuracyMeters < 0)
            problems.Add("gpsTelemetry needs a valid latitude, longitude and accuracy.");

        if (metadata.ManualOverrides is { } claims)
        {
            if (claims.ApproximateAgeMonths is < 0 or > 360)
                problems.Add("approximateAgeMonths must be between 0 and 360.");
            if (claims.ClaimedBreed is { Length: > 40 })
                problems.Add("claimedBreed is too long.");
            if (!string.IsNullOrEmpty(claims.Sex) && CattleSex.Normalise(claims.Sex) is null)
                problems.Add("sex must be Female, Male or Unknown.");
        }

        if (metadata.ExistingCowId is { Length: > 64 })
            problems.Add("existingCowId is too long.");
        return metadata;
    }

    SKBitmap? DecodePhoto(byte[] data, string name, int minShortSide, List<string> problems)
    {
        if (data.Length == 0)
        {
            problems.Add($"The {name} photo is missing.");
            return null;
        }
        if (data.Length > Options.MaxImageBytes)
        {
            problems.Add($"The {name} photo is larger than {Options.MaxImageBytes / (1024 * 1024)} MB.");
            return null;
        }
        if (CattleImages.Extension(data) is null)
        {
            problems.Add($"The {name} photo must be a JPEG or PNG.");
            return null;
        }

        var bitmap = CattleImages.DecodeUpright(data);
        if (bitmap is null)
        {
            problems.Add($"The {name} photo couldn't be read.");
            return null;
        }
        if (Math.Min(bitmap.Width, bitmap.Height) < minShortSide)
        {
            problems.Add($"The {name} photo is {bitmap.Width}×{bitmap.Height}; it needs at least {minShortSide} pixels on its shorter side.");
            bitmap.Dispose();
            return null;
        }
        return bitmap;
    }

    static List<string> QualityProblems(CattleAssessment assessment)
    {
        var problems = assessment.ImageQuality.Problems?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? [];
        if (problems.Count == 0)
        {
            if (!assessment.ImageQuality.FaceUsable)
                problems.Add("Retake the face photo: the muzzle must be sharp and fill the frame.");
            if (!assessment.ImageQuality.FlankUsable)
                problems.Add("Retake the side photo: the whole animal, shoulder to tail, must be in frame.");
        }
        return problems;
    }

    /// <summary>The spec's shape, urn:ndeipi:asset:cattle:zm-49204891, with the ranch's country code.</summary>
    async Task<string> NewCowIdAsync(string ranchId, CancellationToken ct)
    {
        var country = RanchPattern().Match(ranchId).Groups["country"].Value;
        while (true)
        {
            var cowId = $"urn:ndeipi:asset:cattle:{country}-{RandomNumberGenerator.GetInt32(10_000_000, 100_000_000)}";
            if (!await db.Livestock.AnyAsync(c => c.CowId == cowId, ct))
                return cowId;
        }
    }

    /// <summary>
    /// SHA-256 over the record's identifying facts, the evidence (photo hashes) and the operator's
    /// signature -- small enough to anchor on-chain, and enough to show a record wasn't altered.
    /// </summary>
    static string Attest(LivestockAnimal cow, LivestockHealthAudit audit, string muzzleHash) =>
        LivestockContract.Sha256Hex(Encoding.UTF8.GetBytes(string.Join('\n',
            "ndeipi-livestock-attestation-v1",
            cow.CowId,
            cow.RanchId,
            cow.OwnerUserId.ToString("N"),
            muzzleHash,
            cow.Breed,
            audit.AuditId.ToString("N"),
            audit.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
            audit.BodyConditionScore.ToString("0.0", CultureInfo.InvariantCulture),
            audit.HealthRating,
            audit.HydrationStatus,
            audit.AnomalySummary ?? "[]",
            audit.FaceImageRef,
            audit.FlankImageRef,
            audit.SubmissionHash,
            audit.OperatorKeyId.ToString("N"),
            audit.Signature)));

    static RegistrationOutcome Refuse(int statusCode, string status, params IReadOnlyList<string> problems) =>
        new(statusCode, LivestockRegistrationResponse.Failure(status, problems));
}
