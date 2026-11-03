using System.Text;
using System.Text.Json.Nodes;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Store;
using Npgsql;
using Xunit;

namespace CapabilityExplorer.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class NodeSearchNormalizationTests(PostgresFixture db)
{
    private static string Input(Action<List<JsonObject>> edit)
    {
        var rows=Fixtures.Golden("web-ts/v1-base/_fixture_web.jsonl").TrimEnd('\n').Split('\n').Select(s=>JsonNode.Parse(s)!.AsObject()).ToList();
        rows[0]["configHash"]=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray()));
        edit(rows);
        return string.Join('\n',rows.Select(r=>r.ToJsonString()))+"\n";
    }
    private Task<ImportResult> Import(string input,bool stream=true,NpgsqlDataSource? source=null)
    {
        var importer=new ArtifactImporter(source??db.DataSource);
        return stream?importer.ImportAsync(new MemoryStream(Encoding.UTF8.GetBytes(input)))
            :importer.ImportAsync(ArtifactValidator.Validate(input).Artifact!);
    }
    private async Task ClearDerived(long artifact)
    {
        foreach(var table in new[]{"artifact_search_projection","node_search"})
        {
            await using var command=db.DataSource.CreateCommand($"delete from {table} where artifact_id=$1 and normalizer_id=$2");
            command.Parameters.AddWithValue(artifact); command.Parameters.AddWithValue(NodeSearchNormalization.NormalizerId);
            await command.ExecuteNonQueryAsync();
        }
    }
    private async Task<string> RawFingerprint(long artifact)
    {
        await using var command=db.DataSource.CreateCommand("select md5(string_agg(md5(to_jsonb(n)::text),'' order by local_id)) from node n where artifact_id=$1");
        command.Parameters.AddWithValue(artifact);
        return (string)(await command.ExecuteScalarAsync())!;
    }
    private async Task AssertComplete(long artifact)
    {
        await using var command=db.DataSource.CreateCommand("""
            select exists(select 1 from artifact_search_projection where artifact_id=$1 and normalizer_id=$2),
              not exists(select 1 from node n where n.artifact_id=$1 and not exists(select 1 from node_search s
                where s.artifact_id=n.artifact_id and s.local_id=n.local_id and s.normalizer_id=$2))
            """);
        command.Parameters.AddWithValue(artifact); command.Parameters.AddWithValue(NodeSearchNormalization.NormalizerId);
        await using var reader=await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync()); Assert.True(reader.GetBoolean(0)); Assert.True(reader.GetBoolean(1));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Both_import_paths_persist_invariant_unicode_folds_and_scalar_sort_bytes_without_changing_raw_facts(bool streamed)
    {
        foreach(var name in new[]{"İstanbul","Straße","µValue","Σςσ","ﬃFactory","𐐨Factory","KKelvin","a🧪z"})
        {
            var input=Input(rows=>rows.First(r=>(string?)r["kind"]=="node")["name"]=name);
            var artifact=(await Import(input,streamed)).ArtifactId;
            await using var command=db.DataSource.CreateCommand("""
                select n.name,n.symbol_key,s.name_fold,s.symbol_fold,s.name_sort from node n join node_search s
                  on s.artifact_id=n.artifact_id and s.local_id=n.local_id and s.normalizer_id=$2
                where n.artifact_id=$1 and n.name=$3
                """);
            command.Parameters.AddWithValue(artifact); command.Parameters.AddWithValue(NodeSearchNormalization.NormalizerId); command.Parameters.AddWithValue(name);
            await using var reader=await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(name,reader.GetString(0));
            Assert.Equal(name.ToUpperInvariant(),reader.GetString(2));
            Assert.Equal(reader.GetString(1).ToUpperInvariant(),reader.GetString(3));
            Assert.Equal(Encoding.UTF8.GetBytes(name.ToUpperInvariant()),reader.GetFieldValue<byte[]>(4));
            await reader.DisposeAsync();
            await AssertComplete(artifact);
        }
    }

    [Fact]
    public void Sort_keys_match_the_pinned_ordinal_ignore_case_comparer_including_supplementary_scalars()
    {
        var values=new[]{"a","A","İ","ı","ſ","µ","Μ","ß","Σ","ς","σ","K","ﬃ","𐐀","𐐨","\uE000","\U00010000","a🧪z"};
        foreach(var left in values)
        foreach(var right in values)
            Assert.Equal(Math.Sign(StringComparer.OrdinalIgnoreCase.Compare(left,right)),
                Math.Sign(NodeSearchNormalization.Sort(left).AsSpan().SequenceCompareTo(NodeSearchNormalization.Sort(right))));
    }

    [Fact]
    public async Task Old_facts_backfill_without_jsonl_and_repeated_ensure_and_publication_never_mutate_raw_nodes()
    {
        var artifact=(await Import(Input(_=>{}))).ArtifactId;
        var before=await RawFingerprint(artifact);
        await ClearDerived(artifact);
        await NodeSearchNormalization.EnsureAsync(db.DataSource,[artifact]);
        await AssertComplete(artifact);
        await NodeSearchNormalization.EnsureAsync(db.DataSource,[artifact]);
        await ClearDerived(artifact);
        await new SnapshotStore(db.DataSource).CreateAsync("normalizer-"+Guid.NewGuid().ToString("N"),"facts",[artifact]);
        await AssertComplete(artifact);
        Assert.Equal(before,await RawFingerprint(artifact));
    }

    [Fact]
    public async Task Identical_reimport_repairs_old_facts_with_a_single_connection_budget()
    {
        var input=Input(_=>{});
        var artifact=(await Import(input)).ArtifactId;
        var builder=new NpgsqlConnectionStringBuilder(db.ConnectionString){MaxPoolSize=1};
        await using var source=NpgsqlDataSource.Create(builder.ConnectionString);
        foreach(var streamed in new[]{true,false})
        {
            await ClearDerived(artifact);
            var result=await Import(input,streamed,source).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(ImportOutcome.AlreadyImported,result.Outcome);
            await AssertComplete(artifact);
        }
    }

    [Fact]
    public async Task Zero_node_artifacts_still_receive_a_complete_projection_marker()
    {
        var input=Input(rows=>
        {
            rows.RemoveRange(1,rows.Count-2);
            rows[^1]["counts"]=new JsonObject{["node"]=0,["edge"]=0,["evidence"]=0,["calculation"]=0,["diagnostic"]=0};
            rows[^1]["coverage"]!["unresolvedEdges"]=0;
        });
        var artifact=(await Import(input)).ArtifactId;
        await AssertComplete(artifact);
        await ClearDerived(artifact);
        await NodeSearchNormalization.EnsureAsync(db.DataSource,[artifact]);
        await AssertComplete(artifact);
    }

    [Fact]
    public async Task Import_refuses_a_different_globalization_runtime_before_writing_artifacts()
    {
        var directory=Path.Combine(Path.GetTempPath(),"ce-normalizer-runtime-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var app=Path.Combine(Fixtures.RepositoryRoot,"platform","src","CapabilityExplorer.Cli","bin","Debug","net10.0","ce.dll");
            var settings=JsonNode.Parse(File.ReadAllText(Path.ChangeExtension(app,"runtimeconfig.json")))!;
            settings["runtimeOptions"]!["configProperties"]!["System.Globalization.Invariant"]=false;
            var config=Path.Combine(directory,"runtimeconfig.json");
            File.WriteAllText(config,settings.ToJsonString());
            var artifact=Path.Combine(directory,"input.jsonl");
            File.WriteAllText(artifact,Input(_=>{}));
            var before=await db.CountAllAsync();
            var start=new System.Diagnostics.ProcessStartInfo("dotnet"){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false};
            foreach(var arg in new[]{"exec","--runtimeconfig",config,app,"import",artifact}) start.ArgumentList.Add(arg);
            start.Environment["CE_DATABASE"]=db.ConnectionString;
            using var process=System.Diagnostics.Process.Start(start)!;
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var output=process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error=process.StandardError.ReadToEndAsync(timeout.Token);
            try { await Task.WhenAll(output,error,process.WaitForExitAsync(timeout.Token)); }
            finally { if(!process.HasExited) process.Kill(true); }
            Assert.NotEqual(0,process.ExitCode);
            Assert.Contains("publication was not confirmed",await output+await error,StringComparison.Ordinal);
            Assert.Equal(before,await db.CountAllAsync());
        }
        finally { Directory.Delete(directory,true); }
    }

    [Fact]
    public async Task Backfill_batches_large_rows_by_aggregate_encoded_input_bytes_before_copying()
    {
        var input=Input(rows=>
        {
            foreach(var node in rows.Where(r=>(string?)r["kind"]=="node").Take(3)) node["name"]=new string('x',3*1024*1024);
        });
        var artifact=(await Import(input)).ArtifactId;
        var before=await RawFingerprint(artifact);
        await ClearDerived(artifact);
        // Instrument the real PostgreSQL staging boundary: a batch violating the input budget fails the test.
        await db.ExecuteAsync($"""
            create table ce_normalization_batch_test(transaction_id bigint,input_bytes bigint);
            create function ce_normalization_batch_test() returns trigger language plpgsql as
            $$ declare bytes bigint; begin
              if new.artifact_id={artifact} then
                select sum(octet_length(name_fold)::bigint+octet_length(symbol_fold)) into bytes from pg_temp.ce_node_search_stage;
                if bytes>8388608 then raise exception 'normalization batch exceeded input budget'; end if;
                insert into ce_normalization_batch_test values(txid_current(),bytes);
              end if;
              return new;
            end $$;
            create trigger ce_normalization_batch_test before insert on node_search for each row execute function ce_normalization_batch_test();
            """);
        try
        {
            await NodeSearchNormalization.EnsureAsync(db.DataSource,[artifact]);
            await AssertComplete(artifact);
            Assert.Equal(before,await RawFingerprint(artifact));
            await using var command=db.DataSource.CreateCommand("select count(distinct transaction_id),max(input_bytes) from ce_normalization_batch_test");
            await using var reader=await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetInt64(0)>=2);
            Assert.True(reader.GetInt64(1)<=NodeSearchNormalization.MaximumBatchInputBytes);
        }
        finally
        {
            await db.ExecuteAsync("drop trigger ce_normalization_batch_test on node_search; drop function ce_normalization_batch_test(); drop table ce_normalization_batch_test");
        }
    }
}
