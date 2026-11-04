using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CapabilityExplorer.Contracts.Collection;

internal static class CollectionCanonicalization
{
    public const string Recipe = "ce.collection.v1/utf16-escaped-json";

    public static string Digest(CollectionContent content)
    {
        var output = new StringBuilder(Recipe).Append('\0');
        Write(JsonSerializer.SerializeToElement(content, CollectionJson.Options), output);
        if (output.Length > 6 * CollectionRules.MaxPlanBytes + Recipe.Length + 1)
            throw new JsonException("canonical collection metadata exceeds its bound");
        return CollectionRules.Digest(output.ToString());
    }

    private static void Write(JsonElement value, StringBuilder output)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                output.Append('{');
                var separator = "";
                foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    output.Append(separator);
                    Quote(property.Name, output);
                    output.Append(':');
                    Write(property.Value, output);
                    separator = ",";
                }
                output.Append('}');
                break;
            case JsonValueKind.Array:
                output.Append('[');
                for (var index = 0; index < value.GetArrayLength(); index++)
                {
                    if (index > 0) output.Append(',');
                    Write(value[index], output);
                }
                output.Append(']');
                break;
            case JsonValueKind.String:
                Quote(value.GetString()!, output);
                break;
            case JsonValueKind.Number:
                if (!value.TryGetDecimal(out var number) || decimal.Truncate(number) != number ||
                    number is < -9007199254740991m or > 9007199254740991m)
                    throw new JsonException("canonical collection numbers must be safe integers");
                output.Append(number.ToString("0", CultureInfo.InvariantCulture));
                break;
            case JsonValueKind.True: output.Append("true"); break;
            case JsonValueKind.False: output.Append("false"); break;
            case JsonValueKind.Null: output.Append("null"); break;
            default: throw new JsonException("unsupported canonical collection value");
        }
    }

    private static void Quote(string value, StringBuilder output)
    {
        output.Append('"');
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character) && (index + 1 == value.Length || !char.IsLowSurrogate(value[index + 1])) ||
                char.IsLowSurrogate(character) && (index == 0 || !char.IsHighSurrogate(value[index - 1])))
                throw new JsonException("canonical collection strings must be well-formed Unicode");
            output.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
        }
        output.Append('"');
    }
}
