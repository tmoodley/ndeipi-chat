using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ndeipi.Payments.Api;

/// <summary>
/// The wire format: snake_case fields, snake_case enum strings, nulls left out, and every decimal
/// as a string (FR-API-02) so no amount ever passes through binary floating point.
/// </summary>
public static class PaymentsJson
{
    public static readonly JsonSerializerOptions Options = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions o)
    {
        o.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        o.DictionaryKeyPolicy = null;
        o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        o.NumberHandling = JsonNumberHandling.Strict;
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        o.Converters.Add(new DecimalStringConverter());
        return o;
    }

    /// <summary>Reads and writes decimals as strings like "25.00"; refuses JSON numbers and exponents.</summary>
    public sealed class DecimalStringConverter : JsonConverter<decimal>
    {
        const NumberStyles Style = NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign;

        public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException("Amounts are decimal strings, not JSON numbers.");
            return decimal.TryParse(reader.GetString(), Style, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new JsonException("Not a decimal string.");
        }

        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }
}
