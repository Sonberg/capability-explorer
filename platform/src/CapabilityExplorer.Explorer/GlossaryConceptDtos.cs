namespace CapabilityExplorer.Explorer;

/// <summary>One feature of the score. A feature with zero points is still reported: it is a fact, not a verdict.</summary>
public sealed record GlossaryReason(string Feature, int Points, string? Detail);

/// <summary>A declaration folded into a concept: itself, a representation of it, or a variant of an interface.</summary>
public sealed record GlossaryMember(
    string Id, string Key, string Name, string Kind, string Fold, string RepositoryId, string CommitSha,
    string Unit, string UnitPath, IReadOnlyList<EvidenceDto> Evidence);

/// <summary>A property, field or column nested under the concept that declares it.</summary>
public sealed record GlossaryField(
    string Label, string Name, bool Generic, int Declarations, IReadOnlyList<EvidenceDto> Evidence);

/// <summary>
/// Declarations with an equal stem, ranked by an explainable score. Navigation aid only: membership never means
/// the same value (MADR-0005). Tier is derived from Score and the checked-in thresholds.
/// </summary>
public sealed record GlossaryConcept(
    string Key, string Term, string Tier, int Score, IReadOnlyList<GlossaryReason> Why,
    IReadOnlyList<GlossaryMember> Members, IReadOnlyList<GlossaryMember> Variants,
    IReadOnlyList<GlossaryField> Attributes, IReadOnlyList<string> Repositories, IReadOnlyList<string> Capabilities);

/// <summary>Projection plus what it could not see. Limits are disclosed so a partial view is not read as complete.</summary>
public sealed record GlossaryConceptView(
    int WeightsVersion, IReadOnlyList<GlossaryConcept> Concepts, IReadOnlyList<GlossaryCandidate> Metrics,
    int ExcludedTestDeclarations, IReadOnlyList<string> PartialUnits, IReadOnlyList<string> Limits);
