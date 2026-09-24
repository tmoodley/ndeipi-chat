namespace NdeipiChat.Api.Data;

/// <summary>
/// A registered animal -- the spec's <c>LivestockMaster</c>, plus the owner's user id, the muzzle
/// embedding used for identity checks, and the registration's supporting details.
/// </summary>
public sealed class LivestockAnimal
{
    public required string CowId { get; set; }
    public required string RanchId { get; set; }

    /// <summary>Optional here (the spec has NOT NULL): farmers without a 0x wallet can still register.</summary>
    public string? OwnerWallet { get; set; }

    public Guid OwnerUserId { get; set; }

    /// <summary>SHA-256 of the muzzle embedding -- or of the face photo when no muzzle model is configured.</summary>
    public required string BiometricMuzzleHash { get; set; }

    /// <summary>L2-normalised float32 vector, little-endian. Null when no muzzle model was configured.</summary>
    public byte[]? MuzzleEmbedding { get; set; }

    /// <summary>Embeddings are only comparable within one model.</summary>
    public string? EmbeddingModel { get; set; }

    public required string Breed { get; set; }
    public decimal BreedConfidence { get; set; }

    /// <summary>MODEL (confidence met the threshold), CLAIMED (farmer's claim kept) or MODEL_UNCONFIRMED.</summary>
    public required string BreedSource { get; set; }

    public required string Sex { get; set; }
    public int? ApproximateAgeMonths { get; set; }
    public DateTime RegistrationTimestamp { get; set; }
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public decimal GpsAccuracyMeters { get; set; }
    public required string FaceImageRef { get; set; }
    public required string MorphologyJson { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// One screening of an animal -- the spec's <c>LivestockHealthAudit</c>. The registration itself is
/// the first; later re-scans add more. Each carries the operator's signature, and the response
/// sent back, so a resent upload gets the same answer.
/// </summary>
public sealed class LivestockHealthAudit
{
    public Guid AuditId { get; set; }
    public required string CowId { get; set; }
    public DateTime TimestampUtc { get; set; }
    public DateTime ClientCapturedAtUtc { get; set; }
    public decimal BodyConditionScore { get; set; }
    public required string HealthRating { get; set; }
    public required string HydrationStatus { get; set; }

    /// <summary>The detected anomalies as JSON; null when there were none.</summary>
    public string? AnomalySummary { get; set; }

    public bool RequiresVetInspection { get; set; }
    public required string FaceImageRef { get; set; }
    public required string FlankImageRef { get; set; }

    /// <summary>SHA-256 hex (the API returns it with a 0x prefix).</summary>
    public required string AttestationHash { get; set; }

    public Guid OperatorUserId { get; set; }
    public Guid OperatorKeyId { get; set; }
    public required string Signature { get; set; }

    /// <summary>SHA-256 of the signed payload: the same upload sent twice is recognised.</summary>
    public required string SubmissionHash { get; set; }

    public required string AssessmentModel { get; set; }
    public required string ResponseJson { get; set; }
}

/// <summary>A device key an operator signs registrations with. The private half never leaves the phone.</summary>
public sealed class OperatorSigningKey
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required byte[] PublicKey { get; set; }
    public string? Label { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
