using CapabilityExplorer.Interpretation;

namespace CapabilityExplorer.Explorer;

/// <summary>The configured intelligence layer; <see cref="Explanations"/> is null when no model is configured.</summary>
public sealed record InterpretationLayer(InterpretationOptions Options, ExplanationService? Explanations)
{
    public bool Enabled => Explanations is not null;
}

public sealed record CalculationRef(string Key, string Name, string RepositoryId, string CommitSha, string Language, string OutputType);

public sealed record ComparisonDto(
    CalculationRef Left,
    CalculationRef Right,
    bool SameExpression,
    IReadOnlyList<Difference> Differences,
    IReadOnlyList<string> Agreements,
    IReadOnlyList<string> UnknownOnBoth);

public sealed record ExplainRequest(string A, string B, string? View);

public static class Comparisons
{
    public static CalculationComparison? Compare(ViewIndex view, string a, string b)
    {
        CalculationSide? Side(string key)
        {
            if (!view.Calculations.TryGetValue(key, out var c))
            {
                return null;
            }

            var name = view.Node(key)?.Record.Name ?? Map.DisplayName(key);
            return new CalculationSide(key, name, c.Artifact.RepositoryId, c.Artifact.CommitSha, c.Artifact.Artifact.Header.Language, c.Record.Raw);
        }

        return Side(a) is { } left && Side(b) is { } right ? CalculationComparer.Compare(left, right) : null;
    }

    public static ComparisonDto ToDto(CalculationComparison c)
    {
        static CalculationRef Ref(CalculationSide s, string type) => new(s.Key, s.Name, s.RepositoryId, s.CommitSha, s.Language, type);
        return new ComparisonDto(Ref(c.Left, c.LeftOutputType), Ref(c.Right, c.RightOutputType), c.SameExpression, c.Differences, c.Agreements, c.UnknownOnBoth);
    }
}
