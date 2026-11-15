using System.Text.Json;
using System.Text.Json.Serialization;
using Deliverance.Core.Serialization;

namespace Valencia.Persistence;

/// <summary>Application payload serializer inside Deliverance's versioned/checksummed container, not a second file format.</summary>
public sealed class ProjectJsonSerializer : ISaveSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    public byte Id => 42;
    public string Name => "valencia-json-v1";
    public byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);
    public T Deserialize<T>(ReadOnlyMemory<byte> bytes) => JsonSerializer.Deserialize<T>(bytes.Span, Options)
        ?? throw new InvalidDataException("Project payload is null.");
    public byte[] Serialize(object value, Type type) => JsonSerializer.SerializeToUtf8Bytes(value, type, Options);
    public object Deserialize(Type type, ReadOnlyMemory<byte> bytes) => JsonSerializer.Deserialize(bytes.Span, type, Options)
        ?? throw new InvalidDataException("Project payload is null.");
}
