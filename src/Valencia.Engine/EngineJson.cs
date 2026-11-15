using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Valencia.Engine;

public static class EngineJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options)
        ?? throw new JsonException("Null request.");

    public static long Int64(string value)
    {
        long parsed = long.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        if (parsed.ToString(CultureInfo.InvariantCulture) != value)
            throw new FormatException("Int64 values must use canonical decimal strings.");
        return parsed;
    }
    public static string Exact(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            AllowOutOfOrderMetadataProperties = true,
            MaxDepth = 64
        };
        options.Converters.Add(new ExactIntegerConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private sealed class ExactIntegerConverter : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String ? Int64(reader.GetString()!) : throw new JsonException("Int64 requires a decimal string.");
        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) => writer.WriteStringValue(Exact(value));
    }
}
