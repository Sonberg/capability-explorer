using Npgsql;

namespace CapabilityExplorer.Store;

public sealed partial class SnapshotStore
{
    /// <summary>Advances one scope atomically. The first publication expects generation zero.
    /// Conflicts leave the previous publication and all immutable candidate selections untouched.</summary>
    public async Task<AnalysisSnapshot> PublishAsync(Guid id, long expectedGeneration, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedGeneration);
        await using var c = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await c.BeginTransactionAsync(cancellationToken);
        string capability;
        await using (var find = new NpgsqlCommand("select capability_id from analysis_snapshot where id=$1", c, tx))
        {
            find.Parameters.AddWithValue(id);
            capability = (string?)await find.ExecuteScalarAsync(cancellationToken)
                ?? throw new ArgumentException("Snapshot does not exist", nameof(id));
        }
        await LockAsync(c, tx, "publish:" + capability, cancellationToken);
        long actual = 0;
        await using (var pointer = new NpgsqlCommand("select snapshot_id,generation from published_snapshot where capability_id=$1", c, tx))
        {
            pointer.Parameters.AddWithValue(capability);
            await using var reader = await pointer.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                actual = reader.GetInt64(1);
                if (reader.GetGuid(0) == id)
                {
                    await reader.DisposeAsync();
                    await tx.CommitAsync(cancellationToken);
                    return (await GetByIdAsync(c, id, cancellationToken))!;
                }
            }
        }
        if (actual != expectedGeneration) throw new SnapshotConflictException($"Publication expected generation {expectedGeneration}, current generation is {actual}");
        var generation = checked(actual + 1);
        await using (var publish = new NpgsqlCommand("""
            update analysis_snapshot set status='published',generation=$2,published_at=now()
            where id=$1 and status='candidate'
            """, c, tx))
        {
            publish.Parameters.AddWithValue(id);
            publish.Parameters.AddWithValue(generation);
            if (await publish.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new SnapshotConflictException("A historical published snapshot cannot be republished");
        }
        await ExecuteAsync(c, tx, """
            insert into published_snapshot(capability_id,snapshot_id,generation) values($1,$2,$3)
            on conflict(capability_id) do update set snapshot_id=excluded.snapshot_id,generation=excluded.generation
            """, cancellationToken, capability, id, generation);
        // Notifications only accelerate discovery. Readers can always poll the authoritative pointer.
        await ExecuteAsync(c, tx, "select pg_notify('ce_snapshot_published',$1)", cancellationToken, capability);
        await tx.CommitAsync(cancellationToken);
        return (await GetByIdAsync(c, id, cancellationToken))!;
    }
}
