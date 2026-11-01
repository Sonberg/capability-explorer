using CapabilityExplorer.Store;
using Xunit;

namespace CapabilityExplorer.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class SnapshotDefaultRevisionTests(PostgresFixture db)
{
    private readonly SnapshotStore _store=new(db.DataSource);
    private static string Scope()=>"defaults-"+Guid.NewGuid().ToString("N");
    private async Task<long> Import(string file)=>(await new ArtifactImporter(db.DataSource).ImportAsync(Fixtures.Golden(file))).ArtifactId;
    private async Task<Dictionary<string,string>> Defaults(Guid snapshot)
    {
        await using var cmd=db.DataSource.CreateCommand("select repository_id,commit_sha::text from snapshot_revision where snapshot_id=$1 and is_default");
        cmd.Parameters.AddWithValue(snapshot);
        var result=new Dictionary<string,string>(StringComparer.Ordinal);
        await using var reader=await cmd.ExecuteReaderAsync();
        while(await reader.ReadAsync()) result.Add(reader.GetString(0),reader.GetString(1));
        return result;
    }

    [Fact]
    public async Task Single_revision_repositories_get_immutable_automatic_defaults()
    {
        var snapshot=await _store.CreateAsync(Scope(),"single",[await Import("web-ts/v1-base/_fixture_web.jsonl")]);
        Assert.Equal(Fixtures.Lock["web-ts"]["v1-base"],(await Defaults(snapshot.Id))["web-ts"]);
    }

    [Fact]
    public async Task Multiple_revisions_without_explicit_default_reject_the_candidate()
    {
        var scope=Scope();
        var ids=new[]{await Import("api-cs/v1-base/Sales.Domain.jsonl"),await Import("api-cs/v2-rename/Sales.Domain.jsonl")};
        await Assert.ThrowsAsync<ArgumentException>(()=>_store.CreateAsync(scope,"ambiguous",ids));
        await using var command=db.DataSource.CreateCommand("select count(*) from analysis_snapshot where capability_id=$1");
        command.Parameters.AddWithValue(scope);
        Assert.Equal(0L,await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Explicit_partial_map_selects_exact_revision_and_single_revision_repositories_auto_complete()
    {
        var scope=Scope();
        var ids=new[]{await Import("api-cs/v1-base/Sales.Domain.jsonl"),await Import("api-cs/v2-rename/Sales.Domain.jsonl"),await Import("web-ts/v1-base/_fixture_web.jsonl")};
        var map=new Dictionary<string,string>{{"api-cs",Fixtures.Lock["api-cs"]["v1-base"]}};
        var snapshot=await _store.CreateAsync(scope,"selection",ids,map);
        var defaults=await Defaults(snapshot.Id);
        Assert.Equal(map["api-cs"],defaults["api-cs"]);
        Assert.Equal(Fixtures.Lock["web-ts"]["v1-base"],defaults["web-ts"]);
        Assert.Equal(snapshot,await _store.CreateAsync(scope,"selection",ids.Reverse().ToArray(),map));
        await _store.PublishAsync(snapshot.Id,0);
        map["api-cs"]=Fixtures.Lock["api-cs"]["v2-rename"];
        await Assert.ThrowsAsync<SnapshotConflictException>(()=>_store.CreateAsync(scope,"selection",ids,map));
        Assert.Equal(Fixtures.Lock["api-cs"]["v1-base"],(await Defaults(snapshot.Id))["api-cs"]);
    }

    [Theory]
    [InlineData("missing-repository")]
    [InlineData("missing-revision")]
    [InlineData("malformed-revision")]
    public async Task Default_map_must_reference_selected_repositories_and_revisions(string problem)
    {
        var artifact=await Import("web-ts/v1-base/_fixture_web.jsonl");
        var repo=problem=="missing-repository"?"another-repository":"web-ts";
        var sha=problem=="malformed-revision"?"HEAD":new string('0',40);
        var map=new Dictionary<string,string>{{repo,sha}};
        await Assert.ThrowsAsync<ArgumentException>(()=>_store.CreateAsync(Scope(),"invalid",[artifact],map));
    }
}
