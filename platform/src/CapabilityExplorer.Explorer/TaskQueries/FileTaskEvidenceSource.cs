using CapabilityExplorer.Contracts.Collection;

namespace CapabilityExplorer.Explorer;

/// <summary>Reads the files-mode load (<see cref="ExplorerState"/>): the same artifacts, models and git access the
/// rest of the Explorer UI uses, scoped to exactly the repository/commit pairs the caller selected.</summary>
public sealed class FileTaskEvidenceSource(ExplorerState state) : ITaskEvidenceSource
{
    public string Mode => "files";
    private sealed record FileHandle(ExplorerSnapshot Snapshot, ViewIndex View);

    public Task<TaskSnapshotScope?> ResolveAsync(string cap, IReadOnlyDictionary<string, string> specifiedView, string? snapshotId, long? generation, CancellationToken ct)
    {
        var current = state.Current;
        var currentId = current.ModelSnapshotId.ToString();
        if (snapshotId is not null && (snapshotId != currentId || generation is not null && generation != current.Generation))
        {
            // Files mode keeps no history: any snapshot id except the live one is, by definition, stale.
            return Task.FromResult<TaskSnapshotScope?>(new TaskSnapshotScope(snapshotId, generation ?? current.Generation,
                new Dictionary<string, string>(StringComparer.Ordinal), false, currentId, current));
        }

        if (cap != "*" && current.Capabilities.Get(cap) is null) throw new TaskQueryInvalidRequestException("unknown-capability");
        var allowedRepos = current.Capabilities.Scope(cap)?.Repositories;
        var choices = current.Index.Revisions.Select(r => (r.RepositoryId, r.CommitSha))
            .Concat(current.Models.Products.Select(p => (p.RepositoryId, p.CommitSha)))
            .Where(r => allowedRepos is null || allowedRepos.Contains(r.RepositoryId)).ToArray();
        var preferred = current.Capabilities.Scope(cap)?.Preferred;
        var selection = choices.GroupBy(r => r.RepositoryId).ToDictionary(g => g.Key,
            g => preferred?.GetValueOrDefault(g.Key) ?? current.Index.DefaultSelection.GetValueOrDefault(g.Key) ??
                 g.Select(r => r.CommitSha).Order(StringComparer.Ordinal).Last(),
            StringComparer.Ordinal);
        foreach (var pair in specifiedView)
        {
            if (!choices.Contains((pair.Key, pair.Value))) throw new TaskQueryInvalidRequestException("revision-outside-scope");
            selection[pair.Key] = pair.Value;
        }

        var view = new ViewIndex(current.Index, selection);
        return Task.FromResult<TaskSnapshotScope?>(new TaskSnapshotScope(currentId, current.Generation, selection, true, currentId, new FileHandle(current, view)));
    }

    public TaskSnapshotScope Narrow(TaskSnapshotScope scope, IReadOnlyDictionary<string, string> allowed)
    {
        var handle = (FileHandle)scope.Handle;
        return scope with { Selection = allowed, Handle = handle with { View = new ViewIndex(handle.View.Index, allowed) } };
    }

    public Task<TaskModelPage> ModelsAsync(TaskSnapshotScope scope, string? kind, CancellationToken ct)
    {
        var heads = Models(scope).Select(ToHead).Where(h => kind is null || h.ModelKind == kind).ToList();
        return Task.FromResult(new TaskModelPage(heads, false));
    }

    private static CapabilityExplorer.Store.ModelProductHead ToHead(ModelProduct p)
    {
        var summary = ModelSummary.From(p);
        return new CapabilityExplorer.Store.ModelProductHead(p.Id, p.Profile, p.RepositoryId, p.CommitSha, p.UnitPath,
            summary.ModelKind, summary.Coverage, summary.DiagnosticCount, p.SourceStatus, summary.Source.Sha256 ?? "");
    }

    public Task<ModelProduct?> ModelAsync(TaskSnapshotScope scope, string modelId, CancellationToken ct) =>
        Task.FromResult(Models(scope).FirstOrDefault(p => p.Id == modelId));

    public Task<Integrations> IntegrationsAsync(TaskSnapshotScope scope, CancellationToken ct) =>
        Task.FromResult(Queries.Integrations(((FileHandle)scope.Handle).View));

    public Task<TaskEvidencePage> EvidenceAsync(TaskSnapshotScope scope, string key, CancellationToken ct)
    {
        var view = ((FileHandle)scope.Handle).View;
        var evidence = new List<EvidenceDto>();
        if (view.Node(key) is { } node) evidence.AddRange(Map.Node(node).Evidence);
        evidence.AddRange(view.Outgoing[key].SelectMany(e => Map.Edge(view, e).Evidence));
        evidence.AddRange(view.Incoming[key].SelectMany(e => Map.Edge(view, e).Evidence));
        return Task.FromResult(new TaskEvidencePage(evidence
            .GroupBy(e => e.Id, StringComparer.Ordinal).Select(g => g.First())
            .OrderBy(e => e.Path, StringComparer.Ordinal).ThenBy(e => e.StartLine).ThenBy(e => e.Id, StringComparer.Ordinal).ToList(), false));
    }

    public Task<bool> IsCitedAsync(TaskSnapshotScope scope, string repositoryId, string commitSha, string path, CancellationToken ct) =>
        Task.FromResult(((FileHandle)scope.Handle).View.Index.IsCitedFile(repositoryId, commitSha, path));

    public Task<string?> ReadCitedFileAsync(TaskSnapshotScope scope, string repositoryId, string commitSha, string path, CancellationToken ct) =>
        ((FileHandle)scope.Handle).View.Index.Git is { } git ? git.ReadFileAsync(repositoryId, commitSha, path, ct) : Task.FromResult<string?>(null);

    private static List<ModelProduct> Models(TaskSnapshotScope scope) =>
        ((FileHandle)scope.Handle).Snapshot.Models.Products.Where(p => scope.Selection.TryGetValue(p.RepositoryId, out var sha) && sha == p.CommitSha).ToList();
}
