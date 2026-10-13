using System.Text.Json;

namespace Ndeipi.Payments.Api;

/// <summary>
/// Operations in the contract that a later milestone builds (SRS §10.1). They are mapped now so the
/// route table matches openapi.yaml from the start (the contract test checks it), and answer
/// <c>501 not_implemented</c> until built.
/// </summary>
public static class Stubs
{
    // A route handler rather than a RequestDelegate, so group filters (such as sandbox_only) still run.
    public static Func<IResult> Milestone(string milestone) => () => throw PaymentsException.NotImplemented(milestone);
}

/// <summary>Enum values in query strings use the wire spelling (<c>not_started</c>), as bodies do.</summary>
public static class WireEnum
{
    /// <summary>The wire spelling of an enum value: <c>NotStarted</c> as <c>not_started</c>.</summary>
    public static string Name<T>(T value) where T : struct, Enum =>
        JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(value, PaymentsJson.Options))!;

    public static T? Parse<T>(string? value, string field) where T : struct, Enum
    {
        if (string.IsNullOrEmpty(value))
            return null;
        try
        {
            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value), PaymentsJson.Options);
        }
        catch (JsonException)
        {
            throw PaymentsException.BadRequest($"{value} is not a valid {field}.", field);
        }
    }
}
