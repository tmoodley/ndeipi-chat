using System.Security.Cryptography;
using System.Text;

namespace NdeipiChat.Contracts;

/// <summary>
/// Livestock registry (functional addendum "Ndeipi Super App Livestock Registry Service"): a cow is
/// registered, or re-screened, from a frontal face/muzzle photo and a lateral flank photo.
/// </summary>
public static class LivestockContract
{
    public const string BasePath = "/api/v1/livestock";
    public const string RegisterPath = BasePath + "/register";
    public const string OperatorKeysPath = BasePath + "/operator-keys";
    public const string CowsPath = BasePath + "/cows";
    public const string ImagesPath = BasePath + "/images";

    public const string SignatureHeader = "X-Ndeipi-Signature";

    /// <summary>Which of the operator's registered device keys made the signature.</summary>
    public const string KeyIdHeader = "X-Ndeipi-Key-Id";

    public const string FaceImageField = "face_image";
    public const string FlankImageField = "flank_image";
    public const string MetadataField = "metadata";

    public const string SigningScheme = "ndeipi-livestock-register-v1";

    /// <summary>
    /// The bytes an operator signs with their device key (ECDSA P-256, SHA-256, signature as IEEE
    /// P1363 r‖s in base64): the scheme name and the SHA-256 of each multipart part, one per line.
    /// The signature therefore covers exactly the bytes uploaded.
    /// </summary>
    public static byte[] SigningPayload(byte[] faceImage, byte[] flankImage, string metadataJson) =>
        Encoding.UTF8.GetBytes(string.Join('\n',
            SigningScheme,
            Sha256Hex(faceImage),
            Sha256Hex(flankImage),
            Sha256Hex(Encoding.UTF8.GetBytes(metadataJson))));

    public static string Sha256Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>"zm-st-041" becomes "urn:ndeipi:ranch:zm-st-041".</summary>
    public static string RanchUrn(string code) => $"urn:ndeipi:ranch:{code.Trim().ToLowerInvariant()}";
}

public sealed record GpsTelemetry(double Latitude, double Longitude, double AccuracyMeters);

public sealed record ManualOverrides(string? ClaimedBreed, string? Sex, int? ApproximateAgeMonths);

/// <summary>
/// The spec's <c>metadata</c> part. <see cref="ExistingCowId"/> is an addition: set it for a routine
/// health audit of an animal already on the register.
/// </summary>
/// <param name="ClientTimestampUtc">When the photos were taken, in Unix seconds.</param>
public sealed record LivestockRegistrationMetadata(
    string RanchId,
    string? OperatorWallet,
    long ClientTimestampUtc,
    GpsTelemetry GpsTelemetry,
    ManualOverrides? ManualOverrides,
    string? ExistingCowId = null);

/// <summary>The spec's response. On refusal only <see cref="Status"/> and <see cref="Problems"/> are set.</summary>
public sealed record LivestockRegistrationResponse(
    string Status,
    string? CowId,
    BiometricsResult? Biometrics,
    PhenotypeResult? Phenotype,
    HealthScreeningResult? HealthScreening,
    string? AttestationHash,
    IReadOnlyList<string>? Problems = null)
{
    public static LivestockRegistrationResponse Failure(string status, IReadOnlyList<string> problems) =>
        new(status, null, null, null, null, null, problems);
}

/// <param name="SimilarityThreshold">The muzzle similarity at or above which two captures are the same animal.</param>
/// <param name="ClosestMatchSimilarity">The best similarity found against the register, if a muzzle model ran.</param>
public sealed record BiometricsResult(
    string FaceVectorSha256,
    string EnrollmentStatus,
    double SimilarityThreshold,
    double? ClosestMatchSimilarity);

/// <param name="BreedConfirmed">Model confidence reached the registry's threshold (90%).</param>
/// <param name="RecordedBreed">What the register holds: the detected breed if confirmed, otherwise the farmer's claim.</param>
public sealed record PhenotypeResult(
    string DetectedBreed,
    double BreedConfidence,
    bool BreedConfirmed,
    string RecordedBreed,
    MorphologicalTraits MorphologicalTraits);

public sealed record MorphologicalTraits(
    bool HasThoracicHump,
    string DewlapProminence,
    string HornState,
    string? EarShape,
    string? CoatPattern);

public sealed record HealthScreeningResult(
    double BodyConditionScore,
    string BcsScale,
    string HydrationStatus,
    IReadOnlyList<DetectedAnomaly> DetectedAnomalies,
    bool RequiresVetInspection,
    string OverallHealthRating);

public sealed record DetectedAnomaly(string Type, string Location, double Confidence, string? Note);

/// <param name="PublicKey">SubjectPublicKeyInfo (DER), base64, of a P-256 key generated on the device.</param>
public sealed record OperatorKeyRequest(string PublicKey, string? Label);

public sealed record OperatorKeyDto(Guid KeyId, DateTimeOffset CreatedAt);

public sealed record CowSummaryDto(
    string CowId,
    string RanchId,
    string Breed,
    string Sex,
    int? ApproximateAgeMonths,
    DateTimeOffset RegisteredAt,
    double LatestBodyConditionScore,
    string LatestHealthRating,
    bool RequiresVetInspection,
    string FaceImageRef);

public sealed record CowDetailDto(
    string CowId,
    string RanchId,
    string Breed,
    double BreedConfidence,
    string BreedSource,
    string Sex,
    int? ApproximateAgeMonths,
    DateTimeOffset RegisteredAt,
    double Latitude,
    double Longitude,
    string? OwnerWallet,
    bool BiometricallyEnrolled,
    MorphologicalTraits MorphologicalTraits,
    string FaceImageRef,
    IReadOnlyList<HealthAuditDto> Audits);

public sealed record HealthAuditDto(
    Guid AuditId,
    DateTimeOffset TimestampUtc,
    double BodyConditionScore,
    string HealthRating,
    string HydrationStatus,
    IReadOnlyList<DetectedAnomaly> DetectedAnomalies,
    bool RequiresVetInspection,
    string FlankImageRef,
    string AttestationHash);

public static class RegistrationStatuses
{
    public const string Success = "SUCCESS";
    public const string Rejected = "REJECTED";
    public const string Duplicate = "DUPLICATE";
    public const string Unavailable = "UNAVAILABLE";
}

public static class EnrollmentStatuses
{
    /// <summary>A new animal; its muzzle matched nothing on the register.</summary>
    public const string NewRegistration = "NEW_REGISTRATION";

    /// <summary>The muzzle matched one of the operator's own animals: recorded as a health audit.</summary>
    public const string ExistingAnimal = "EXISTING_ANIMAL";

    /// <summary>No muzzle model is configured, so identity couldn't be checked against the register.</summary>
    public const string UnverifiedIdentity = "UNVERIFIED_IDENTITY";
}

public static class HealthRatings
{
    public const string Prime = "PRIME";
    public const string Good = "GOOD";
    public const string Fair = "FAIR";
    public const string Poor = "POOR";
    public const string Critical = "CRITICAL";

    public static readonly IReadOnlyList<string> All = [Prime, Good, Fair, Poor, Critical];

    /// <summary>0 = best.</summary>
    public static int Rank(string rating) => All.IndexOf(rating) is var i and >= 0 ? i : All.Count - 1;

    static int IndexOf(this IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == value)
                return i;
        }
        return -1;
    }
}

public static class HydrationStatuses
{
    public const string Normal = "NORMAL";
    public const string Mild = "MILD_DEHYDRATION";
    public const string Severe = "SEVERE_DEHYDRATION";

    public static readonly IReadOnlyList<string> All = [Normal, Mild, Severe];
}

public static class AnomalyTypes
{
    public const string Ticks = "TICK_INFESTATION";
    public const string LumpySkin = "LUMPY_SKIN_NODULES";
    public const string Ringworm = "RINGWORM";
    public const string Wound = "WOUND";
    public const string OcularDischarge = "OCULAR_DISCHARGE";
    public const string NasalDischarge = "NASAL_DISCHARGE";
    public const string CornealOpacity = "CORNEAL_OPACITY";
    public const string SunkenEyes = "SUNKEN_EYES";
    public const string Lameness = "LAMENESS_POSTURE";
    public const string Other = "OTHER";

    public static readonly IReadOnlyList<string> All =
        [Ticks, LumpySkin, Ringworm, Wound, OcularDischarge, NasalDischarge, CornealOpacity, SunkenEyes, Lameness, Other];
}

public static class CattleBreeds
{
    public const string Crossbreed = "Crossbreed";
    public const string Other = "Other";

    /// <summary>The spec's supported taxonomies, plus Crossbreed and Other.</summary>
    public static readonly IReadOnlyList<string> All =
        ["Boran", "Brahman", "Tuli", "Mashona", "Nguni", "Afrikaner", "Angus", "Simmental", "Holstein-Friesian", Crossbreed, Other];

    /// <summary>The list's spelling of a breed name, or null if it isn't one.</summary>
    public static string? Normalise(string? breed)
    {
        var name = breed?.Trim();
        if (string.IsNullOrEmpty(name))
            return null;
        if (name.Equals("Holstein", StringComparison.OrdinalIgnoreCase) || name.Equals("Friesian", StringComparison.OrdinalIgnoreCase))
            return "Holstein-Friesian";
        return All.FirstOrDefault(b => b.Equals(name, StringComparison.OrdinalIgnoreCase));
    }
}

public static class CattleSex
{
    public const string Female = "Female";
    public const string Male = "Male";
    public const string Unknown = "Unknown";

    public static readonly IReadOnlyList<string> All = [Female, Male, Unknown];

    public static string? Normalise(string? sex) => All.FirstOrDefault(s => s.Equals(sex?.Trim(), StringComparison.OrdinalIgnoreCase));
}
