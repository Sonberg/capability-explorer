using CapabilityExplorer.Contracts.Collection;
using CapabilityExplorer.Store;

namespace CapabilityExplorer.Explorer;

/// <summary>Metadata-only model listing: identity, repository/commit, model kind/coverage and source status, all
/// cheap to produce (no product content, no evidence bytes, no source envelope). Used both for the paged
/// <c>/models</c> summary and for decision-reference candidate matching, so neither path ever reads full product
/// content before it is actually needed. <paramref name="Truncated"/> is true only when the source capped its own
/// internal scan (so the true total is honestly "unknown", never a fabricated count).</summary>
public sealed record TaskModelPage(IReadOnlyList<ModelProductHead> Heads, bool Truncated);
public sealed record TaskEvidencePage(IReadOnlyList<EvidenceDto> Items, bool Truncated);

/// <summary>Resolved, immutable scope for one task-query request: a snapshot id, its generation, the repository
/// selection that was actually used, and the freshness relationship to the current publication. <see cref="Handle"/>
/// is an opaque, source-owned cache (a built <c>ViewIndex</c> for files, an <c>ExplorerQueryContext</c> for
/// Postgres) so a source need not re-resolve its own view for every operation on the same scope.</summary>
public sealed record TaskSnapshotScope(
    string SnapshotId,
    long Generation,
    IReadOnlyDictionary<string, string> Selection,
    bool IsCurrent,
    string? CurrentSnapshotId,
    object Handle);

/// <summary>One typed service over both evidence sources (files, Postgres); no silent fallback between them.
/// Every member is pre-authorized by <see cref="TaskQueryAccessPolicy"/> before it is called; this interface
/// never re-implements access control, only retrieval.</summary>
public interface ITaskEvidenceSource
{
    string Mode { get; }

    Task<TaskSnapshotScope?> ResolveAsync(string cap, IReadOnlyDictionary<string, string> specifiedView, string? snapshotId, long? generation, CancellationToken ct);

    /// <summary>Rebuilds <see cref="TaskSnapshotScope.Handle"/> for a repository selection narrowed by access
    /// policy (never widened): every subsequent read on the narrowed scope sees only granted repositories, even
    /// though the original resolve saw the full snapshot selection.</summary>
    TaskSnapshotScope Narrow(TaskSnapshotScope scope, IReadOnlyDictionary<string, string> allowed);

    /// <summary>All model heads (metadata only, no content) in the selection; callers page and sort the result.</summary>
    Task<TaskModelPage> ModelsAsync(TaskSnapshotScope scope, string? kind, CancellationToken ct);

    async Task<ModelProductHead?> ModelHeadAsync(TaskSnapshotScope scope, string modelId, CancellationToken ct) =>
        (await ModelsAsync(scope, null, ct)).Heads.FirstOrDefault(head => head.Id == modelId);

    Task<ModelProduct?> ModelAsync(TaskSnapshotScope scope, string modelId, CancellationToken ct);

    Task<Integrations> IntegrationsAsync(TaskSnapshotScope scope, CancellationToken ct);

    /// <summary>Every evidence record attached to the symbol key (its own declaration and any edge touching it).</summary>
    Task<TaskEvidencePage> EvidenceAsync(TaskSnapshotScope scope, string key, CancellationToken ct);

    Task<bool> IsCitedAsync(TaskSnapshotScope scope, string repositoryId, string commitSha, string path, CancellationToken ct);

    /// <summary>Reads a cited source file at the exact revision; never dereferences a URI.</summary>
    Task<string?> ReadCitedFileAsync(TaskSnapshotScope scope, string repositoryId, string commitSha, string path, CancellationToken ct);
}

/// <summary>Thrown by <see cref="ITaskEvidenceSource.ResolveAsync"/> for a malformed request (unknown capability,
/// a <c>view</c> override outside the capability's scope, or an unparsable snapshot id): always wire status
/// <c>invalid</c>, never a guess at <c>denied</c>/<c>missing</c>.</summary>
public sealed class TaskQueryInvalidRequestException(string reason) : ArgumentException(reason)
{
    public string Reason { get; } = reason;
}

/// <summary>Thrown by <see cref="ITaskEvidenceSource.ResolveAsync"/> when a resolved (e.g. pinned) snapshot's
/// selection now reaches outside the capability's current repository scope: wire status <c>denied</c>, distinct
/// from an unknown capability (<see cref="TaskQueryInvalidRequestException"/>, status <c>invalid</c>).</summary>
public sealed class TaskQueryScopeDeniedException(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}
