using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace CapabilityExplorer.Store;

public sealed record SnapshotEmbeddingModel(string Id, string Fingerprint, string RecipeVersion);
public sealed class EmbeddingProjectionBusyException() : Exception("Embedding projection already has an active writer.");
public sealed class EmbeddingProjectionFailedException() : Exception("Embedding projection has failed items; retry the same snapshot and model.");

/// <summary>Snapshot-owned occurrence vectors. The legacy symbol cache is never consulted.</summary>
public sealed partial class SnapshotEmbeddingStore(NpgsqlDataSource dataSource, string? writerConnectionString = null)
{
    private sealed record Input(long Artifact, string LocalId, string Text, string Hash, string State);

    public async Task<Guid> ResolvePublishedAsync(string capability, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("select snapshot_id from published_snapshot where capability_id=$1");
        command.Parameters.AddWithValue(capability);
        return await command.ExecuteScalarAsync(ct) is Guid id ? id : throw new InvalidOperationException("No published snapshot for capability.");
    }

    public async Task<int> EmbedAsync(Guid snapshot, SnapshotEmbeddingModel model,
        Func<IReadOnlyList<string>, CancellationToken, Task<float[][]>> embed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(embed);
        if (writerConnectionString is null) throw new InvalidOperationException("An explicit non-pooled writer connection is required.");
        var builder = new NpgsqlConnectionStringBuilder(writerConnectionString) { Pooling = false, Multiplexing = false };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(ct);
        var lockName = $"ce.snapshot-embedding:{snapshot:D}:{model.Fingerprint}";
        await using (var command = Command(connection, "select pg_try_advisory_lock(hashtextextended($1,590035))", lockName))
            if (!(bool)(await command.ExecuteScalarAsync(ct))!) throw new EmbeddingProjectionBusyException();
        // Disposing this non-pooled session releases the lock, including every exceptional path.
        await using (var command = Command(connection, "select 1 from analysis_snapshot where id=$1 and status='published'", snapshot))
            if (await command.ExecuteScalarAsync(ct) is null) throw new InvalidOperationException("Published snapshot is unavailable.");
        var membership = await Membership(connection, snapshot, ct);
        await using (var command = Command(connection, """
            insert into snapshot_embedding_projection(snapshot_id,model_key,model_id,recipe_version,artifact_count,artifacts_sha256)
            values($1,$2,$3,$4,$5,$6) on conflict do nothing
            """, snapshot, model.Fingerprint, model.Id, model.RecipeVersion, membership.Count, membership.Hash))
            await command.ExecuteNonQueryAsync(ct);
        await using (var check = Command(connection, """
            select 1 from snapshot_embedding_projection where snapshot_id=$1 and model_key=$2::char(64)
              and model_id=$3 and recipe_version=$4 and artifact_count=$5 and artifacts_sha256=$6
            """, snapshot, model.Fingerprint, model.Id, model.RecipeVersion, membership.Count, membership.Hash))
            if (await check.ExecuteScalarAsync(ct) is null) throw new InvalidOperationException("Projection identity does not match snapshot membership.");
        int? dimensions;
        await using (var command = Command(connection, """
            select vector_dimensions,enumeration_complete,last_success from snapshot_embedding_projection where snapshot_id=$1 and model_key=$2::char(64)
            """, snapshot, model.Fingerprint))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new SnapshotConflictException("Snapshot is unavailable.");
            dimensions = reader.IsDBNull(0) ? null : reader.GetInt32(0);
            if (reader.GetBoolean(1) && !reader.IsDBNull(2)) return 0;
        }
        var embedded = 0;
        var failed = false;
        long after = 0;
        while (true)
        {
            var artifacts = await ArtifactPage(connection, snapshot, after, ct);
            if (artifacts.Count == 0) break;
            foreach (var artifact in artifacts)
            {
                ct.ThrowIfCancellationRequested();
                await using (var command = Command(connection, """
                    insert into snapshot_embedding_artifact(snapshot_id,model_key,artifact_id) values($1,$2,$3) on conflict do nothing
                    """, snapshot, model.Fingerprint, artifact))
                    await command.ExecuteNonQueryAsync(ct);
                string? local = null;
                while (true)
                {
                    var page = await Inputs(connection, snapshot, model.Fingerprint, artifact, local, ct);
                    if (page.Count == 0) break;
                    await Insert(connection, snapshot, model.Fingerprint, page, ct);
                    var attempted = page.Where(i => i.State != "ready").ToArray();
                    if (attempted.Length > 0)
                    {
                        float[][] vectors;
                        try
                        {
                            ct.ThrowIfCancellationRequested();
                            vectors = await embed(attempted.Select(i => i.Text).ToArray(), ct);
                            ct.ThrowIfCancellationRequested();
                            EmbeddingStore.ValidateVectors(vectors, attempted.Length, dimensions);
                        }
                        catch (OperationCanceledException)
                        {
                            using var failureBudget = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                            await Finish(connection, snapshot, model.Fingerprint, attempted, null, "cancelled", failureBudget.Token);
                            throw;
                        }
                        catch (Exception e) when (e is not (OutOfMemoryException or NpgsqlException))
                        {
                            await Finish(connection, snapshot, model.Fingerprint, attempted, null,
                                e is InvalidDataException ? "invalid-provider-output" : "provider-failed", ct);
                            failed = true;
                            local = page[^1].LocalId;
                            continue;
                        }
                        dimensions ??= vectors[0].Length;
                        await Finish(connection, snapshot, model.Fingerprint, attempted, vectors, null, ct);
                        embedded = checked(embedded + attempted.Length);
                    }
                    local = page[^1].LocalId;
                }
                await using (var command = Command(connection, """
                    update snapshot_embedding_artifact set sealed=true,
                      last_success=case when pending_count=0 and failed_count=0 then coalesce(last_success,now()) else null end
                    where snapshot_id=$1 and model_key=$2::char(64) and artifact_id=$3
                    """, snapshot, model.Fingerprint, artifact))
                    await command.ExecuteNonQueryAsync(ct);
            }
            after = artifacts[^1];
        }
        await using (var command = Command(connection, """
            update snapshot_embedding_projection p set enumeration_complete=true,updated_at=now(),
              error_code=case when exists(select 1 from snapshot_embedding_artifact a where a.snapshot_id=p.snapshot_id
                and a.model_key=p.model_key and a.failed_count>0) then 'provider-failed' else null end,
              last_success=case when not exists(select 1 from snapshot_embedding_artifact a where a.snapshot_id=p.snapshot_id
                and a.model_key=p.model_key and (not a.sealed or a.pending_count>0 or a.failed_count>0))
                then coalesce(last_success,now()) else null end
            where snapshot_id=$1 and model_key=$2::char(64)
              and artifact_count=(select count(*) from analysis_snapshot_artifact sa where sa.snapshot_id=p.snapshot_id)
              and artifact_count=(select count(*) from snapshot_embedding_artifact a
                where a.snapshot_id=p.snapshot_id and a.model_key=p.model_key and a.sealed)
            """, snapshot, model.Fingerprint))
            if (await command.ExecuteNonQueryAsync(ct) != 1) throw new SnapshotConflictException("Snapshot is unavailable or membership changed.");
        if (failed) throw new EmbeddingProjectionFailedException();
        return embedded;
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, params object[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 5 };
        foreach (var value in parameters) command.Parameters.AddWithValue(value);
        return command;
    }

    private static async Task<List<long>> ArtifactPage(NpgsqlConnection connection, Guid snapshot, long after, CancellationToken ct)
    {
        await using var command = Command(connection, """
            select artifact_id from analysis_snapshot_artifact where snapshot_id=$1 and artifact_id>$2 order by artifact_id limit 64
            """, snapshot, after);
        var result = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(reader.GetInt64(0));
        return result;
    }

    private static async Task<(long Count, string Hash)> Membership(NpgsqlConnection connection, Guid snapshot, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long count = 0, after = 0;
        while (true)
        {
            var page = await ArtifactPage(connection, snapshot, after, ct);
            if (page.Count == 0) break;
            foreach (var id in page)
            {
                hash.AppendData(Encoding.UTF8.GetBytes(id.ToString(CultureInfo.InvariantCulture) + "\n"));
                count++;
            }
            after = page[^1];
        }
        return (count, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static async Task<List<Input>> Inputs(NpgsqlConnection connection, Guid snapshot, string model, long artifact, string? after, CancellationToken ct)
    {
        await using var command = Command(connection, """
            select n.local_id,n.symbol_key,n.node_kind,n.name,n.signature,
              (select array_agg(distinct e.relation||' '||e.to_key order by e.relation||' '||e.to_key)
                from edge e where e.artifact_id=n.artifact_id and e.from_key=n.symbol_key and e.to_key is not null
                and e.relation in ('CALLS','READS','WRITES','REQUESTS','PUBLISHES','CONSUMES')),
              (select c.body->'expr' from calculation c where c.artifact_id=n.artifact_id and c.symbol_key=n.symbol_key limit 1)::text,
              coalesce(i.state,'pending')
            from node n left join snapshot_embedding_item i on i.snapshot_id=$1 and i.model_key=$2::char(64)
              and i.artifact_id=n.artifact_id and i.node_local_id=n.local_id
            where n.artifact_id=$3 and ($4 or n.local_id collate "C">$5 collate "C") and not n.generated
              and (n.node_kind in ('Method','Function','Endpoint','Routine')
                or exists(select 1 from calculation c where c.artifact_id=n.artifact_id and c.symbol_key=n.symbol_key))
            order by n.local_id collate "C" limit 64
            """, snapshot, model, artifact, after is null, after ?? "");
        var result = new List<Input>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var text = EmbeddingStore.Text(reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? [] : reader.GetFieldValue<string[]>(5),
                reader.IsDBNull(6) ? null : reader.GetString(6));
            result.Add(new(artifact, reader.GetString(0), text, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))), reader.GetString(7)));
        }
        return result;
    }

    private static async Task Insert(NpgsqlConnection connection, Guid snapshot, string model, List<Input> rows, CancellationToken ct)
    {
        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var row in rows)
        {
            await using var command = Command(connection, """
                with inserted as (
                  insert into snapshot_embedding_item(snapshot_id,model_key,artifact_id,node_local_id,text_sha256)
                  values($1,$2,$3,$4,$5) on conflict do nothing returning 1
                )
                update snapshot_embedding_artifact set total_count=total_count+1,pending_count=pending_count+1
                where snapshot_id=$1 and model_key=$2::char(64) and artifact_id=$3 and exists(select 1 from inserted)
                """, snapshot, model, row.Artifact, row.LocalId, row.Hash);
            command.Transaction = transaction;
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    private static async Task Finish(NpgsqlConnection connection, Guid snapshot, string model, Input[] rows, float[][]? vectors, string? error, CancellationToken ct)
    {
        await using var transaction = await connection.BeginTransactionAsync(ct);
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            await using var command = Command(connection, """
                with prior as (
                  select state from snapshot_embedding_item where snapshot_id=$1 and model_key=$2::char(64) and artifact_id=$3 and node_local_id=$4 and state<>'ready'
                ), changed as (
                  update snapshot_embedding_item set state=$5,vec=$6::real[]::vector,
                    completed_at=case when $5='ready' then now() else null end,attempts=attempts+1,error_code=$7
                  where snapshot_id=$1 and model_key=$2::char(64) and artifact_id=$3 and node_local_id=$4 and state<>'ready' returning 1
                )
                update snapshot_embedding_artifact set
                  pending_count=pending_count-(select count(*) from prior where state='pending'),
                  failed_count=failed_count-(select count(*) from prior where state='failed')+case when $5='failed' then 1 else 0 end,
                  ready_count=ready_count+case when $5='ready' then 1 else 0 end
                where snapshot_id=$1 and model_key=$2::char(64) and artifact_id=$3 and exists(select 1 from changed)
                """, snapshot, model, row.Artifact, row.LocalId, vectors is null ? "failed" : "ready",
                vectors is null ? DBNull.Value : vectors[index], error is null ? DBNull.Value : error);
            command.Parameters[5].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Real;
            command.Parameters[6].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text;
            command.Transaction = transaction;
            await command.ExecuteNonQueryAsync(ct);
        }
        if (vectors is not null)
        {
            await using var command = Command(connection, """
                update snapshot_embedding_projection set vector_dimensions=coalesce(vector_dimensions,$3),updated_at=now()
                where snapshot_id=$1 and model_key=$2::char(64)
                """, snapshot, model, vectors[0].Length);
            command.Transaction = transaction;
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }
}
