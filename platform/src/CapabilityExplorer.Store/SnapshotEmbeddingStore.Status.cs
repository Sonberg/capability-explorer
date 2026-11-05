using Npgsql;

namespace CapabilityExplorer.Store;

public sealed record EmbeddingCounts(long Total, long Ready, long Pending, long Failed);
public sealed record SnapshotEmbeddingCoverage(string State, SnapshotEmbeddingModel? Model, long SnapshotArtifactCount,
    long SelectedArtifactCount, long CoveredArtifactCount, string? SnapshotArtifactsSha256, bool CountsExact,
    EmbeddingCounts? Counts, DateTime? LastSuccessfulCompletion, string? ReasonCode);

public sealed partial class SnapshotEmbeddingStore
{
    // Coverage touches only selected artifact metadata; historical lookup is indexed and LIMIT 1.
    internal const string StatusSql = """
        with selected as materialized (
          select sa.artifact_id from analysis_snapshot_artifact sa
          join unnest($2::text[],$3::text[]) selection(repo,sha)
            on selection.repo=sa.repository_id and selection.sha=sa.commit_sha::text
          where sa.snapshot_id=$1
        ), current_projection as (
          select * from snapshot_embedding_projection where snapshot_id=$1 and model_key=$4::char(64)
        ), historical as (
          select p.* from snapshot_embedding_projection p where p.snapshot_id=$1 and $4<>''
            and p.enumeration_complete and p.last_success is not null
            and not exists(select 1 from current_projection)
          order by p.last_success desc,p.model_key limit 1
        ), projection as (
          select * from current_projection union all select * from historical
        )
        select (select count(*) from analysis_snapshot_artifact where snapshot_id=$1),
          (select count(*) from selected),
          p.model_id,p.model_key,p.recipe_version,p.artifacts_sha256,p.enumeration_complete,p.last_success,
          count(a.artifact_id) filter(where a.sealed),
          count(a.artifact_id) filter(where a.sealed and a.pending_count=0 and a.failed_count=0),
          coalesce(sum(a.total_count),0)::bigint,coalesce(sum(a.ready_count),0)::bigint,
          coalesce(sum(a.pending_count),0)::bigint,coalesce(sum(a.failed_count),0)::bigint,
          max(a.last_success)
        from analysis_snapshot anchor left join projection p on true
        left join selected s on true
        left join lateral (
          select metadata.* from snapshot_embedding_artifact metadata
          where metadata.snapshot_id=$1 and metadata.model_key=p.model_key and metadata.artifact_id=s.artifact_id
          limit 1
        ) a on true
        where anchor.id=$1 and anchor.status='published'
        group by p.model_id,p.model_key,p.recipe_version,p.artifacts_sha256,p.enumeration_complete,p.last_success
        """;

    public async Task<SnapshotEmbeddingCoverage> StatusAsync(Guid snapshot, IReadOnlyDictionary<string, string> selection,
        SnapshotEmbeddingModel? configured, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        await using var command = dataSource.CreateCommand(StatusSql);
        command.CommandTimeout = 5;
        var sorted = selection.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
        command.Parameters.AddWithValue(snapshot);
        command.Parameters.AddWithValue(sorted.Select(p => p.Key).ToArray());
        command.Parameters.AddWithValue(sorted.Select(p => p.Value).ToArray());
        command.Parameters.AddWithValue(configured?.Fingerprint ?? "");
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new SnapshotConflictException("Snapshot is unavailable.");
        var snapshotCount = Safe(reader.GetInt64(0));
        var selectedCount = Safe(reader.GetInt64(1));
        if (configured is null)
            return new("not-configured", null, snapshotCount, selectedCount, 0, null, false, null, null, "model-not-configured");
        if (reader.IsDBNull(2))
            return new("pending", null, snapshotCount, selectedCount, 0, null, false, null, null, "projection-not-started");
        var model = new SnapshotEmbeddingModel(reader.GetString(2), reader.GetString(3), reader.GetString(4));
        var completed = reader.GetBoolean(6) && !reader.IsDBNull(7);
        var exact = Safe(reader.GetInt64(8)) == selectedCount && (selectedCount > 0 || completed);
        var covered = Safe(reader.GetInt64(9));
        var counts = new EmbeddingCounts(Safe(reader.GetInt64(10)), Safe(reader.GetInt64(11)), Safe(reader.GetInt64(12)), Safe(reader.GetInt64(13)));
        var ready = exact && counts.Pending == 0 && counts.Failed == 0;
        var historical = model.Fingerprint != configured.Fingerprint;
        var state = historical && ready ? "stale" : ready ? "ready" : counts.Failed > 0 ? "partial-failure" : "pending";
        DateTime? success = ready ? selectedCount == 0 ? reader.GetDateTime(7)
            : reader.IsDBNull(14) ? null : reader.GetDateTime(14) : null;
        return new(state, model, snapshotCount, selectedCount, covered, reader.GetString(5), exact, exact ? counts : null, success,
            state switch { "ready" => null, "stale" => "model-changed", "partial-failure" => "embedding-items-failed", _ => "projection-incomplete" });
    }

    private static long Safe(long count) => count is >= 0 and <= 9007199254740991
        ? count : throw new InvalidOperationException("Embedding metadata exceeds the safe numeric budget.");
}
