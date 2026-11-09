using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CapabilityExplorer.Explorer;

public static class OwnershipCanonical
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 32,
    };

    public static byte[] Encode<T>(T value) => Encode(JsonSerializer.SerializeToElement(value, Json));

    public static string Hash<T>(T value) => Digest(Encode(value));

    public static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static byte[] Encode(JsonElement value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) Write(writer, value);
        return buffer.ToArray();
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                var elements = value.EnumerateArray().Select(Encode).ToArray();
                Array.Sort(elements, Comparer<byte[]>.Create((left, right) => left.AsSpan().SequenceCompareTo(right)));
                foreach (var element in elements) writer.WriteRawValue(element);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                if (!value.TryGetInt64(out var number)) throw new JsonException("Ownership canonical values must be integers.");
                writer.WriteNumberValue(number);
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                writer.WriteBooleanValue(value.GetBoolean());
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException("Unsupported ownership canonical value.");
        }
    }
}
