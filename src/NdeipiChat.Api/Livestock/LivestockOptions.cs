namespace NdeipiChat.Api.Livestock;

public sealed class LivestockOptions
{
    public const string Section = "Livestock";

    /// <summary>Where uploaded photos are kept, relative to the content root unless absolute.</summary>
    public string ImageStoragePath { get; set; } = "App_Data/livestock-images";

    /// <summary>The spec asks for a face capture of at least 1080p.</summary>
    public int MinFaceShortSide { get; set; } = 1080;

    public int MinFlankShortSide { get; set; } = 720;

    public long MaxImageBytes { get; set; } = 15 * 1024 * 1024;

    /// <summary>Captures queue on the phone while offline; they must still arrive within this long.</summary>
    public TimeSpan MaxCaptureAge { get; set; } = TimeSpan.FromDays(30);

    public TimeSpan ClockTolerance { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>The spec's "confidence scoring (≥ 90%)": below it, the farmer's claimed breed is kept.</summary>
    public double BreedConfidenceThreshold { get; set; } = 0.90;

    public ClaudeVisionOptions Claude { get; set; } = new();

    public MuzzleModelOptions Muzzle { get; set; } = new();
}

/// <summary>Claude grades breed, morphology, body condition and visible health from the two photos.</summary>
public sealed class ClaudeVisionOptions
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.anthropic.com/v1/";
    public string Model { get; set; } = "claude-opus-5";
    public int MaxTokens { get; set; } = 2048;

    /// <summary>Photos are downscaled to this long edge before sending; Claude gains nothing from more.</summary>
    public int MaxImageEdge { get; set; } = 1568;

    public int MaxAttempts { get; set; } = 3;

    public bool IsConfigured => ApiKey.Length > 0;
}

/// <summary>
/// An ONNX model mapping a muzzle image to an embedding vector (the spec's 512-dimensional
/// muzzle-print vector). Without one, animals are registered but can't be checked for duplicates.
/// </summary>
public sealed class MuzzleModelOptions
{
    public string ModelPath { get; set; } = "";

    /// <summary>Recorded with each embedding; defaults to the model's file name.</summary>
    public string ModelId { get; set; } = "";

    public int InputWidth { get; set; } = 224;
    public int InputHeight { get; set; } = 224;

    /// <summary>Per-channel RGB normalisation; ImageNet's when not set.</summary>
    public float[]? Mean { get; set; }

    public float[]? Std { get; set; }

    /// <summary>Cosine similarity at or above which two muzzles are the same animal.</summary>
    public double SimilarityThreshold { get; set; } = 0.90;

    /// <summary>Margin added around the muzzle box Claude finds, as a fraction of its size.</summary>
    public double CropPadding { get; set; } = 0.10;
}
