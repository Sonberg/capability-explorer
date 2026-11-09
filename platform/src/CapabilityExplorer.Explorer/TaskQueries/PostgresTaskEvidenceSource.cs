using CapabilityExplorer.Contracts.Collection;
using CapabilityExplorer.Store;
using Npgsql;

namespace CapabilityExplorer.Explorer;

/// <summary>Reads the Postgres-projected analysis (<see cref="IExplorerQueries"/>, <see cref="ModelProductStore"/>):
/// the same snapshots the database-mode Explorer UI reads, scoped to exactly the caller's selected revisions.
/// A pinned, older-but-still-published snapshot is a valid read here (freshness "superseded"), unlike files mode,
/// which keeps no history.</summary>
public sealed class PostgresTaskEvidenceSource(IExplorerQueries queries, NpgsqlDataSource dataSource, DatabaseExplorerConfiguration configuration) : ITaskEvidenceSource
{
    public string Mode => "database";

    public async Task<TaskSnapshotScope?> ResolveAsync(string cap, IReadOnlyDictionary<string, string> specifiedView, string? snapshotId, long? generation, CancellationToken ct)
    {
        Guid? pinned = null;
        if (snapshotId is not null)
        {
            if (!Guid.TryParse(snapshotId, out var parsed)) throw new TaskQueryInvalidRequestException("invalid-snapshot-id");
            pinned = parsed;
        }

        if (cap != "*")
        {
            using var definitions = configuration.OpenDefinitions();
            if (definitions.Find(cap) is null) throw new TaskQueryInvalidRequestException("unknown-capability");
        }

        // Ops 2-6 never bind a `cap` query/tool argument (spec: snapshot+generation only, "ModelApi pattern"),
        // so the caller-supplied `cap` is always the "*" sentinel here when a snapshot is pinned. "*" is never a
        // real capability_id, so the published-by-capability lookup below could never match a row. Discover the
        // pinned snapshot's own capability first (no interface/schema changes - a direct lookup over the already
        // injected data source) and resolve both "current" and the pinned context against that real capability.
        var effectiveCap = cap;
        if (pinned is not null && cap == "*")
        {
            var discovered = await DiscoverCapabilityAsync(pinned.Value, ct);
            if (discovered is null) return null;
            effectiveCap = discovered;
        }

        var current = await queries.ResolveAsync(effectiveCap, specifiedView, null, ct);
        var context = pinned is null ? current : await queries.ResolveAsync(effectiveCap, specifiedView, pinned, ct);
        if (context is null) return null;

        if (cap != "*")
        {
            using var definitions = configuration.OpenDefinitions();
            if (definitions.Find(cap) is { } definition &&
                context.Selection.Keys.Any(repo => !definition.Repositories.Any(r => r.Id == repo)))
                throw new TaskQueryScopeDeniedException("scope-denied");
        }

        var isCurrent = current is not null && context.SnapshotId == current.SnapshotId;
        return new TaskSnapshotScope(context.SnapshotId.ToString(), context.Generation, context.Selection, isCurrent,
            current?.SnapshotId.ToString(), context);
    }

    public TaskSnapshotScope Narrow(TaskSnapshotScope scope, IReadOnlyDictionary<string, string> allowed) =>
        scope with { Selection = allowed, Handle = ((ExplorerQueryContext)scope.Handle) with { Selection = allowed } };

    /// <summary>Looks up the real <c>capability_id</c> of a published, pinned snapshot directly, bypassing the
    /// capability-scoped <see cref="IExplorerQueries.ResolveAsync"/> filter that a "*" sentinel can never match.</summary>
    private async Task<string?> DiscoverCapabilityAsync(Guid snapshotId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            "select capability_id from analysis_snapshot where id=@id and status='published'");
        command.Parameters.AddWithValue("id", snapshotId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? reader.GetString(0) : null;
    }

    /// <summary>Metadata-only (<see cref="ModelProductHead"/>) listing: never reads full product content
    /// (evidence/source bytes) for every head before pagination, unlike a naive "read everything, then page".
    /// Heads are capped at 4096 (same bound as before); when the cap is actually hit, <see cref="TaskModelPage.Truncated"/>
    /// is true so the caller reports an honest "unknown" total rather than a fabricated one.</summary>
    public async Task<TaskModelPage> ModelsAsync(TaskSnapshotScope scope, string? kind, CancellationToken ct)
    {
        var context = (ExplorerQueryContext)scope.Handle;
        var store = new ModelProductStore(dataSource);
        var heads = new List<ModelProductHead>();
        string after = "";
        var truncated = false;
        while (true)
        {
            var batch = await store.HeadsAsync(context.SnapshotId, scope.Selection, after, 128, ct);
            if (batch.Count == 0) break;
            heads.AddRange(batch);
            after = batch[^1].Id;
            if (batch.Count < 128) break;
            if (heads.Count >= 4096) { truncated = true; break; }
        }

        var filtered = (kind is null ? heads : heads.Where(h => h.ModelKind == kind)).ToList();
        return new TaskModelPage(filtered, truncated);
    }

    public async Task<ModelProduct?> ModelAsync(TaskSnapshotScope scope, string modelId, CancellationToken ct) =>
        await new ModelProductStore(dataSource).ReadAsync(((ExplorerQueryContext)scope.Handle).SnapshotId, scope.Selection, modelId, ct);

    public async Task<ModelProductHead?> ModelHeadAsync(TaskSnapshotScope scope, string modelId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            select m.id,m.profile,m.repository_id,m.commit_sha,m.unit_path,m.model_kind,m.coverage,
              m.diagnostic_count,sm.source_status,m.source_sha256
            from analysis_snapshot_model sm join model_product m on m.id=sm.model_id
            join unnest($2::text[],$3::text[]) s(repo,sha) on s.repo=m.repository_id and s.sha=m.commit_sha::text
            where sm.snapshot_id=$1 and m.id=$4
            """);
        command.Parameters.AddWithValue(((ExplorerQueryContext)scope.Handle).SnapshotId);
        command.Parameters.AddWithValue(scope.Selection.Keys.ToArray());
        command.Parameters.AddWithValue(scope.Selection.Values.ToArray());
        command.Parameters.AddWithValue(modelId);
        command.CommandTimeout = 5;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        if (reader.GetString(4).Length > 1024) throw new FormatException("stored model path exceeds limit");
        return new ModelProductHead(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetInt32(7), reader.GetString(8), reader.GetString(9));
    }

    public Task<Integrations> IntegrationsAsync(TaskSnapshotScope scope, CancellationToken ct) =>
        queries.IntegrationsAsync((ExplorerQueryContext)scope.Handle, [], ct);

    public async Task<TaskEvidencePage> EvidenceAsync(TaskSnapshotScope scope, string key, CancellationToken ct)
    {
        var context = (ExplorerQueryContext)scope.Handle;
        var result = new List<ExplorerEvidenceRow>();
        string? cursor = null;
        var truncated = false;
        while (true)
        {
            var page = await queries.EvidenceAsync(context, key, new ExplorerPageRequest(100, cursor), ct);
            var remaining = 4096 - result.Count;
            result.AddRange(page.Items.Take(remaining));
            if (result.Count >= 4096)
            {
                truncated = page.NextCursor is not null || page.Items.Count > remaining;
                break;
            }
            if (page.NextCursor is null) break;
            cursor = page.NextCursor;
        }

        return new TaskEvidencePage(result.Select(r => new EvidenceDto(r.Id, r.RepositoryId, r.CommitSha, r.Path, r.StartLine, r.StartColumn, r.EndLine, r.EndColumn)).ToList(), truncated);
    }


    public Task<bool> IsCitedAsync(TaskSnapshotScope scope, string repositoryId, string commitSha, string path, CancellationToken ct) =>
        queries.IsCitedAsync((ExplorerQueryContext)scope.Handle, repositoryId, commitSha, path, ct);

    public Task<string?> ReadCitedFileAsync(TaskSnapshotScope scope, string repositoryId, string commitSha, string path, CancellationToken ct)
    {
        return _git is { } git ? git.ReadTaskQueryFileAsync(repositoryId, commitSha, path, ct) : Task.FromResult<string?>(null);
    }

    private GitSource? _git;

    /// <summary>Set once by DI wiring (<see cref="TaskQueryApi"/>), after construction: <see cref="DatabaseExplorerConfiguration"/>
    /// opens capability configuration eagerly, which this source must not do until a call actually needs source text.</summary>
    public PostgresTaskEvidenceSource WithGit(GitSource? git)
    {
        _git = git;
        return this;
    }
}
