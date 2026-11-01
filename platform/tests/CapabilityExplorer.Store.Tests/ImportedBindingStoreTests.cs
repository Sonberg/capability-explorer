using System.Text.Json;
using System.Text.Json.Nodes;
using CapabilityExplorer.Binding;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Store;
using Xunit;

namespace CapabilityExplorer.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class ImportedBindingStoreTests(PostgresFixture db)
{
    private const string Hint = "@unbound:pricing-node/@fixture/pricing:env=FORECAST_API_URL";
    private async Task<(long ArtifactId, Artifact Artifact)> Import(string path)
    {
        var artifact = ArtifactValidator.Validate(Fixtures.Golden(path)).Artifact!;
        var saved = await new ArtifactImporter(db.DataSource).ImportAsync(artifact);
        return (saved.ArtifactId, artifact);
    }
    private static BindingFile Map(string suffix, string target="forecast-cs/Forecast.Api") => BindingFile.Parse(JsonSerializer.Serialize(new
    {
        version=1, bindings=new[] {new {hint=Hint,target,note=suffix},new {hint="@unbound:unused/unit:env=NONE",target="unused",note="unused"}}
    }));

    [Theory]
    [InlineData("v1-base", 1, 0)]
    [InlineData("v2-contract", 0, 1)]
    public async Task Imported_binding_matches_Binder_with_an_occurrence_versioned_identity(string revision, int bound, int broken)
    {
        var pricing = await Import("pricing-node/v1-base/_fixture_pricing.jsonl");
        var forecast = await Import($"forecast-cs/{revision}/Forecast.Api.jsonl");
        var inputs = new[] {pricing,forecast};
        var file = Map("imported-parity-"+revision);
        var store = new BindingStore(db.DataSource);
        var before = await db.CountAllAsync();
        var saved = await store.SaveImportedAsync([forecast.ArtifactId, pricing.ArtifactId,pricing.ArtifactId], file);
        Assert.True(saved.Created);
        Assert.Equal((bound,broken),(saved.BoundEdges,saved.BrokenEdges));
        Assert.Single(saved.UnusedBindings);
        var expected = Binder.Bind(inputs.Select(a=>(new LoadedFrom(a.Artifact.Header.RepositoryId,a.Artifact.Header.CommitSha,a.Artifact.Header.Unit.Path,a.ArtifactId),a.Artifact)),file);
        await AssertRows(saved.SnapshotId, expected);
        var legacy = await store.SaveAsync(inputs.Select(a=>(a.ArtifactId,a.Artifact)),file);
        Assert.True(legacy.Created);
        Assert.NotEqual(saved.SnapshotId,legacy.SnapshotId);
        await AssertRows(legacy.SnapshotId,expected);
        Assert.Equal((legacy.BoundEdges,legacy.BrokenEdges),(saved.BoundEdges,saved.BrokenEdges));
        Assert.Equal(before,await db.CountAllAsync());
        var again = await store.SaveImportedAsync([pricing.ArtifactId,forecast.ArtifactId],file);
        Assert.False(again.Created);
        Assert.Equal(saved.SnapshotId,again.SnapshotId);
    }

    private async Task AssertRows(string snapshot, BindingResult expected)
    {
        await using var command=db.DataSource.CreateCommand("select coalesce(jsonb_agg(to_jsonb(b)-'snapshot_id' order by local_id),'[]')::text from bound_edge b where snapshot_id=$1");
        command.Parameters.AddWithValue(snapshot);
        var actual=JsonNode.Parse((string)(await command.ExecuteScalarAsync())!);
        var wanted=JsonSerializer.SerializeToNode(expected.Edges.DistinctBy(b=>b.Edge.Id).OrderBy(b=>b.Edge.Id,StringComparer.Ordinal).Select(b=>new
        {
            local_id=b.Edge.Id,artifact_id=(long)b.Artifact.Handle!,original_local_id=b.Original.Id,from_key=b.Edge.From,
            relation=b.Edge.Relation,to_key=b.Edge.To,derivation=b.Edge.Derivation,resolution=b.Edge.Resolution,
            unresolved_reason=b.Edge.UnresolvedReason,rule=new {name=Binder.RuleName,version=Binder.RuleVersion},
            evidence=b.Edge.Evidence,status=b.Status==BindingStatus.Bound?"bound":"broken",hint=b.Binding.Hint,target=b.Binding.Target,note=b.Binding.Note
        }));
        Assert.True(JsonNode.DeepEquals(wanted,actual),$"Expected {wanted}; actual {actual}");
    }

    [Fact]
    public async Task First_matching_segment_and_rewritten_declarations_match_Binder_across_multiple_batches()
    {
        const string a="@unbound:pricing-node/unit:env=A", b="@unbound:pricing-node/unit:env=B";
        var records=Fixtures.Golden("pricing-node/v1-base/_fixture_pricing.jsonl").TrimEnd('\n').Split('\n').Select(l=>JsonNode.Parse(l)!.AsObject()).ToList();
        records[0]["configHash"]=new string('d',64);
        var prototype=records.First(r=>r["kind"]!.GetValue<string>()=="edge" && r["relation"]!.GetValue<string>()=="REQUESTS");
        var declared=$"ts|pricing-node|{a}|{b}|Declared#value";
        var node=new JsonObject { ["schema"]="ce.v1",["kind"]="node",["id"]="n:imported-binding",["symbolKey"]=declared,["nodeKind"]="Endpoint",["name"]="declared",["evidence"]=prototype["evidence"]!.DeepClone() };
        records.Insert(records.FindIndex(r=>r["kind"]!.GetValue<string>()=="edge"),node);
        var edges=new List<JsonObject>();
        for(var i=0;i<516;i++)
        {
            var edge=prototype.DeepClone().AsObject();edge["id"]=$"e:imported-{i:D4}";
            edge["to"]=i==514?$"ts|pricing-node|UnitA|{b}|Declared#value":declared;
            if(i==515) { edge["relation"]="READS";edge["to"]=$"ts|pricing-node|{a}|{b}|Missing#value"; }
            edges.Add(edge);
        }
        records.InsertRange(records.FindIndex(r=>r["kind"]!.GetValue<string>()=="calculation"),edges);
        records[^1]["counts"]!["node"]=records.Count(r=>r["kind"]!.GetValue<string>()=="node");
        records[^1]["counts"]!["edge"]=records.Count(r=>r["kind"]!.GetValue<string>()=="edge");
        records[^1]["coverage"]!["unresolvedEdges"]=records.Count(r=>r["kind"]!.GetValue<string>()=="edge" && r["resolution"]!.GetValue<string>()=="unresolved");
        var validation=ArtifactValidator.Validate(string.Join('\n',records.Select(r=>r.ToJsonString()))+"\n");
        Assert.True(validation.IsValid,string.Join(";",validation.Problems));
        var artifact=validation.Artifact!;
        var imported=await new ArtifactImporter(db.DataSource).ImportAsync(artifact);
        var map=BindingFile.Parse(JsonSerializer.Serialize(new {version=1,bindings=new[]{new {hint=a,target="UnitA"},new {hint=b,target="UnitB"}}}));
        var expected=Binder.Bind([(new LoadedFrom("pricing-node",artifact.Header.CommitSha,artifact.Header.Unit.Path,imported.ArtifactId),artifact)],map);
        var saved=await new BindingStore(db.DataSource).SaveImportedAsync([imported.ArtifactId],map);
        Assert.Equal(515,saved.BoundEdges);Assert.Equal(1,saved.BrokenEdges);Assert.Empty(saved.UnusedBindings);
        await AssertRows(saved.SnapshotId,expected);
    }

    [Fact]
    public async Task Concurrent_saves_publish_once_and_empty_map_has_no_bound_edges()
    {
        var pricing=await Import("pricing-node/v1-base/_fixture_pricing.jsonl");
        var store=new BindingStore(db.DataSource);
        var file=Map("imported-concurrent");
        var saves=await Task.WhenAll(Enumerable.Range(0,3).Select(_=>store.SaveImportedAsync([pricing.ArtifactId],file)));
        Assert.Single(saves,s=>s.Created);
        Assert.Single(saves.Select(s=>s.SnapshotId).Distinct());
        Assert.All(saves,s=>Assert.Equal((0,1),(s.BoundEdges,s.BrokenEdges)));
        var empty=await store.SaveImportedAsync([pricing.ArtifactId],BindingFile.Empty);
        Assert.Equal((0,0),(empty.BoundEdges,empty.BrokenEdges));Assert.Empty(empty.UnusedBindings);
    }

    [Fact]
    public async Task Empty_inputs_and_map_are_deterministic_and_missing_or_invalid_ids_fail()
    {
        var store=new BindingStore(db.DataSource);
        var first=await store.SaveImportedAsync([],Map("empty-imported"));
        Assert.Equal((0,0),(first.BoundEdges,first.BrokenEdges));
        Assert.Equal(2,first.UnusedBindings.Count);
        Assert.False((await store.SaveImportedAsync([],Map("empty-imported"))).Created);
        await Assert.ThrowsAsync<ArgumentException>(()=>store.SaveImportedAsync([long.MaxValue],BindingFile.Empty));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(()=>store.SaveImportedAsync([0],BindingFile.Empty));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(()=>store.SaveImportedAsync(new long[100_001],BindingFile.Empty));
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>store.SaveImportedAsync([],BindingFile.Empty,cancelled.Token));
    }
}
