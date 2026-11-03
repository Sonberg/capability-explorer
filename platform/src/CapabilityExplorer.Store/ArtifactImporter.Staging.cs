using System.Text.Json;
using CapabilityExplorer.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace CapabilityExplorer.Store;

public sealed partial class ArtifactImporter
{
    private static async Task<ImportResult> CommitStagedAsync(NpgsqlConnection c, NpgsqlTransaction tx,
        HeaderRecord h, CompleteRecord complete, string sha, CancellationToken ct,
        PublicationArtifactImport? publicationAttempt = null)
    {
        await Exec(c, tx, "insert into repository(id) values($1) on conflict do nothing", ct, h.RepositoryId);
        await Exec(c, tx, "insert into revision(repository_id,commit_sha) values($1,$2) on conflict do nothing", ct, h.RepositoryId, h.CommitSha);
        long? inserted;
        await using (var insert = new NpgsqlCommand("""
            insert into artifact(repository_id,commit_sha,unit_path,unit_name,language,extractor_name,extractor_version,
                config_hash,input_hash,status,content_sha256,header,complete)
            values($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13)
            on conflict on constraint artifact_analysis_key do nothing returning id
            """, c, tx))
        {
            foreach (var value in new[] {h.RepositoryId,h.CommitSha,h.Unit.Path,h.Unit.Name,h.Language,h.Extractor.Name,
                h.Extractor.Version,h.ConfigHash,h.InputHash,complete.Status,sha}) insert.Parameters.AddWithValue(value);
            insert.Parameters.Add(new NpgsqlParameter {NpgsqlDbType=NpgsqlDbType.Jsonb,Value=JsonSerializer.Serialize(h,JsonOptions)});
            insert.Parameters.Add(new NpgsqlParameter {NpgsqlDbType=NpgsqlDbType.Jsonb,Value=JsonSerializer.Serialize(complete,JsonOptions)});
            inserted = (long?)await insert.ExecuteScalarAsync(ct);
        }
        if (inserted is null)
        {
            await using var existing = new NpgsqlCommand("""
                select id,content_sha256 from artifact where repository_id=$1 and commit_sha=$2 and unit_path=$3
                    and extractor_name=$4 and extractor_version=$5 and config_hash=$6
                """, c, tx);
            foreach (var value in new[] {h.RepositoryId,h.CommitSha,h.Unit.Path,h.Extractor.Name,h.Extractor.Version,h.ConfigHash})
                existing.Parameters.AddWithValue(value);
            await using var reader = await existing.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            var id = reader.GetInt64(0);
            var existingSha = reader.GetString(1);
            var same = existingSha == sha;
            await reader.DisposeAsync();
            if (!same)
            {
                if (publicationAttempt is not null)
                {
                    await RecordAttemptArtifactAsync(c, tx, publicationAttempt, "conflict", null, existingSha, ct);
                    await tx.CommitAsync(ct);
                }
                else await tx.RollbackAsync(ct);
                throw new ArtifactConflictException($"artifact {id} has different content for the same analysis key", existingSha);
            }
            if (publicationAttempt is not null)
            {
                await RecordAttemptArtifactAsync(c, tx, publicationAttempt, "reused", id, sha, ct);
                await tx.CommitAsync(ct);
            }
            else await tx.RollbackAsync(ct);
            await NodeSearchNormalization.EnsureOnConnectionAsync(c,[id],ct);
            return new ImportResult(id,ImportOutcome.AlreadyImported);
        }
        var artifactId = inserted.Value;
        // SQL projections preserve the exact normalized schema used by the materialized importer.
        const string projectionSql = """
            insert into evidence(artifact_id,local_id,repository_id,commit_sha,path,start_line,start_column,end_line,end_column,snippet_sha256,extractor)
            select $1,id,body->>'repositoryId',body->>'commitSha',body->>'path',
                (body#>>'{span,startLine}')::int,(body#>>'{span,startColumn}')::int,
                (body#>>'{span,endLine}')::int,(body#>>'{span,endColumn}')::int,
                body->>'snippetSha256',body->'extractor' from ce_import_record where kind='evidence';
            insert into node(artifact_id,local_id,symbol_key,node_kind,name,signature,generated,props,evidence)
            select $1,id,body->>'symbolKey',body->>'nodeKind',body->>'name',body->>'signature',
                coalesce((body->>'generated')::boolean,false),body->'props',
                array(select jsonb_array_elements_text(body->'evidence')) from ce_import_record where kind='node';
            insert into edge(artifact_id,local_id,from_key,relation,to_key,derivation,resolution,unresolved_reason,rule,props,evidence)
            select $1,id,body->>'from',body->>'relation',body->>'to',body->>'derivation',body->>'resolution',body->>'unresolvedReason',
                body->'rule',body->'props',array(select jsonb_array_elements_text(body->'evidence')) from ce_import_record where kind='edge';
            insert into calculation(artifact_id,local_id,symbol_key,ir_hash,body)
            select $1,id,body->>'symbolKey',body->>'irHash',body from ce_import_record where kind='calculation';
            insert into diagnostic(artifact_id,local_id,severity,code,message,path,affects_coverage,evidence)
            select $1,id,body->>'severity',body->>'code',body->>'message',body->>'path',coalesce((body->>'affectsCoverage')::boolean,false),
                case when body ? 'evidence' then array(select jsonb_array_elements_text(body->'evidence')) end
                from ce_import_record where kind='diagnostic';
            """;
        foreach (var statement in projectionSql.Split(';',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries))
            await Exec(c,tx,statement,ct,artifactId);
        await Exec(c,tx,"""
            insert into node_search(artifact_id,local_id,normalizer_id,name_fold,symbol_fold,name_sort)
            select $1,id,$2,name_fold,symbol_fold,name_sort from ce_import_record where kind='node'
            """,ct,artifactId,NodeSearchNormalization.NormalizerId);
        await Exec(c,tx,"insert into artifact_search_projection(artifact_id,normalizer_id) values($1,$2)",ct,artifactId,NodeSearchNormalization.NormalizerId);
        if (publicationAttempt is not null)
            await RecordAttemptArtifactAsync(c, tx, publicationAttempt, "imported", artifactId, sha, ct);
        await tx.CommitAsync(ct);
        return new ImportResult(artifactId,ImportOutcome.Imported);
    }

    private static async Task RecordAttemptArtifactAsync(NpgsqlConnection c, NpgsqlTransaction tx,
        PublicationArtifactImport attempt, string state, long? artifactId, string observedHash, CancellationToken ct)
    {
        await using (var owner = new NpgsqlCommand("""
            select 1 from analysis_publication_attempt where run_id=$1 and attempt_id=$2 for update
            """, c, tx))
        {
            owner.Parameters.AddWithValue(attempt.RunId);
            owner.Parameters.AddWithValue(attempt.AttemptId);
            if (await owner.ExecuteScalarAsync(ct) is null)
                throw new InvalidOperationException("publication attempt receipt is missing");
        }
        await using (var finished = new NpgsqlCommand("""
            select 1 from analysis_publication_attempt_result where run_id=$1 and attempt_id=$2
            """, c, tx))
        {
            finished.Parameters.AddWithValue(attempt.RunId);
            finished.Parameters.AddWithValue(attempt.AttemptId);
            if (await finished.ExecuteScalarAsync(ct) is not null)
                throw new InvalidOperationException("publication attempt already has a durable result");
        }
        await using var update = new NpgsqlCommand("""
            update analysis_publication_attempt_artifact
            set import_state=$4,artifact_id=$5,observed_content_sha256=$6
            where run_id=$1 and attempt_id=$2 and artifact_index=$3 and import_state='pending'
              and source_sha256=$7
            """, c, tx);
        update.Parameters.AddWithValue(attempt.RunId);
        update.Parameters.AddWithValue(attempt.AttemptId);
        update.Parameters.AddWithValue(attempt.Index);
        update.Parameters.AddWithValue(state);
        update.Parameters.AddWithValue((object?)artifactId ?? DBNull.Value);
        update.Parameters.AddWithValue(observedHash);
        update.Parameters.AddWithValue(attempt.SourceSha256);
        if (await update.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidOperationException("publication artifact receipt is missing, conflicting, or already final");
    }
}
