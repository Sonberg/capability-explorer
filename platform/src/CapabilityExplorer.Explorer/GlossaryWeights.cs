using System.Reflection;
using System.Text.Json;

namespace CapabilityExplorer.Explorer;

/// <summary>Integer points for one feature. Basis says whether it was fitted from labels or is a stated prior.</summary>
public sealed record GlossaryWeight(int Points, string Basis);

/// <summary>
/// Checked-in, versioned scoring parameters (MADR-0005). Scores are integer sums, so tiers never depend on
/// floating-point behavior. Weights are produced offline from labelled keep/hide decisions; nothing is learned at runtime.
/// </summary>
public sealed record GlossaryWeights(
    int Version, int Bias, int SupportingAt, int CoreAt, double GenericLabelShare,
    IReadOnlyDictionary<string, GlossaryWeight> Weights)
{
    private static readonly Lazy<GlossaryWeights> DefaultWeights = new(() =>
    {
        using var stream = typeof(GlossaryWeights).Assembly.GetManifestResourceStream("glossary-weights.json")
            ?? throw new InvalidOperationException("glossary-weights.json is not embedded");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });

    public static GlossaryWeights Default => DefaultWeights.Value;

    public static GlossaryWeights Parse(string json) =>
        JsonSerializer.Deserialize<GlossaryWeights>(json, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("glossary weights are empty");

    public int Points(string feature) => Weights.TryGetValue(feature, out var weight) ? weight.Points : 0;

    public string TierOf(int score) => score >= CoreAt ? "core" : score >= SupportingAt ? "supporting" : "plumbing";
}
