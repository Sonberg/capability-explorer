using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CapabilityExplorer.Binding;
using CapabilityExplorer.Contracts;
using Npgsql;

namespace CapabilityExplorer.Store;

public sealed record BindingSaveResult(string SnapshotId, bool Created, int BoundEdges, int BrokenEdges, IReadOnlyList<BindingEntry> UnusedBindings);

/// <summary>
/// Persists the result of binding already-imported artifacts. Only new rows are written, and only to the binding tables.
/// </summary>
public sealed class BindingStore(NpgsqlDataSource dataSource)
{
    public async Task<BindingSaveResult> SaveAsync(
        IEnumerable<(long ArtifactId, Artifact Artifact)> artifacts,
        BindingFile file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(file);
        var list = artifacts.DistinctBy(a => a.ArtifactId).OrderBy(a => a.ArtifactId).ToList();
        var result = Binder.Bind(
            list.Select(a => (new LoadedFrom(a.Artifact.Header.RepositoryId, a.Artifact.Header.CommitSha, a.Artifact.Header.Unit.Path, a.ArtifactId), a.Artifact)),
            file);

        // Identity: the artifacts' content hashes and the bindings bytes. The same inputs are the same snapshot.
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\n', list.Select(a => a.Artifact.ContentSha256).Order(StringComparer.Ordinal)) + "\n" + file.Sha256)));
        var summary = new BindingSaveResult(
            id, true,
            result.Edges.Count(e => e.Status == BindingStatus.Bound),
            result.Edges.Count(e => e.Status == BindingStatus.Broken),
            result.UnusedBindings);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await using (var insert = new NpgsqlCommand(
            "insert into binding_snapshot (id, bindings_sha, bindings) values ($1, $2, $3) on conflict do nothing", connection, tx))
        {
            insert.Parameters.AddWithValue(id);
            insert.Parameters.AddWithValue(file.Sha256);
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb, Value = JsonSerializer.Serialize(file.Entries, JsonOptions) });
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return summary with { Created = false };
            }
        }

        foreach (var (artifactId, _) in list)
        {
            await using var link = new NpgsqlCommand("insert into binding_snapshot_artifact (snapshot_id, artifact_id) values ($1, $2)", connection, tx);
            link.Parameters.AddWithValue(id);
            link.Parameters.AddWithValue(artifactId);
            await link.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var b in result.Edges)
        {
            await using var row = new NpgsqlCommand(
                """
                insert into bound_edge (snapshot_id, local_id, artifact_id, original_local_id, from_key, relation, to_key, derivation,
                                        resolution, unresolved_reason, rule, evidence, status, hint, target, note)
                values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16)
                on conflict do nothing
                """,
                connection,
                tx);
            row.Parameters.AddWithValue(id);
            row.Parameters.AddWithValue(b.Edge.Id);
            row.Parameters.AddWithValue((long)b.Artifact.Handle!);
            row.Parameters.AddWithValue(b.Original.Id);
            row.Parameters.AddWithValue(b.Edge.From);
            row.Parameters.AddWithValue(b.Edge.Relation);
            row.Parameters.AddWithValue(b.Edge.To!);
            row.Parameters.AddWithValue(b.Edge.Derivation);
            row.Parameters.AddWithValue(b.Edge.Resolution);
            row.Parameters.AddWithValue(b.Edge.UnresolvedReason is null ? DBNull.Value : b.Edge.UnresolvedReason);
            row.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb, Value = JsonSerializer.Serialize(b.Edge.Rule, JsonOptions) });
            row.Parameters.AddWithValue(b.Edge.Evidence.ToArray());
            row.Parameters.AddWithValue(b.Status == BindingStatus.Bound ? "bound" : "broken");
            row.Parameters.AddWithValue(b.Binding.Hint);
            row.Parameters.AddWithValue(b.Binding.Target);
            row.Parameters.AddWithValue(b.Binding.Note is null ? DBNull.Value : b.Binding.Note);
            await row.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return summary;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
