using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace CapabilityExplorer.Interpretation.BoundedClassification;

internal static class ClassificationJson
{
    internal static bool TryParse(
        ReadOnlySpan<byte> utf8,
        int maximumBytes,
        out JsonDocument? document,
        out ClassificationReason reason)
    {
        document = null;
        if (utf8.Length > maximumBytes)
        {
            reason = ClassificationReason.PayloadTooLarge;
            return false;
        }

        try
        {
            document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            reason = ClassificationReason.None;
            return true;
        }
        catch (JsonException)
        {
            reason = ClassificationReason.InvalidInput;
            return false;
        }
    }

    internal static bool TryGetExactProperties(
        JsonElement element,
        ReadOnlySpan<string> expectedNames,
        out Dictionary<string, JsonElement> properties)
    {
        properties = new Dictionary<string, JsonElement>(expectedNames.Length, StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var knownNames = new HashSet<string>(expectedNames.ToArray(), StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!knownNames.Contains(property.Name) ||
                property.Value.ValueKind == JsonValueKind.Null ||
                !properties.TryAdd(property.Name, property.Value))
            {
                return false;
            }
        }

        return properties.Count == expectedNames.Length;
    }

    internal static bool TryGetString(JsonElement element, int maximumLength, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var parsed = element.GetString();
        if (parsed is null || parsed.Length > maximumLength)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    internal static bool IsIdentifier(string value) =>
        value.Length is > 0 and <= 64 && value.All(static character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.' or '-');

    internal static bool IsSafeAscii(string value) =>
        value.All(static character => character is >= ' ' and <= '~');

    internal static bool TryGetFiniteDouble(JsonElement element, out double value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Number ||
            !double.TryParse(element.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        return double.IsFinite(value);
    }

    internal static bool TryGetFiniteDoubleArray(
        JsonElement element,
        int expectedLength,
        out ImmutableArray<double> values)
    {
        values = ImmutableArray<double>.Empty;
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != expectedLength)
        {
            return false;
        }

        var builder = ImmutableArray.CreateBuilder<double>(expectedLength);
        foreach (var item in element.EnumerateArray())
        {
            if (!TryGetFiniteDouble(item, out var value))
            {
                return false;
            }

            builder.Add(value);
        }

        values = builder.MoveToImmutable();
        return true;
    }
}
