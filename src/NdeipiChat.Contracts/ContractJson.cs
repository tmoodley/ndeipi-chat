using System.Text.Json;
using System.Text.Json.Serialization;

namespace NdeipiChat.Contracts;

/// <summary>
/// The one JSON shape used on every wire -- HTTP, SignalR and stored payloads -- so a payload
/// written by the app reads back identically on the server and vice versa.
/// </summary>
public static class ContractJson
{
    public static readonly JsonSerializerOptions Options = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.PropertyNameCaseInsensitive = true;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        if (!options.Converters.OfType<JsonStringEnumConverter>().Any())
            options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static T? Read<T>(JsonElement element) => element.Deserialize<T>(Options);

    public static T? Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
}
