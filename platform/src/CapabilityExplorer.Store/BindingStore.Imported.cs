using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CapabilityExplorer.Binding;
using Npgsql;
using NpgsqlTypes;

namespace CapabilityExplorer.Store;

public sealed partial class BindingStore
{
    /// <summary>Bind imported immutable facts using server-side staging and bounded edge-ID batches.</summary>
    public async Task<BindingSaveResult> SaveImportedAsync(IReadOnlyList<long> artifactIds, BindingFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifactIds);
        ArgumentNullException.ThrowIfNull(file);
        if (artifactIds.Count > 100_000 || artifactIds.Any(id => id <= 0))
            throw new ArgumentOutOfRangeException(nameof(artifactIds));
        var ids = artifactIds.Distinct().Order().ToArray();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        async Task<int> Execute(string sql, params object[] values)
        {
            await using var command = new NpgsqlCommand(sql, connection, tx);
            foreach (var value in values) command.Parameters.AddWithValue(value);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
        var hashes = new List<string>(ids.Length);
        await using (var command = new NpgsqlCommand("select content_sha256 from artifact where id=any($1) order by content_sha256 collate \"C\"", connection, tx))
        {
            command.Parameters.AddWithValue(ids);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) hashes.Add(reader.GetString(0));
        }
        if (hashes.Count != ids.Length) throw new ArgumentException("Every artifact must already be imported.", nameof(artifactIds));
        var snapshot = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', hashes) + "\n" + file.Sha256)));
        await Execute("create temporary table ce_binding_map(hint text primary key,target text not null,note text) on commit drop");
        await Execute("insert into ce_binding_map select * from unnest($1::text[],$2::text[],$3::text[])",
            file.Entries.Select(e => e.Hint).ToArray(), file.Entries.Select(e => e.Target).ToArray(), file.Entries.Select(e => e.Note).ToArray());
        // A first matching segment after the prefix is rewritten, exactly as Binder.Rewrite.
        await Execute("""
            create temporary table ce_binding_stage on commit drop as
            select e.artifact_id,e.local_id collate "C" original_id,e.from_key,e.relation,e.evidence,
                   m.hint,m.target,m.note,
                   array_to_string(s.parts[1:m.pos-1] || array[m.target] || s.parts[m.pos+1:cardinality(s.parts)],'|') to_key,
                   null::text bound_id
            from edge e cross join lateral (select string_to_array(e.to_key,'|') parts) s
            cross join lateral (
              select b.*, p.pos::int from unnest(s.parts) with ordinality p(segment,pos)
              join ce_binding_map b on b.hint=p.segment where p.pos>1 order by p.pos limit 1
            ) m
            where e.artifact_id=any($1) and strpos(e.to_key,'@unbound:')>0
            """, ids);
        await Execute("alter table ce_binding_stage add primary key(artifact_id,original_id)");
        await Execute("create temporary table ce_binding_targets(to_key text primary key,declared boolean not null) on commit drop");
        // Generate only keys that could rewrite to each requested target, then use node's symbol-key index.
        // Recheck the first-match rule because inverse candidates can contain an earlier matching hint.
        await Execute("""
            insert into ce_binding_targets
            select t.to_key, exists(
              select 1 from node n where n.artifact_id=any($1) and n.symbol_key=t.to_key
              union all
              select 1 from unnest(string_to_array(t.to_key,'|')) with ordinality p(segment,pos)
              join ce_binding_map b on b.target=p.segment and p.pos>1
              cross join lateral (select string_to_array(t.to_key,'|') parts) ts
              join node n on n.symbol_key=array_to_string(ts.parts[1:p.pos-1] || array[b.hint] || ts.parts[p.pos+1:cardinality(ts.parts)],'|')
                and n.artifact_id=any($1)
              cross join lateral (select string_to_array(n.symbol_key,'|') parts) ns
              cross join lateral (
                select x.*, q.pos::int from unnest(ns.parts) with ordinality q(segment,pos)
                join ce_binding_map x on x.hint=q.segment where q.pos>1 order by q.pos limit 1
              ) first_binding
              where array_to_string(ns.parts[1:first_binding.pos-1] || array[first_binding.target] || ns.parts[first_binding.pos+1:cardinality(ns.parts)],'|')=t.to_key
            ) from (select distinct to_key from ce_binding_stage where relation='REQUESTS') t
            """, ids);
        var used = new HashSet<string>(StringComparer.Ordinal);
        int bound, broken;
        await using (var command = new NpgsqlCommand("""
            select count(*) filter(where not(s.relation='REQUESTS' and not coalesce(t.declared,false))),
                   count(*) filter(where s.relation='REQUESTS' and not coalesce(t.declared,false))
            from ce_binding_stage s left join ce_binding_targets t using(to_key)
            """, connection, tx))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            bound = checked((int)reader.GetInt64(0)); broken = checked((int)reader.GetInt64(1));
        }
        await using (var command = new NpgsqlCommand("select distinct hint from ce_binding_stage", connection, tx))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) used.Add(reader.GetString(0));
        var summary = new BindingSaveResult(snapshot, true, bound, broken, file.Entries.Where(e => !used.Contains(e.Hint)).ToArray());
        await using (var command = new NpgsqlCommand("insert into binding_snapshot(id,bindings_sha,bindings) values($1,$2,$3) on conflict do nothing", connection, tx))
        {
            command.Parameters.AddWithValue(snapshot); command.Parameters.AddWithValue(file.Sha256);
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = JsonSerializer.Serialize(file.Entries, JsonOptions) });
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return summary with { Created = false };
            }
        }
        long lastArtifact = 0; string lastId = "";
        while (true)
        {
            var batch = new List<(long Artifact, string Original, string Bound)>(512);
            await using (var command = new NpgsqlCommand("""
                select artifact_id,original_id,hint,target from ce_binding_stage
                where (artifact_id,original_id collate "C")>($1,$2 collate "C")
                order by artifact_id,original_id collate "C" limit 512
                """, connection, tx))
            {
                command.Parameters.AddWithValue(lastArtifact); command.Parameters.AddWithValue(lastId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var original = reader.GetString(1);
                    var id = "e:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"bind|{original}|{reader.GetString(2)}|{reader.GetString(3)}")))[..16];
                    batch.Add((reader.GetInt64(0), original, id));
                }
            }
            if (batch.Count == 0) break;
            await Execute("""
                update ce_binding_stage s set bound_id=b.bound_id
                from unnest($1::bigint[],$2::text[],$3::text[]) b(artifact_id,original_id,bound_id)
                where s.artifact_id=b.artifact_id and s.original_id=b.original_id
                """, batch.Select(b => b.Artifact).ToArray(), batch.Select(b => b.Original).ToArray(), batch.Select(b => b.Bound).ToArray());
            (lastArtifact, lastId) = (batch[^1].Artifact, batch[^1].Original);
        }
        await Execute("insert into binding_snapshot_artifact select $1,unnest($2::bigint[])", snapshot, ids);
        await Execute("""
            insert into bound_edge(snapshot_id,local_id,artifact_id,original_local_id,from_key,relation,to_key,derivation,
              resolution,unresolved_reason,rule,evidence,status,hint,target,note)
            select $1,s.bound_id,s.artifact_id,s.original_id,s.from_key,s.relation,s.to_key,'human',
              case when s.relation='REQUESTS' and not coalesce(t.declared,false) then 'unresolved' else 'resolved' end,
              case when s.relation='REQUESTS' and not coalesce(t.declared,false) then 'missing-reference' else null end,
              jsonb_build_object('name',$2::text,'version',$3::text),s.evidence,
              case when s.relation='REQUESTS' and not coalesce(t.declared,false) then 'broken' else 'bound' end,
              s.hint,s.target,s.note from ce_binding_stage s left join ce_binding_targets t using(to_key)
            order by s.artifact_id,s.original_id collate "C" on conflict do nothing
            """, snapshot, Binder.RuleName, Binder.RuleVersion);
        await tx.CommitAsync(cancellationToken);
        return summary;
    }
}
