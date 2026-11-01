using System.Text;
using CapabilityExplorer.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace CapabilityExplorer.Store;

/// <summary>Versioned search projections use the same pinned invariant .NET case folding as file browsing.
/// Raw extractor facts remain unchanged. Rebuilds run in imports or explicit migration work, never read requests.</summary>
public static class NodeSearchNormalization
{
    public const string NormalizerId = "dotnet10-invariant-v1";
    public const int MaximumBatchRecords = 128;
    public const int MaximumBatchInputBytes = 8 * 1024 * 1024;
    private static readonly bool InvariantMode=AppContext.TryGetSwitch("System.Globalization.Invariant",out var enabled) && enabled;

    public static void CheckRuntime()
    {
        if(!InvariantMode) throw new InvalidOperationException("Node search normalization requires the pinned invariant globalization runtime");
    }

    public static string Fold(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        CheckRuntime();
        return value.ToUpperInvariant();
    }
    public static byte[] Sort(string value) => Encoding.UTF8.GetBytes(Fold(value));

    public static async Task EnsureAsync(NpgsqlDataSource dataSource,long[]? artifactIds=null,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        CheckRuntime();
        if(artifactIds is {Length:>SnapshotStore.MaximumArtifacts} || artifactIds?.Any(id=>id<=0)==true)
            throw new ArgumentException("Artifact IDs must be bounded positive identities",nameof(artifactIds));
        var scope=artifactIds?.Distinct().Order().ToArray();
        await using var connection=await dataSource.OpenConnectionAsync(cancellationToken);
        await EnsureOnConnectionAsync(connection,scope,cancellationToken);
    }

    internal static async Task EnsureOnConnectionAsync(NpgsqlConnection connection,long[]? artifactIds,CancellationToken ct)
    {
        CheckRuntime();
        long after=0;
        while(true)
        {
            var ids=new List<long>(MaximumBatchRecords);
            await using(var command=new NpgsqlCommand("""
                select a.id from artifact a where a.id>$1 and ($2 or a.id=any($3))
                and not exists(select 1 from artifact_search_projection p where p.artifact_id=a.id and p.normalizer_id=$4)
                order by a.id limit 128
                """,connection))
            {
                command.Parameters.AddWithValue(after);
                command.Parameters.AddWithValue(artifactIds is null);
                command.Parameters.AddWithValue(artifactIds??[]);
                command.Parameters.AddWithValue(NormalizerId);
                await using var reader=await command.ExecuteReaderAsync(ct);
                while(await reader.ReadAsync(ct)) ids.Add(reader.GetInt64(0));
            }
            if(ids.Count==0) return;
            await EnsureGroupAsync(connection,ids.ToArray(),ct);
            after=ids[^1];
        }
    }

    private sealed record Row(long Artifact,string Id,string NameFold,string SymbolFold,byte[] NameSort);

    private static async Task EnsureGroupAsync(NpgsqlConnection connection,long[] ids,CancellationToken ct)
    {
        long artifact=0; string local="";
        while(true)
        {
            await using var tx=await connection.BeginTransactionAsync(ct);
            var rows=new List<Row>(MaximumBatchRecords);
            // Only identity and encoded-size metadata enter the window. Large strings are projected only
            // after the aggregate input budget passes. Native PK collation is sufficient for worker iteration.
            await using(var command=new NpgsqlCommand("""
                with candidates as materialized (
                  select n.artifact_id,n.local_id,octet_length(n.name)::bigint+octet_length(n.symbol_key) bytes
                  from node n where n.artifact_id=any($1) and (n.artifact_id,n.local_id)>($2,$3)
                    and not exists(select 1 from node_search s where s.artifact_id=n.artifact_id
                      and s.local_id=n.local_id and s.normalizer_id=$4)
                  order by n.artifact_id,n.local_id limit 128
                ), sized as (
                  select *,sum(bytes) over(order by artifact_id,local_id) running,
                    row_number() over(order by artifact_id,local_id) position from candidates
                )
                select s.artifact_id,s.local_id,s.bytes,
                  case when s.running<=8388608 then n.name end,
                  case when s.running<=8388608 then n.symbol_key end
                from sized s join node n using(artifact_id,local_id)
                where s.running<=8388608 or s.position=1 order by s.artifact_id,s.local_id
                """,connection,tx))
            {
                command.Parameters.AddWithValue(ids); command.Parameters.AddWithValue(artifact);
                command.Parameters.AddWithValue(local); command.Parameters.AddWithValue(NormalizerId);
                await using var reader=await command.ExecuteReaderAsync(ct);
                while(await reader.ReadAsync(ct))
                {
                    if(reader.GetInt64(2)>MaximumBatchInputBytes)
                        throw new InvalidOperationException("node search projection exceeds eight MiB input budget");
                    var folded=Fold(reader.GetString(3));
                    rows.Add(new(reader.GetInt64(0),reader.GetString(1),folded,Fold(reader.GetString(4)),Encoding.UTF8.GetBytes(folded)));
                }
            }
            if(rows.Count==0)
            {
                await tx.RollbackAsync(ct);
                break;
            }
            await using(var create=new NpgsqlCommand("""
                create temporary table ce_node_search_stage(artifact_id bigint,local_id text,normalizer_id text,
                  name_fold text,symbol_fold text,name_sort bytea) on commit drop
                """,connection,tx)) await create.ExecuteNonQueryAsync(ct);
            await using(var writer=await connection.BeginBinaryImportAsync(
                "copy ce_node_search_stage(artifact_id,local_id,normalizer_id,name_fold,symbol_fold,name_sort) from stdin(format binary)",ct))
            {
                foreach(var row in rows) await WriteRowAsync(writer,row,ct);
                await writer.CompleteAsync(ct);
            }
            await using(var insert=new NpgsqlCommand("insert into node_search select * from ce_node_search_stage on conflict do nothing",connection,tx))
                await insert.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            (artifact,local)=(rows[^1].Artifact,rows[^1].Id);
        }
        await using var markTx=await connection.BeginTransactionAsync(ct);
        await using(var mark=new NpgsqlCommand("""
            insert into artifact_search_projection(artifact_id,normalizer_id)
            select a.id,$2 from artifact a where a.id=any($1) and not exists(
              select 1 from node n where n.artifact_id=a.id and not exists(select 1 from node_search s
                where s.artifact_id=n.artifact_id and s.local_id=n.local_id and s.normalizer_id=$2))
            on conflict do nothing
            """,connection,markTx))
        {
            mark.Parameters.AddWithValue(ids); mark.Parameters.AddWithValue(NormalizerId);
            await mark.ExecuteNonQueryAsync(ct);
        }
        await markTx.CommitAsync(ct);
    }

    internal static async Task WriteImportedAsync(NpgsqlConnection connection,NpgsqlTransaction tx,long artifact,
        IReadOnlyList<NodeRecord> nodes,CancellationToken ct)
    {
        await using(var writer=await connection.BeginBinaryImportAsync(
            "copy node_search(artifact_id,local_id,normalizer_id,name_fold,symbol_fold,name_sort) from stdin(format binary)",ct))
        {
            foreach(var node in nodes)
            {
                var folded=Fold(node.Name);
                await WriteRowAsync(writer,new Row(artifact,node.Id,folded,Fold(node.SymbolKey),Encoding.UTF8.GetBytes(folded)),ct);
            }
            await writer.CompleteAsync(ct);
        }
        await using var marker=new NpgsqlCommand("insert into artifact_search_projection(artifact_id,normalizer_id) values($1,$2)",connection,tx);
        marker.Parameters.AddWithValue(artifact); marker.Parameters.AddWithValue(NormalizerId);
        await marker.ExecuteNonQueryAsync(ct);
    }

    private static async Task WriteRowAsync(NpgsqlBinaryImporter writer,Row row,CancellationToken ct)
    {
        await writer.StartRowAsync(ct);
        await writer.WriteAsync(row.Artifact,NpgsqlDbType.Bigint,ct);
        await writer.WriteAsync(row.Id,NpgsqlDbType.Text,ct);
        await writer.WriteAsync(NormalizerId,NpgsqlDbType.Text,ct);
        await writer.WriteAsync(row.NameFold,NpgsqlDbType.Text,ct);
        await writer.WriteAsync(row.SymbolFold,NpgsqlDbType.Text,ct);
        await writer.WriteAsync(row.NameSort,NpgsqlDbType.Bytea,ct);
    }
}
