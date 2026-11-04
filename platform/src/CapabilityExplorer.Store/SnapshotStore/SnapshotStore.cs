using Npgsql;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Contracts.Collection;

namespace CapabilityExplorer.Store;

public sealed record AnalysisSnapshot(Guid Id, string CapabilityId, string InputIdentity, string Status,
    long? Generation, DateTime CreatedAt, DateTime? PublishedAt, long ArtifactCount);

public sealed class SnapshotConflictException(string message) : Exception(message);

/// <summary>Immutable exact artifact selections, with one atomically published generation per scope.</summary>
public sealed partial class SnapshotStore(NpgsqlDataSource dataSource)
{
    public const int MaximumArtifacts = 100_000;

    public Task<AnalysisSnapshot> CreateWithModelsAsync(string capabilityId, string inputIdentity,
        IReadOnlyList<long> artifactIds, IReadOnlyDictionary<string,string> defaults,
        IReadOnlyList<ModelProduct> models, CancellationToken ct = default) =>
        CreateCoreAsync(capabilityId, inputIdentity, artifactIds, defaults, ct, models);

    public Task<AnalysisSnapshot> CreateAsync(string capabilityId, string inputIdentity,
        IReadOnlyList<long> artifactIds, CancellationToken cancellationToken = default) =>
        CreateCoreAsync(capabilityId,inputIdentity,artifactIds,null,cancellationToken);

    /// <summary>Explicit defaults may override single-revision repositories and must choose one member revision
    /// for every repository having multiple revisions. Defaults become immutable snapshot metadata.</summary>
    public Task<AnalysisSnapshot> CreateAsync(string capabilityId, string inputIdentity,
        IReadOnlyList<long> artifactIds, IReadOnlyDictionary<string,string> defaultRevisions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(defaultRevisions);
        return CreateCoreAsync(capabilityId,inputIdentity,artifactIds,defaultRevisions,cancellationToken);
    }

    private async Task<AnalysisSnapshot> CreateCoreAsync(string capabilityId,string inputIdentity,
        IReadOnlyList<long> artifactIds,IReadOnlyDictionary<string,string>? defaultRevisions,CancellationToken cancellationToken,
        IReadOnlyList<ModelProduct>? modelProducts = null)
    {
        ArgumentNullException.ThrowIfNull(artifactIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputIdentity);
        if (inputIdentity.Length > 1024) throw new ArgumentException("Input identity is too long", nameof(inputIdentity));
        if (artifactIds.Count > MaximumArtifacts) throw new ArgumentException("Too many artifacts in one snapshot", nameof(artifactIds));
        var ids = artifactIds.Order().ToArray();
        if (ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Length)
            throw new ArgumentException("Artifact IDs must be positive and unique", nameof(artifactIds));
        var models = modelProducts ?? [];
        ModelProductStore.Validate(models);
        await NodeSearchNormalization.EnsureAsync(dataSource,ids,cancellationToken);
        await using var c = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await c.BeginTransactionAsync(cancellationToken);
        await LockAsync(c, tx, "create:" + capabilityId + ":" + inputIdentity, cancellationToken);
        if (models.Count > 128 || models.Select(m => m.Id).Distinct().Count() != models.Count)
            throw new ArgumentException("model membership must be unique and bounded");
        var defaults=await ResolveDefaultsAsync(c,tx,ids,defaultRevisions,cancellationToken,models.Select(m=>m.Id).ToArray());
        await using (var existing = new NpgsqlCommand("""
            select id from analysis_snapshot where capability_id=$1 and input_identity=$2
            """, c, tx))
        {
            existing.Parameters.AddWithValue(capabilityId);
            existing.Parameters.AddWithValue(inputIdentity);
            if (await existing.ExecuteScalarAsync(cancellationToken) is Guid prior)
            {
                await using var matches = new NpgsqlCommand("""
                    select array_agg(artifact_id order by artifact_id) from analysis_snapshot_artifact where snapshot_id=$1
                    """, c, tx);
                matches.Parameters.AddWithValue(prior);
                var previous = await matches.ExecuteScalarAsync(cancellationToken) as long[];
                if (!(previous ?? []).SequenceEqual(ids))
                    throw new SnapshotConflictException("Input identity already selects different artifacts");
                if (!await DefaultsMatchAsync(c,tx,prior,defaults,cancellationToken))
                    throw new SnapshotConflictException("Input identity already selects different default revisions");
                if (!await ModelsMatchAsync(c,tx,prior,models,cancellationToken))
                    throw new SnapshotConflictException("Input identity already selects different model products or permissions");
                await tx.CommitAsync(cancellationToken);
                return (await GetByIdAsync(c, prior, cancellationToken))!;
            }
        }
        var id = Guid.NewGuid();
        await ExecuteAsync(c, tx, """
            insert into analysis_snapshot(id,capability_id,status,input_identity) values($1,$2,'candidate',$3)
            """, cancellationToken, id, capabilityId, inputIdentity);
        await using (var membership = new NpgsqlCommand("""
            insert into analysis_snapshot_artifact(snapshot_id,artifact_id,repository_id,commit_sha,unit_path)
            select $1,id,repository_id,commit_sha,unit_path from artifact where id=any($2)
            """, c, tx))
        {
            membership.Parameters.AddWithValue(id);
            membership.Parameters.AddWithValue(ids);
            try
            {
                if (await membership.ExecuteNonQueryAsync(cancellationToken) != ids.Length)
                    throw new ArgumentException("Snapshot references an unknown artifact", nameof(artifactIds));
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new SnapshotConflictException("Snapshot selects competing analyses for the same repository, revision and unit");
            }
        }
        foreach (var model in models)
            await ModelProductStore.Execute(c,tx, """
                insert into analysis_snapshot_model(snapshot_id,model_id,collection_identity,source_status,source_product_sha256)
                values($1,$2,$3,$4,$5)
                """, cancellationToken, id, model.Id, model.CollectionIdentity, model.SourceStatus,
                model.SourceEnvelopeBytes is { } source ? ModelSource.Digest(source) : DBNull.Value);
        await ExecuteAsync(c, tx, """
            insert into snapshot_revision(snapshot_id,repository_id,commit_sha,label,is_default)
            select distinct $1,sa.repository_id,sa.commit_sha,left(sa.commit_sha,12),sa.commit_sha::text=d.sha
            from (
              select repository_id,commit_sha from analysis_snapshot_artifact where snapshot_id=$1
              union select m.repository_id,m.commit_sha from analysis_snapshot_model sm join model_product m on m.id=sm.model_id where sm.snapshot_id=$1
            ) sa join unnest($2::text[],$3::text[]) d(repo,sha) on d.repo=sa.repository_id
            """, cancellationToken, id,defaults.Keys.ToArray(),defaults.Values.ToArray());
        await tx.CommitAsync(cancellationToken);
        return (await GetByIdAsync(c, id, cancellationToken))!;
    }

    public async Task<AnalysisSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var c = await dataSource.OpenConnectionAsync(cancellationToken);
        return await GetByIdAsync(c,id,cancellationToken);
    }

    private static async Task<AnalysisSnapshot?> GetByIdAsync(NpgsqlConnection c,Guid id,CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(Select + " where s.id=$1", c);
        cmd.Parameters.AddWithValue(id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<AnalysisSnapshot?> GetPublishedAsync(string capabilityId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);
        await using var c = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(Select + " join published_snapshot p on p.snapshot_id=s.id where p.capability_id=$1", c);
        cmd.Parameters.AddWithValue(capabilityId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private const string Select = """
        select s.id,s.capability_id,s.input_identity,s.status,s.generation,s.created_at,s.published_at,
          (select count(*) from analysis_snapshot_artifact a where a.snapshot_id=s.id)
        from analysis_snapshot s
        """;

    private static AnalysisSnapshot Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2),
        r.GetString(3), r.IsDBNull(4) ? null : r.GetInt64(4), r.GetDateTime(5), r.IsDBNull(6) ? null : r.GetDateTime(6), r.GetInt64(7));

    private static Task LockAsync(NpgsqlConnection c, NpgsqlTransaction tx, string scope, CancellationToken ct) =>
        ExecuteAsync(c, tx, "select pg_advisory_xact_lock(hashtextextended($1,284662))", ct, scope);

    private static async Task ExecuteAsync(NpgsqlConnection c, NpgsqlTransaction tx, string sql, CancellationToken ct, params object[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, c, tx);
        foreach (var arg in args) cmd.Parameters.AddWithValue(arg);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
