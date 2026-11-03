using System.Diagnostics;
using System.Text.Json.Nodes;
using CapabilityExplorer.Binding;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Store;
using Xunit;

namespace CapabilityExplorer.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class SnapshotPublishCliTests(PostgresFixture db)
{
    private static string Scope()=>"cli-"+Guid.NewGuid().ToString("N");
    private static Process StartCli(string connection,params string[] args)
    {
        var dll=Path.Combine(Fixtures.RepositoryRoot,"platform","src","CapabilityExplorer.Cli","bin","Debug","net10.0","ce.dll");
        Assert.True(File.Exists(dll),"Build the CLI before running its subprocess tests");
        var start=new ProcessStartInfo("dotnet"){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false};
        start.ArgumentList.Add(dll);
        foreach(var arg in args) start.ArgumentList.Add(arg);
        start.Environment["CE_DATABASE"]=connection;
        return Process.Start(start)!;
    }
    private static async Task<(int Exit,string Output)> Ce(string connection,params string[] args)
    {
        using var process=StartCli(connection,args);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output=process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error=process.StandardError.ReadToEndAsync(deadline.Token);
        try { await Task.WhenAll(output,error,process.WaitForExitAsync(deadline.Token)); }
        catch { if(!process.HasExited) process.Kill(true); throw; }
        return(process.ExitCode,await output+await error);
    }
    private Task<AnalysisSnapshot?> Current(string scope)=>new SnapshotStore(db.DataSource).GetPublishedAsync(scope);
    private async Task<AnalysisSnapshot> Publish(string scope,params string[] files)
    {
        var result=await Ce(db.ConnectionString,["publish","--scope",scope,..files]);
        Assert.True(result.Exit==0,result.Output);
        return(await Current(scope))!;
    }

    [Fact]
    public async Task Publish_imports_streamed_artifacts_and_retries_identically_without_advancing_generation()
    {
        using var input=new InputFiles();
        var file=input.Artifact("web-ts/v1-base/_fixture_web.jsonl");
        var scope=Scope();
        var first=await Publish(scope,file);
        Assert.Equal(1L,first.Generation);
        var again=await Ce(db.ConnectionString,"publish","--scope",scope,file);
        Assert.True(again.Exit==0,again.Output);
        Assert.Contains("unchanged",again.Output,StringComparison.Ordinal);
        Assert.Equal(first,await Current(scope));
        await using var command=db.DataSource.CreateCommand("select a.content_sha256 from artifact a join analysis_snapshot_artifact s on s.artifact_id=a.id where s.snapshot_id=$1");
        command.Parameters.AddWithValue(first.Id);
        Assert.Equal(ArtifactValidator.Validate(File.ReadAllText(file)).Artifact!.ContentSha256,await command.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_truncated_input_never_replaces_last_good_publication(bool truncated)
    {
        using var input=new InputFiles();
        var scope=Scope();
        var old=await Publish(scope,input.Artifact("web-ts/v1-base/_fixture_web.jsonl"));
        var valid=input.Artifact("web-ts/v1-base/_fixture_web.jsonl");
        var bad=Path.Combine(input.Directory,"missing.jsonl");
        if(truncated)
        {
            bad=input.Artifact("api-cs/v1-base/Sales.Domain.jsonl");
            var lines=File.ReadAllLines(bad);
            File.WriteAllLines(bad,lines[..^1]);
        }
        var failed=await Ce(db.ConnectionString,"publish","--scope",scope,valid,bad);
        Assert.Equal(1,failed.Exit);
        Assert.Contains("FAIL",failed.Output,StringComparison.Ordinal);
        Assert.DoesNotContain("publication unchanged",failed.Output,StringComparison.Ordinal);
        Assert.Equal(old,await Current(scope));
    }

    [Fact]
    public async Task Stale_generation_cannot_publish_a_different_candidate()
    {
        using var input=new InputFiles();
        var scope=Scope();
        var old=await Publish(scope,input.Artifact("web-ts/v1-base/_fixture_web.jsonl"));
        var next=input.Artifact("web-ts/v1-base/_fixture_web.jsonl");
        var result=await Ce(db.ConnectionString,"publish","--scope",scope,"--expected-generation","0",next);
        Assert.Equal(2,result.Exit);
        Assert.Equal(old,await Current(scope));
        var accepted=await Ce(db.ConnectionString,"publish","--scope",scope,"--expected-generation","1",next);
        Assert.True(accepted.Exit==0,accepted.Output);
        Assert.Equal(2L,(await Current(scope))!.Generation);
    }

    [Fact]
    public async Task Multiple_revisions_require_explicit_default_and_view_selection_is_persisted()
    {
        using var input=new InputFiles();
        var scope=Scope();
        var files=new[]{input.Artifact("api-cs/v1-base/Sales.Domain.jsonl"),input.Artifact("api-cs/v2-rename/Sales.Domain.jsonl")};
        var ambiguous=await Ce(db.ConnectionString,["publish","--scope",scope,..files]);
        Assert.Equal(2,ambiguous.Exit);
        Assert.Null(await Current(scope));
        var sha=Fixtures.Lock["api-cs"]["v1-base"];
        var explicitDefault=await Ce(db.ConnectionString,["publish","--scope",scope,"--view","api-cs:"+sha,..files]);
        Assert.True(explicitDefault.Exit==0,explicitDefault.Output);
        var published=(await Current(scope))!;
        await using var command=db.DataSource.CreateCommand("select commit_sha::text from snapshot_revision where snapshot_id=$1 and repository_id='api-cs' and is_default");
        command.Parameters.AddWithValue(published.Id);
        Assert.Equal(sha,await command.ExecuteScalarAsync());
        Assert.Equal(2L,published.ArtifactCount);
    }

    [Fact]
    public async Task Binding_snapshot_has_exactly_the_published_artifact_set_and_the_existing_binder_result()
    {
        using var input=new InputFiles();
        var scope=Scope();
        var files=new[]{input.Artifact("pricing-node/v1-base/_fixture_pricing.jsonl"),input.Artifact("forecast-cs/v2-contract/Forecast.Api.jsonl")};
        var bindingPath=Path.Combine(Fixtures.RepositoryRoot,"bindings.json");
        var result=await Ce(db.ConnectionString,["publish","--scope",scope,"--bindings",bindingPath,..files]);
        Assert.True(result.Exit==0,result.Output);
        var published=(await Current(scope))!;
        await using var membership=db.DataSource.CreateCommand("""
            with selected as(select artifact_id from analysis_snapshot_artifact where snapshot_id=$1),
              bound as(select b.artifact_id from binding_snapshot_artifact b join analysis_snapshot_binding l on l.binding_snapshot_id=b.snapshot_id where l.snapshot_id=$1)
            select exists(select 1 from analysis_snapshot_binding where snapshot_id=$1) and not exists(
              (select artifact_id from selected except select artifact_id from bound)
              union all (select artifact_id from bound except select artifact_id from selected))
            """);
        membership.Parameters.AddWithValue(published.Id);
        Assert.Equal(true,await membership.ExecuteScalarAsync());
        var artifacts=files.Select(f=>ArtifactValidator.Validate(File.ReadAllText(f)).Artifact!).ToArray();
        var expected=Binder.Bind(artifacts.Select(a=>(new LoadedFrom(a.Header.RepositoryId,a.Header.CommitSha,a.Header.Unit.Path),a)),BindingFile.Load(bindingPath));
        await using var edges=db.DataSource.CreateCommand("""
            select e.local_id,e.to_key,e.status,e.rule->>'name',e.rule->>'version'
            from bound_edge e join analysis_snapshot_binding l on l.binding_snapshot_id=e.snapshot_id where l.snapshot_id=$1
            order by e.local_id
            """);
        edges.Parameters.AddWithValue(published.Id);
        var actual=new List<(string Id,string To,string Status,string Rule,string Version)>();
        await using(var reader=await edges.ExecuteReaderAsync())
            while(await reader.ReadAsync()) actual.Add((reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4)));
        Assert.Equal(expected.Edges.OrderBy(e=>e.Edge.Id,StringComparer.Ordinal).Select(e=>(e.Edge.Id,e.Edge.To!,e.Status==BindingStatus.Bound?"bound":"broken",Binder.RuleName,Binder.RuleVersion)),actual);
        var retry=await Ce(db.ConnectionString,["publish","--scope",scope,"--bindings",bindingPath,..files.Reverse()]);
        Assert.True(retry.Exit==0,retry.Output);
        Assert.Equal(published,await Current(scope));
    }

    [Fact]
    public async Task Bindings_keep_occurrences_when_the_same_edge_is_unchanged_across_two_revisions()
    {
        using var input=new InputFiles();
        var scope=Scope();
        var files=new[]{input.Artifact("pricing-node/v1-base/_fixture_pricing.jsonl"),input.Artifact("pricing-node/v2-move/_fixture_pricing.jsonl"),input.Artifact("forecast-cs/v1-base/Forecast.Api.jsonl")};
        var result=await Ce(db.ConnectionString,["publish","--scope",scope,"--view","pricing-node:"+Fixtures.Lock["pricing-node"]["v2-move"],
            "--bindings",Path.Combine(Fixtures.RepositoryRoot,"bindings.json"),..files]);
        Assert.True(result.Exit==0,result.Output);
        var published=(await Current(scope))!;
        await using var rows=db.DataSource.CreateCommand("""
            select count(*),count(distinct e.artifact_id),count(distinct e.local_id)
            from bound_edge e join analysis_snapshot_binding l on l.binding_snapshot_id=e.snapshot_id where l.snapshot_id=$1
            """);
        rows.Parameters.AddWithValue(published.Id);
        await using var reader=await rows.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2L,reader.GetInt64(0));
        Assert.Equal(2L,reader.GetInt64(1));
        Assert.Equal(1L,reader.GetInt64(2));
    }

    [Fact]
    public async Task Interrupting_the_cli_during_import_keeps_last_good_and_allows_a_clean_retry()
    {
        using var input=new InputFiles();
        var scope=Scope();
        var old=await Publish(scope,input.Artifact("web-ts/v1-base/_fixture_web.jsonl"));
        var next=input.Artifact("web-ts/v1-base/_fixture_web.jsonl");
        await using var connection=await db.DataSource.OpenConnectionAsync();
        await using var transaction=await connection.BeginTransactionAsync();
        await using(var hold=new Npgsql.NpgsqlCommand("lock table artifact in access exclusive mode",connection,transaction))
            await hold.ExecuteNonQueryAsync();
        using var process=StartCli(db.ConnectionString,"publish","--scope",scope,next);
        var output=process.StandardOutput.ReadToEndAsync();
        var error=process.StandardError.ReadToEndAsync();
        try
        {
            var waiting=false;
            for(var attempt=0;attempt<200 && !waiting;attempt++)
            {
                await using var observe=db.DataSource.CreateCommand("""
                    select exists(select 1 from pg_stat_activity where datname=current_database()
                      and query like 'insert into artifact%' and wait_event_type='Lock')
                    """);
                waiting=(bool)(await observe.ExecuteScalarAsync())!;
                if(!waiting) await Task.Delay(50);
            }
            Assert.True(waiting,"CLI must reach a real in-flight artifact insert before interruption");
            process.Kill(true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if(!process.HasExited) process.Kill(true);
            await transaction.RollbackAsync();
            await Task.WhenAll(output,error);
        }
        Assert.NotEqual(0,process.ExitCode);
        Assert.Equal(old,await Current(scope));
        var retry=await Ce(db.ConnectionString,"publish","--scope",scope,next);
        Assert.True(retry.Exit==0,retry.Output);
        Assert.Equal(2L,(await Current(scope))!.Generation);
    }

    private sealed class InputFiles : IDisposable
    {
        public string Directory{get;}=Path.Combine(Path.GetTempPath(),"ce-publish-cli-"+Guid.NewGuid().ToString("N"));
        public InputFiles()=>System.IO.Directory.CreateDirectory(Directory);
        public string Artifact(string fixture)
        {
            var rows=Fixtures.Golden(fixture).TrimEnd('\n').Split('\n').Select(s=>JsonNode.Parse(s)!.AsObject()).ToArray();
            rows[0]["configHash"]=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray()));
            var path=Path.Combine(Directory,Guid.NewGuid().ToString("N")+".jsonl");
            File.WriteAllText(path,string.Join('\n',rows.Select(r=>r.ToJsonString()))+"\n");
            return path;
        }
        public void Dispose()=>System.IO.Directory.Delete(Directory,true);
    }
}
