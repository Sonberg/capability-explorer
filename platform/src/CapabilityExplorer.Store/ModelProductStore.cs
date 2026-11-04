using CapabilityExplorer.Contracts;
using CapabilityExplorer.Contracts.Collection;
using Npgsql;
using NpgsqlTypes;

namespace CapabilityExplorer.Store;

public sealed record ModelProductHead(string Id,string Profile,string RepositoryId,string CommitSha,string UnitPath,
    string ModelKind,string Coverage,int DiagnosticCount,string SourceStatus,string SourceSha256);

public sealed class ModelProductStore(NpgsqlDataSource dataSource)
{
    public async Task ImportAsync(IReadOnlyList<ModelProduct> products, CancellationToken ct = default)
    {
        Validate(products);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await InsertAsync(connection,tx,products,ct);
        await tx.CommitAsync(ct);
    }

    internal static void Validate(IReadOnlyList<ModelProduct> products)
    {
        ArgumentNullException.ThrowIfNull(products);
        if(products.Count>128 || products.Sum(p=>(long)p.EvidenceBytes.Length+(p.SourceEnvelopeBytes?.Length??0))>134_217_728)
            throw new FormatException("model import exceeds aggregate bounds");
        foreach (var product in products)
        {
            if (ModelSource.Digest(product.EvidenceBytes) != product.ArtifactSha256 ||
                product.Id != ValidatedModelBundle.StableId(product.Profile, product.RepositoryId, product.CommitSha, product.UnitPath, product.ArtifactSha256))
                throw new FormatException("model product identity mismatch");
            var problems = EvidenceProfileSchema.Validate(product.Profile, product.Evidence);
            if (problems.Count != 0) throw new FormatException("model evidence invalid");
            if(product.Evidence.GetProperty("repositoryId").GetString()!=product.RepositoryId ||
                product.Evidence.GetProperty("commitSha").GetString()!=product.CommitSha ||
                product.Evidence.GetProperty("unitPath").GetString()!=product.UnitPath ||
                product.SourceStatus is not ("available" or "missing" or "denied") ||
                (product.SourceStatus=="available") != (product.SourceEnvelopeBytes is not null))
                throw new FormatException("model membership identity or source permission is invalid");
            if (product.Source is { } source)
            {
                _ = ModelSource.Decode(source);
                ModelSource.CheckCompanion(source, product.EvidenceBytes);
            }
        }
    }

    private static async Task InsertAsync(NpgsqlConnection connection,NpgsqlTransaction tx,
        IReadOnlyList<ModelProduct> products,CancellationToken ct)
    {
        foreach (var p in products)
        {
            var evidence = p.Evidence;
            var kind = p.Profile == "ce.candidates.v1" ? "candidate" :
                evidence.GetProperty("elements").EnumerateArray().Any(e=>e.GetProperty("kind").GetString()=="dmn") ? "dmn" : "bpmn";
            await Execute(connection, tx, "insert into repository(id) values($1) on conflict do nothing", ct, p.RepositoryId);
            await Execute(connection, tx, "insert into revision(repository_id,commit_sha) values($1,$2) on conflict do nothing", ct, p.RepositoryId, p.CommitSha);
            await Execute(connection, tx, """
                insert into model_product(id,profile,repository_id,commit_sha,unit_path,artifact_sha256,evidence_bytes,
                  model_kind,coverage,diagnostic_count,source_sha256)
                values($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11) on conflict do nothing
                """, ct, p.Id, p.Profile, p.RepositoryId, p.CommitSha, p.UnitPath, p.ArtifactSha256, p.EvidenceBytes,
                kind,evidence.GetProperty("coverage").GetProperty("status").GetString()!,
                evidence.GetProperty("diagnostics").GetArrayLength(),evidence.GetProperty("sourceSha256").GetString()!);
            if (p.SourceEnvelopeBytes is { } bytes)
                await Execute(connection, tx, "insert into model_source_product(sha256,envelope_bytes,source_bytes) values($1,$2,$3) on conflict do nothing",
                    ct, ModelSource.Digest(bytes), bytes, ModelSource.Decode(p.Source!.Value));
        }
    }

    public async Task<IReadOnlyList<ModelProductHead>> HeadsAsync(Guid snapshot,
        IReadOnlyDictionary<string,string> selection,string? after,int limit,CancellationToken ct=default)
    {
        await using var command = dataSource.CreateCommand("""
            select m.id,m.profile,m.repository_id,m.commit_sha,m.unit_path,m.model_kind,m.coverage,
              m.diagnostic_count,sm.source_status,m.source_sha256
            from analysis_snapshot_model sm join model_product m on m.id=sm.model_id
            join unnest($2::text[],$3::text[]) s(repo,sha) on s.repo=m.repository_id and s.sha=m.commit_sha::text
            where sm.snapshot_id=$1 and m.id collate "C" > $4 collate "C"
            order by m.id collate "C" limit $5
            """);
        command.Parameters.AddWithValue(snapshot); command.Parameters.AddWithValue(selection.Keys.ToArray());
        command.Parameters.AddWithValue(selection.Values.ToArray()); command.Parameters.AddWithValue(after??"");
        command.Parameters.AddWithValue(limit);
        command.CommandTimeout=5;
        var result=new List<ModelProductHead>();
        await using var reader=await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))
        {
            if(reader.GetString(4).Length>1024) throw new FormatException("stored model path exceeds limit");
            result.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),
                reader.GetString(4),reader.GetString(5),reader.GetString(6),reader.GetInt32(7),reader.GetString(8),reader.GetString(9)));
        }
        return result;
    }

    public async Task<ModelProduct?> ReadAsync(Guid snapshot,IReadOnlyDictionary<string,string> selection,string id,CancellationToken ct=default)
    {
        await using var command=dataSource.CreateCommand("""
            select m.id,m.profile,m.repository_id,m.commit_sha,m.unit_path,m.artifact_sha256,
              case when octet_length(m.evidence_bytes)<=16777216 then m.evidence_bytes end,
              sm.collection_identity,sm.source_status,
              case when octet_length(sp.envelope_bytes)<=16777216 then sp.envelope_bytes end
            from analysis_snapshot_model sm join model_product m on m.id=sm.model_id
            left join model_source_product sp on sp.sha256=sm.source_product_sha256
            join unnest($2::text[],$3::text[]) s(repo,sha) on s.repo=m.repository_id and s.sha=m.commit_sha::text
            where sm.snapshot_id=$1 and m.id=$4
            """);
        command.Parameters.AddWithValue(snapshot);command.Parameters.AddWithValue(selection.Keys.ToArray());
        command.Parameters.AddWithValue(selection.Values.ToArray());command.Parameters.AddWithValue(id);
        command.CommandTimeout=5;
        await using var reader=await command.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct)) return null;
        if(reader.IsDBNull(6)) throw new FormatException("stored model exceeds byte limit");
        var product=new ModelProduct(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),
            reader.GetString(4),reader.GetString(5),reader.GetFieldValue<byte[]>(6),reader.GetString(7),reader.GetString(8),
            reader.IsDBNull(9)?null:reader.GetFieldValue<byte[]>(9));
        if(ModelSource.Digest(product.EvidenceBytes)!=product.ArtifactSha256) throw new FormatException("stored model digest mismatch");
        return product;
    }

    internal static async Task Execute(NpgsqlConnection c, NpgsqlTransaction tx, string sql, CancellationToken ct, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, c, tx);
        foreach (var value in values)
        {
            if(value is DBNull) command.Parameters.Add(new NpgsqlParameter{NpgsqlDbType=NpgsqlDbType.Text,Value=DBNull.Value});
            else command.Parameters.AddWithValue(value);
        }
        await command.ExecuteNonQueryAsync(ct);
    }
}
