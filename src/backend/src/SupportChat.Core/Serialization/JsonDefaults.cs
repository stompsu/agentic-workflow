using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SupportChat.Core.Serialization;

/// <summary>
/// One JSON configuration for the API and Cosmos: camelCase names, camelCase string enums, timestamps as fixed-width
/// ISO 8601 UTC strings ("2026-10-25T10:15:30.123Z"), which also sort correctly as strings in Cosmos ORDER BY.
/// </summary>
public static class JsonDefaults
{
    public static JsonSerializerOptions Create(bool omitNulls = true)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = omitNulls ? JsonIgnoreCondition.WhenWritingNull : JsonIgnoreCondition.Never,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new UtcMillisDateTimeOffsetConverter());
        return options;
    }

    public static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private sealed class UtcMillisDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(FormatTimestamp(value));
    }
}
