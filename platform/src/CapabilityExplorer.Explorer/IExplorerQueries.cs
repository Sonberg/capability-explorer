using System.Text.Json;

namespace CapabilityExplorer.Explorer;

/// <summary>Immutable read boundary. Scope is re-authorized before each request, including cursor requests.</summary>
public sealed record ExplorerQueryContext(Guid SnapshotId, string Capability, long Generation,
    IReadOnlyDictionary<string, string> Selection);
public sealed record ExplorerPageRequest(int Limit = 50, string? Cursor = null)
{
    public int BoundedLimit => Math.Clamp(Limit, 1, 100);
}
public sealed record ExplorerPage<T>(IReadOnlyList<T> Items, string? NextCursor, Guid SnapshotId);
public sealed record ExplorerDatabaseMetadata(Guid SnapshotId, long Generation, string Capability, string Status);
public sealed record ExplorerRevisionRow(string RepositoryId, string CommitSha);
public sealed record ExplorerArtifactRow(long Id, string RepositoryId, string CommitSha, string Unit, string UnitPath,
    string Language, string Status);
public sealed record ExplorerNodeRow(string Key, string Kind, string Name, string? Signature, bool Generated,
    string RepositoryId, string CommitSha, string Unit, long ArtifactId);
public sealed record ExplorerEdgeRow(long ArtifactId, string LocalId, string From, string Relation, string? To,
    string Derivation, string Resolution, string? UnresolvedReason, string RepositoryId, string CommitSha);
public sealed record ExplorerCalculationRow(long ArtifactId, string LocalId, string Key, string Name,
    string RepositoryId, string CommitSha, string Unit, string Language, string? IrHash);
public sealed record ExplorerCalculationDetail(ExplorerCalculationRow Metadata, JsonElement Body);
public sealed record ExplorerEvidenceRow(string Id, string RepositoryId, string CommitSha, string Path,
    int StartLine, int StartColumn, int EndLine, int EndColumn);

/// <summary>Database-native projections: no Artifact, ExplorerIndex or ViewIndex materialization.</summary>
public interface IExplorerQueries
{
    Task<ExplorerQueryContext?> ResolveAsync(string capability, IReadOnlyDictionary<string, string>? selection = null,
        Guid? snapshotId = null, CancellationToken cancellationToken = default);
    Task<ExplorerDatabaseMetadata> MetadataAsync(ExplorerQueryContext context, CancellationToken cancellationToken = default);
    Task<Integrations> IntegrationsAsync(ExplorerQueryContext context, IReadOnlyList<MarkDto> marks,
        CancellationToken cancellationToken = default);
    Task<ExplorerPage<ExplorerRevisionRow>> RevisionsAsync(ExplorerQueryContext context, ExplorerPageRequest page,
        CancellationToken cancellationToken = default);
    Task<ExplorerPage<ExplorerArtifactRow>> ArtifactsAsync(ExplorerQueryContext context, ExplorerPageRequest page,
        CancellationToken cancellationToken = default);
    Task<ExplorerPage<SearchHit>> SearchAsync(ExplorerQueryContext context, string? query, string? kind, string? repository,
        ExplorerPageRequest page, CancellationToken cancellationToken = default);
    Task<ExplorerNodeRow?> SymbolAsync(ExplorerQueryContext context, string key, CancellationToken cancellationToken = default);
    Task<ExplorerPage<ExplorerEdgeRow>> EdgesAsync(ExplorerQueryContext context, string key, string direction,
        ExplorerPageRequest page, CancellationToken cancellationToken = default);
    Task<ExplorerPage<ExplorerCalculationRow>> CalculationsAsync(ExplorerQueryContext context, ExplorerPageRequest page,
        CancellationToken cancellationToken = default);
    Task<ExplorerCalculationDetail?> CalculationAsync(ExplorerQueryContext context, string key, CancellationToken cancellationToken = default);
    Task<ExplorerPage<ExplorerEvidenceRow>> EvidenceAsync(ExplorerQueryContext context, string key, ExplorerPageRequest page,
        CancellationToken cancellationToken = default);
    Task<bool> IsCitedAsync(ExplorerQueryContext context, string repository, string commit, string path,
        CancellationToken cancellationToken = default);
}

/// <summary>Derived read projection is not available; never fall back to a different search contract.</summary>
public sealed class ExplorerProjectionUnavailableException(string message) : Exception(message);
