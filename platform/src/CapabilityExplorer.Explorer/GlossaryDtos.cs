using CapabilityExplorer.Interpretation;

namespace CapabilityExplorer.Explorer;

/// <summary>Source occurrence, never a reviewed assertion of business equivalence.</summary>
public sealed record GlossaryOccurrence(
    string Id, string Key, string Name, string Kind, string Role,
    string RepositoryId, string CommitSha, string Unit, string UnitPath,
    string? Type, IReadOnlyList<string> Capabilities, IReadOnlyList<EvidenceDto> Evidence);

/// <summary>Null SameExpression means an IR hash is unavailable, not a difference.</summary>
public sealed record GlossaryComparison(
    string LeftId, string RightId, bool? SameExpression, IReadOnlyList<Difference> Differences);

/// <summary>Exact normalized-name group; bounded comparisons disclose incomplete coverage.</summary>
public sealed record GlossaryCandidate(
    string Term, IReadOnlyList<GlossaryOccurrence> Occurrences,
    IReadOnlyList<string> Repositories, IReadOnlyList<string> Capabilities,
    bool TypeDiffers, bool DerivationDiffers, bool ExpressionDiffers,
    bool ComparisonsComplete, long PossibleComparisons,
    IReadOnlyList<GlossaryComparison> Comparisons);
