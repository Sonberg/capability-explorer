using System.Data;
using System.Text;
using Npgsql;

namespace CapabilityExplorer.Explorer;

/// <summary>Removal facts from one pinned snapshot, ending at each selected revision, never the current publication.</summary>
public sealed class PostgresDeprecationRemovals(NpgsqlDataSource dataSource)
{
    private const int MaximumObservations = 100_000;
    private const int MaximumPresenceBytes = 16 * 1024 * 1024;

    public async Task<IReadOnlyDictionary<string, Removal>> ReadAsync(
        ExplorerQueryContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Selection.Count > 1000)
            throw new InvalidOperationException("Removal history exceeds the repository limit.");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await using var revisions = new NpgsqlCommand("""
            select r.repository_id,r.commit_sha::text,
              case when octet_length(r.subject)<=4096 then r.subject else null end,
              coalesce(octet_length(r.subject)>4096,false)
            from snapshot_revision r join analysis_snapshot s on s.id=r.snapshot_id
            where r.snapshot_id=$1 and s.capability_id=$2 and s.generation=$3 and s.status='published'
              and r.repository_id=any($4)
              and exists(select 1 from analysis_snapshot_artifact a
                where a.snapshot_id=r.snapshot_id and a.repository_id=r.repository_id and a.commit_sha=r.commit_sha)
            order by r.repository_id collate "C",coalesce(r.commit_time,'-infinity'::timestamptz),
              r.label collate "C",r.commit_sha collate "C"
            limit 10001
            """, connection, tx) { CommandTimeout = 5 };
        revisions.Parameters.AddWithValue(context.SnapshotId);
        revisions.Parameters.AddWithValue(context.Capability);
        revisions.Parameters.AddWithValue(context.Generation);
        revisions.Parameters.AddWithValue(context.Selection.Keys.ToArray());
        var history = new Dictionary<string, List<Revision>>(StringComparer.Ordinal);
        var revisionCount = 0;
        await using (var reader = await revisions.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (++revisionCount > 10_000)
                    throw new InvalidOperationException("Removal history exceeds the revision limit.");
                if (reader.GetBoolean(3))
                    throw new InvalidOperationException("Removal history subject exceeds the supported size.");
                var repo = reader.GetString(0);
                if (!history.TryGetValue(repo, out var rows))
                    history.Add(repo, rows = []);
                rows.Add(new(reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), []));
            }
        }
        foreach (var (repository, selectedSha) in context.Selection)
        {
            if (!history.TryGetValue(repository, out var rows))
                throw new ArgumentException("Selected repository is outside the pinned snapshot.", nameof(context));
            var selected = rows.FindIndex(row => row.Sha == selectedSha);
            if (selected < 0)
                throw new ArgumentException("Selected revision is outside the pinned snapshot.", nameof(context));
            rows.RemoveRange(selected + 1, rows.Count - selected - 1);
        }

        var byRevision = history.SelectMany(pair => pair.Value.Select(row => (pair.Key, Row: row)))
            .ToDictionary(pair => (pair.Key, pair.Row.Sha), pair => pair.Row);
        await using var coverage = new NpgsqlCommand("""
            with retained as (
                select sa.repository_id,sa.commit_sha,sa.unit_path,a.status
                from analysis_snapshot_artifact sa join artifact a on a.id=sa.artifact_id
                join unnest($2::text[],$3::text[]) r(repo,sha)
                  on r.repo=sa.repository_id and r.sha=sa.commit_sha::text
                where sa.snapshot_id=$1
            )
            select exists(select 1 from retained where status<>'full')
                or exists(select 1 from retained r group by repository_id,commit_sha
                    having count(distinct unit_path) <
                        (select count(distinct unit_path) from retained expected
                         where expected.repository_id=r.repository_id))
            """, connection, tx) { CommandTimeout = 5 };
        coverage.Parameters.AddWithValue(context.SnapshotId);
        coverage.Parameters.AddWithValue(byRevision.Keys.Select(key => key.Key).ToArray());
        coverage.Parameters.AddWithValue(byRevision.Keys.Select(key => key.Sha).ToArray());
        if ((bool)(await coverage.ExecuteScalarAsync(cancellationToken))!)
        {
            // Marks are global: unknown coverage in any selected repository can hide a still-present key.
            await tx.CommitAsync(cancellationToken);
            return new Dictionary<string, Removal>(StringComparer.Ordinal);
        }
        await using var observations = new NpgsqlCommand("""
            with pinned as (
                select a.artifact_id,a.repository_id,a.commit_sha from analysis_snapshot_artifact a
                join unnest($2::text[],$3::text[]) r(repo,sha)
                  on r.repo=a.repository_id and r.sha=a.commit_sha::text
                where a.snapshot_id=$1
            )
            select p.repository_id,p.commit_sha::text,
              case when octet_length(n.symbol_key)<=16384 then n.symbol_key else null end,
              null::text,null::text,octet_length(n.symbol_key)>16384
              from pinned p join node n on n.artifact_id=p.artifact_id
            union all
            select p.repository_id,p.commit_sha::text,
              case when octet_length(e.to_key)<=16384 then e.to_key else null end,
              case when octet_length(e.relation)<=100 then e.relation else null end,
              case when octet_length(e.from_key)<=16384 then e.from_key else null end,
              octet_length(e.to_key)>16384 or octet_length(e.relation)>100 or octet_length(e.from_key)>16384
              from pinned p join edge e on e.artifact_id=p.artifact_id where e.to_key is not null
            limit 100001
            """, connection, tx) { CommandTimeout = 5 };
        observations.Parameters.AddWithValue(context.SnapshotId);
        observations.Parameters.AddWithValue(byRevision.Keys.Select(key => key.Key).ToArray());
        observations.Parameters.AddWithValue(byRevision.Keys.Select(key => key.Sha).ToArray());
        var count = 0;
        var presenceBytes = 0;
        await using (var reader = await observations.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (++count > MaximumObservations)
                    throw new InvalidOperationException("Removal history exceeds the observation limit.");
                if (reader.GetBoolean(5))
                    throw new InvalidOperationException("Removal history presence key exceeds the supported size.");
                var row = byRevision[(reader.GetString(0), reader.GetString(1))];
                var key = reader.GetString(2);
                var from = reader.IsDBNull(4) ? null : reader.GetString(4);
                presenceBytes += Encoding.UTF8.GetByteCount(key) + (from is null ? 0 : Encoding.UTF8.GetByteCount(from));
                if (presenceBytes > MaximumPresenceBytes)
                    throw new InvalidOperationException("Removal history exceeds the presence byte limit.");
                row.Keys.Add(key);
                if (reader.IsDBNull(3)) continue;
                var relation = reader.GetString(3);
                if (relation == "REQUESTS")
                    row.Keys.Add($"group|http|{Queries.Vendor(key, from!)}");
                else if (key.StartsWith("chan|", StringComparison.Ordinal))
                {
                    var tail = key[(key.LastIndexOf('|') + 1)..];
                    row.Keys.Add($"group|channel|{tail[(tail.IndexOf('/', StringComparison.Ordinal) + 1)..]}");
                }
                else if (key.StartsWith("sql|", StringComparison.Ordinal))
                {
                    var table = key.Split('#')[0];
                    row.Keys.Add($"group|data|{table[(table.LastIndexOf('|') + 1)..]}");
                }
            }
        }
        await tx.CommitAsync(cancellationToken);
        return Build(history);
    }

    private static Dictionary<string, Removal> Build(Dictionary<string, List<Revision>> histories)
    {
        var result = new Dictionary<string, Removal>(StringComparer.Ordinal);
        var stillPresent = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (repository, rows) in histories)
        {
            stillPresent.UnionWith(rows[^1].Keys);
            foreach (var key in rows.SelectMany(row => row.Keys).Distinct(StringComparer.Ordinal))
            {
                var lastSeen = rows.FindLastIndex(row => row.Keys.Contains(key));
                if (lastSeen < rows.Count - 1)
                {
                    var gone = rows[lastSeen + 1];
                    result.TryAdd(key, new Removal(repository, rows[lastSeen].Sha, gone.Sha, gone.Subject));
                }
            }
        }
        foreach (var key in stillPresent) result.Remove(key);
        return result;
    }

    private sealed record Revision(string Sha, string? Subject, HashSet<string> Keys);
}
