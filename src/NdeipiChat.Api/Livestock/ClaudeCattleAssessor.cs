using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using NdeipiChat.Contracts;

namespace Ndeipi.Api.Livestock;

/// <summary>What the vision model makes of the two photos.</summary>
public sealed record CattleAssessment(
    bool IsCattle,
    ImageQualityAssessment ImageQuality,
    string DetectedBreed,
    double BreedConfidence,
    MorphologicalTraits Morphology,
    double BodyConditionScore,
    string HydrationStatus,
    List<DetectedAnomaly> Anomalies,
    bool RequiresVetInspection,
    string OverallHealthRating,
    NormalizedBox? MuzzleBox,
    string? Notes);

public sealed record ImageQualityAssessment(bool FaceUsable, bool FlankUsable, List<string>? Problems);

public interface ICattleVisionAssessor
{
    bool IsConfigured { get; }
    string ModelId { get; }
    Task<CattleAssessment> AssessAsync(byte[] faceJpeg, byte[] flankJpeg, ManualOverrides? claims, CancellationToken ct);
}

public sealed class CattleAssessmentException(string message) : Exception(message);

/// <summary>
/// The spec's phenotype engine and health scoring, done by Claude: breed and morphology, the
/// 9-point body condition score, visible clinical signs, and a quality check on the photos.
/// A forced tool call makes the answer structured JSON rather than prose.
/// </summary>
public sealed class ClaudeCattleAssessor(HttpClient http, IOptions<LivestockOptions> options, ILogger<ClaudeCattleAssessor> log) : ICattleVisionAssessor
{
    public const string ToolName = "record_cattle_assessment";

    public bool IsConfigured => options.Value.Claude.IsConfigured;

    public string ModelId => options.Value.Claude.Model;

    public async Task<CattleAssessment> AssessAsync(byte[] faceJpeg, byte[] flankJpeg, ManualOverrides? claims, CancellationToken ct)
    {
        var claude = options.Value.Claude;
        var request = new JsonObject
        {
            ["model"] = claude.Model,
            ["max_tokens"] = claude.MaxTokens,
            ["system"] = SystemPrompt,
            ["tools"] = new JsonArray(new JsonObject
            {
                ["name"] = ToolName,
                ["description"] = "Record the assessment of the animal in the two photos.",
                ["input_schema"] = JsonNode.Parse(InputSchema)
            }),
            ["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = ToolName },
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(
                    Text("Photo 1: frontal view of the face and muzzle."),
                    Image(faceJpeg),
                    Text("Photo 2: lateral view of the body, shoulder to tailhead."),
                    Image(flankJpeg),
                    Text(ClaimsText(claims)))
            })
        };
        var json = request.ToJsonString();

        for (var attempt = 1; ; attempt++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "messages") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            using var response = await http.SendAsync(message, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode)
                return Sanitise(Parse(body));

            var retryable = (int)response.StatusCode is 429 or 500 or 502 or 503 or 529;
            if (!retryable || attempt >= claude.MaxAttempts)
            {
                log.LogError("Claude assessment failed: {Status} {Body}", (int)response.StatusCode, body);
                throw new CattleAssessmentException($"Claude returned {(int)response.StatusCode}.");
            }
            await Task.Delay(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
        }
    }

    static CattleAssessment Parse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.TryGetProperty("stop_reason", out var stop) && stop.GetString() == "max_tokens")
            throw new CattleAssessmentException("Claude ran out of tokens before finishing the assessment.");

        foreach (var block in root.GetProperty("content").EnumerateArray())
        {
            if (block.GetProperty("type").GetString() == "tool_use" && block.GetProperty("name").GetString() == ToolName)
                return block.GetProperty("input").Deserialize<CattleAssessment>(ContractJson.Options)
                    ?? throw new CattleAssessmentException("Claude returned an empty assessment.");
        }
        throw new CattleAssessmentException("Claude didn't return an assessment.");
    }

    /// <summary>Holds the answer to the ranges and vocabularies the rest of the system relies on.</summary>
    static CattleAssessment Sanitise(CattleAssessment a) => a with
    {
        ImageQuality = a.ImageQuality ?? new ImageQualityAssessment(false, false, ["The photos couldn't be assessed."]),
        DetectedBreed = CattleBreeds.Normalise(a.DetectedBreed) ?? CattleBreeds.Other,
        BreedConfidence = Math.Clamp(a.BreedConfidence, 0, 1),
        Morphology = a.Morphology ?? new MorphologicalTraits(false, "NONE", "UNKNOWN", null, null),
        BodyConditionScore = Math.Round(Math.Clamp(a.BodyConditionScore, 1, 9), 1),
        HydrationStatus = HydrationStatuses.All.Contains(a.HydrationStatus) ? a.HydrationStatus : HydrationStatuses.Normal,
        Anomalies = (a.Anomalies ?? [])
            .Select(x => x with { Type = AnomalyTypes.All.Contains(x.Type) ? x.Type : AnomalyTypes.Other, Confidence = Math.Clamp(x.Confidence, 0, 1) })
            .ToList(),
        OverallHealthRating = HealthRatings.All.Contains(a.OverallHealthRating) ? a.OverallHealthRating : HealthRatings.Fair
    };

    static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };

    static JsonObject Image(byte[] jpeg) => new()
    {
        ["type"] = "image",
        ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/jpeg", ["data"] = Convert.ToBase64String(jpeg) }
    };

    static string ClaimsText(ManualOverrides? claims) =>
        claims is null
            ? "The farmer made no claims about breed, sex or age."
            : $"The farmer says: breed {claims.ClaimedBreed ?? "not given"}, sex {claims.Sex ?? "not given"}, " +
              $"age {(claims.ApproximateAgeMonths is { } months ? $"about {months} months" : "not given")}. " +
              "Treat this as context, not evidence.";

    const string SystemPrompt = """
        You assess cattle for a livestock registry in southern and eastern Africa. You receive two
        photographs of one animal: a frontal photo of the face and muzzle, and a lateral photo of
        the whole body. Assess only what is visible, and record your assessment with the
        record_cattle_assessment tool.

        Photo quality. faceUsable only if the muzzle (nose pad and nostrils) is sharp, well lit and
        roughly head-on. flankUsable only if the full side of the animal, from shoulder to tailhead,
        is in frame. List each problem as a short instruction the farmer can act on, such as
        "Move closer so the muzzle fills the frame" or "Step back so the tail is in the photo".
        Set isCattle to false if either photo does not show cattle, or if the photos are clearly of
        different animals (for example a different coat colour or pattern).

        Breed. Judge from morphology (thoracic hump and cervicothoracic curvature, dewlap size, ear
        shape and droop, horn structure) together with coat pattern and pigmentation. Use Crossbreed
        for evident crosses. breedConfidence is your probability that the breed is right; keep it
        below 0.9 unless the features are unambiguous.

        Body condition. Use the 9-point scale (1 emaciated, 5 moderate, 9 obese), judged on the
        lateral photo from fat cover over the short ribs, spine, hooks and pin bones, and tailhead.
        Give one decimal place.

        Health. Report only signs you can see, each with your confidence: tick infestation,
        lumpy skin disease nodules, ringworm, fresh wounds, ocular or nasal discharge, corneal
        opacity, sunken eyes, or a posture suggesting lameness or skeletal defects. Judge hydration
        from the eyes and muzzle. Set requiresVetInspection when a veterinarian should see the
        animal.

        Muzzle box. Give the bounding box of the muzzle (nose pad with nostrils) in the face photo,
        as fractions of the image width and height from the top-left corner.
        """;

    static readonly string InputSchema = $$"""
        {
          "type": "object",
          "properties": {
            "isCattle": { "type": "boolean", "description": "Both photos show cattle, plausibly the same animal." },
            "imageQuality": {
              "type": "object",
              "properties": {
                "faceUsable": { "type": "boolean" },
                "flankUsable": { "type": "boolean" },
                "problems": { "type": "array", "items": { "type": "string" } }
              },
              "required": ["faceUsable", "flankUsable", "problems"]
            },
            "detectedBreed": { "type": "string", "enum": {{Enum(CattleBreeds.All)}} },
            "breedConfidence": { "type": "number", "minimum": 0, "maximum": 1 },
            "morphology": {
              "type": "object",
              "properties": {
                "hasThoracicHump": { "type": "boolean" },
                "dewlapProminence": { "type": "string", "enum": ["NONE", "LOW", "MEDIUM", "HIGH"] },
                "hornState": { "type": "string", "enum": ["HORNED", "POLLED", "SCURRED", "DEHORNED", "UNKNOWN"] },
                "earShape": { "type": "string", "description": "e.g. 'long and pendulous' or 'short and horizontal'" },
                "coatPattern": { "type": "string", "description": "Colour and pattern, e.g. 'white with black patches'" }
              },
              "required": ["hasThoracicHump", "dewlapProminence", "hornState"]
            },
            "bodyConditionScore": { "type": "number", "minimum": 1, "maximum": 9 },
            "hydrationStatus": { "type": "string", "enum": {{Enum(HydrationStatuses.All)}} },
            "anomalies": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "type": { "type": "string", "enum": {{Enum(AnomalyTypes.All)}} },
                  "location": { "type": "string", "description": "Where on the animal, e.g. 'left flank'" },
                  "confidence": { "type": "number", "minimum": 0, "maximum": 1 },
                  "note": { "type": "string" }
                },
                "required": ["type", "location", "confidence"]
              }
            },
            "requiresVetInspection": { "type": "boolean" },
            "overallHealthRating": { "type": "string", "enum": {{Enum(HealthRatings.All)}} },
            "muzzleBox": {
              "type": "object",
              "properties": {
                "x": { "type": "number", "minimum": 0, "maximum": 1 },
                "y": { "type": "number", "minimum": 0, "maximum": 1 },
                "width": { "type": "number", "minimum": 0, "maximum": 1 },
                "height": { "type": "number", "minimum": 0, "maximum": 1 }
              },
              "required": ["x", "y", "width", "height"]
            },
            "notes": { "type": "string" }
          },
          "required": ["isCattle", "imageQuality", "detectedBreed", "breedConfidence", "morphology", "bodyConditionScore",
                       "hydrationStatus", "anomalies", "requiresVetInspection", "overallHealthRating"]
        }
        """;

    static string Enum(IEnumerable<string> values) => JsonSerializer.Serialize(values);
}
