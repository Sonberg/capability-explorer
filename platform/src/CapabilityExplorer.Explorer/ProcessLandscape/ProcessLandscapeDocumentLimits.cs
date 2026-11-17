namespace CapabilityExplorer.Explorer;

/// <summary>Opt-in graph ceilings mirror the producer's manifest/XML limits and add no headroom to the existing
/// snapshot/response byte caps. Counts apply to unique same-file nodes, not repeated DI/DC projections.</summary>
public static class ProcessLandscapeDocumentLimits
{
    public const int MaxDocuments = 512;
    public const int MaxElements = 25_000;
    public const int MaxRelationships = 25_000;
    public const int MaxParentDepth = 128;
    public const int MaxAttributes = 128;
    public const int MaxAttributeBytes = 2 * 1024 * 1024;
    public const int MaxTextBytes = 2 * 1024 * 1024;
    public const int MaxJsonBytes = 96 * 1024 * 1024;
}
