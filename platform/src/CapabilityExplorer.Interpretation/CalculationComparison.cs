using System.Text.Json;

namespace CapabilityExplorer.Interpretation;

/// <summary>One side of a comparison: a calculation record as stored in an artifact.</summary>
public sealed record CalculationSide(string Key, string Name, string RepositoryId, string CommitSha, string Language, JsonElement Raw);

public sealed record AttributeValue(string Status, string? Value, JsonElement? Detail);

public enum DifferenceKind
{
    /// <summary>Both sides state a value and the values differ.</summary>
    ValueDiffers,

    /// <summary>Same value, different detail (e.g. same aggregation, different smoothing constant).</summary>
    DetailDiffers,

    /// <summary>One side has evidence for a value, the other has none.</summary>
    KnownOnOneSide,
}

public sealed record Difference(string Id, string Attribute, DifferenceKind Kind, AttributeValue? Left, AttributeValue? Right);

public sealed record CalculationComparison(
    CalculationSide Left,
    CalculationSide Right,
    bool SameExpression,
    string LeftOutputType,
    string RightOutputType,
    IReadOnlyList<Difference> Differences,
    IReadOnlyList<string> Agreements,
    IReadOnlyList<string> UnknownOnBoth);

/// <summary>
/// Deterministic, attribute-by-attribute comparison of two calculations. This is the factual basis for any
/// explanation; it needs no model and is always available.
/// </summary>
public static class CalculationComparer
{
    private static readonly string[] CanonicalOrder =
        ["aggregation", "timeBucket", "period", "denominatorSource", "filters", "rounding", "numericType", "nullHandling", "zeroDenominator", "currency", "overflow"];

    public static CalculationComparison Compare(CalculationSide left, CalculationSide right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var la = left.Raw.GetProperty("attributes");
        var ra = right.Raw.GetProperty("attributes");
        var names = CanonicalOrder
            .Concat(la.EnumerateObject().Select(p => p.Name))
            .Concat(ra.EnumerateObject().Select(p => p.Name))
            .Distinct(StringComparer.Ordinal)
            .Where(n => la.TryGetProperty(n, out _) || ra.TryGetProperty(n, out _))
            .ToList();

        var differences = new List<Difference>();
        var agreements = new List<string>();
        var unknownOnBoth = new List<string>();
        foreach (var name in names)
        {
            var l = Read(la, name);
            var r = Read(ra, name);
            var lKnown = l is { Status: not "unknown" };
            var rKnown = r is { Status: not "unknown" };
            if (!lKnown && !rKnown)
            {
                unknownOnBoth.Add(name);
            }
            else if (lKnown != rKnown)
            {
                differences.Add(new($"d{differences.Count + 1}", name, DifferenceKind.KnownOnOneSide, l, r));
            }
            else if (l!.Value != r!.Value)
            {
                differences.Add(new($"d{differences.Count + 1}", name, DifferenceKind.ValueDiffers, l, r));
            }
            else if (l.Detail?.GetRawText() != r.Detail?.GetRawText() && l.Detail is not null && r.Detail is not null && !SameDetail(l.Detail.Value, r.Detail.Value))
            {
                differences.Add(new($"d{differences.Count + 1}", name, DifferenceKind.DetailDiffers, l, r));
            }
            else
            {
                agreements.Add(name);
            }
        }

        static string? Hash(JsonElement raw) => raw.TryGetProperty("irHash", out var h) ? h.GetString() : null;
        static string OutputType(JsonElement raw) => raw.GetProperty("output").GetProperty("type").GetString() ?? "unknown";
        return new CalculationComparison(
            left, right,
            Hash(left.Raw) is { } lh && lh == Hash(right.Raw),
            OutputType(left.Raw), OutputType(right.Raw),
            differences, agreements, unknownOnBoth);
    }

    private static AttributeValue? Read(JsonElement attributes, string name)
    {
        if (!attributes.TryGetProperty(name, out var a))
        {
            return null;
        }

        var value = a.TryGetProperty("value", out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText() : null;
        return new AttributeValue(a.GetProperty("status").GetString()!, value, a.TryGetProperty("detail", out var d) ? d.Clone() : null);
    }

    /// <summary>
    /// Details mention places and helper names that naturally differ between implementations; only the
    /// numeric parameters (digits, alpha, window) are compared, because those change behaviour.
    /// </summary>
    private static bool SameDetail(JsonElement l, JsonElement r)
    {
        static Dictionary<string, string> Numbers(JsonElement e) => e.ValueKind != JsonValueKind.Object
            ? []
            : e.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Number).ToDictionary(p => p.Name, p => p.Value.GetRawText(), StringComparer.Ordinal);
        var ln = Numbers(l);
        var rn = Numbers(r);
        return ln.Count == rn.Count && ln.All(kv => rn.TryGetValue(kv.Key, out var x) && x == kv.Value);
    }
}
