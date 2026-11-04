using System.Net;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Contracts.Collection;
using CapabilityExplorer.Store;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

namespace CapabilityExplorer.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class ModelProductTests(PostgresFixture db)
{
    [Fact]
    public async Task Database_model_only_snapshot_serves_immutable_BOM_bytes_and_denial_changes_generation()
    {
        var model=Product();
        var store=new ModelProductStore(db.DataSource);
        await store.ImportAsync([model]);
        var snapshots=new SnapshotStore(db.DataSource);
        var cap="model-"+Guid.NewGuid().ToString("N");
        var first=await snapshots.CreateWithModelsAsync(cap,"source-permitted",[],new Dictionary<string,string>(),[model]);
        await snapshots.PublishAsync(first.Id,0);
        using var host=new ModelDatabaseFactory(db.ConnectionString,cap,model.RepositoryId);
        using var client=host.CreateClient();
        var page=await Get(client,$"/api/models?cap={cap}");
        Assert.Equal("database",page.GetProperty("mode").GetString());
        var item=Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(model.Id,item.GetProperty("id").GetString());
        Assert.Equal(model.CommitSha,page.GetProperty("selection").GetProperty(model.RepositoryId).GetString());
        var scope=$"cap={cap}&snapshot={first.Id}&generation=1&id={Uri.EscapeDataString(model.Id)}";
        var source=await Get(client,"/api/models/source?"+scope);
        Assert.Equal(Xml,source.GetProperty("xml").GetString());
        var evidence=await Get(client,"/api/models/evidence?"+scope);
        Assert.Equal("ce.models.v1",evidence.GetProperty("evidence").GetProperty("schema").GetString());
        var denied=model with {SourceStatus="denied",SourceEnvelopeBytes=null,CollectionIdentity=new string('e',64)};
        await store.ImportAsync([denied]);
        var second=await snapshots.CreateWithModelsAsync(cap,"source-denied",[],new Dictionary<string,string>(),[denied]);
        await snapshots.PublishAsync(second.Id,1);
        using var stale=await client.GetAsync(new Uri("/api/models/source?"+scope,UriKind.Relative));
        Assert.Equal(HttpStatusCode.Conflict,stale.StatusCode);
        using var denial=await client.GetAsync(new Uri($"/api/models/source?cap={cap}&snapshot={second.Id}&generation=2&id={Uri.EscapeDataString(model.Id)}",UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden,denial.StatusCode);
        await Assert.ThrowsAsync<SnapshotConflictException>(()=>
            snapshots.CreateWithModelsAsync(cap,"source-permitted",[],new Dictionary<string,string>(),[denied]));
        await using var mutation=db.DataSource.CreateCommand("update analysis_snapshot_model set source_status='missing',source_product_sha256=null where snapshot_id=$1");
        mutation.Parameters.AddWithValue(first.Id);
        await Assert.ThrowsAsync<PostgresException>(async()=>await mutation.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Union_membership_retains_native_revisions_and_rejects_tampered_model_import()
    {
        var imported=await new ArtifactImporter(db.DataSource).ImportAsync(Fixtures.Golden("web-ts/v1-base/_fixture_web.jsonl"));
        var model=Product();
        await new ModelProductStore(db.DataSource).ImportAsync([model]);
        var cap="union-"+Guid.NewGuid().ToString("N");
        var store=new SnapshotStore(db.DataSource);
        var snapshot=await store.CreateWithModelsAsync(cap,"mixed-membership",[imported.ArtifactId],new Dictionary<string,string>(),[model]);
        await store.PublishAsync(snapshot.Id,0);
        await using var revisions=db.DataSource.CreateCommand("select count(*) from snapshot_revision where snapshot_id=$1");
        revisions.Parameters.AddWithValue(snapshot.Id);
        Assert.Equal(2L,(long)(await revisions.ExecuteScalarAsync())!);
        await Assert.ThrowsAsync<FormatException>(()=>new ModelProductStore(db.DataSource).ImportAsync([model with {EvidenceBytes=[1,2,3]}]));
    }

    [Fact]
    public async Task Snapshot_creation_refuses_a_source_association_not_bound_to_its_companion()
    {
        var product=Product();
        var otherEvidence=JsonNode.Parse(product.EvidenceBytes)!.AsObject();
        otherEvidence["unitPath"]="other.bpmn";
        var otherBytes=JsonSerializer.SerializeToUtf8Bytes(otherEvidence);
        var otherSource=JsonNode.Parse(product.SourceEnvelopeBytes!)!.AsObject();
        otherSource["unitPath"]="other.bpmn";
        otherSource["modelArtifactSha256"]=ModelSource.Digest(otherBytes);
        var other=product with {Id=ValidatedModelBundle.StableId(product.Profile,product.RepositoryId,product.CommitSha,"other.bpmn",ModelSource.Digest(otherBytes)),
            UnitPath="other.bpmn",ArtifactSha256=ModelSource.Digest(otherBytes),EvidenceBytes=otherBytes,
            SourceEnvelopeBytes=JsonSerializer.SerializeToUtf8Bytes(otherSource)};
        await new ModelProductStore(db.DataSource).ImportAsync([product,other]);
        var altered=product with {SourceEnvelopeBytes=other.SourceEnvelopeBytes};
        await Assert.ThrowsAsync<FormatException>(()=>new SnapshotStore(db.DataSource).CreateWithModelsAsync(
            "bad-source-"+Guid.NewGuid().ToString("N"),"bad-source",[],new Dictionary<string,string>(),[altered]));
    }

    private const string Xml="\uFEFF<definitions xmlns=\"http://www.omg.org/spec/BPMN/20100524/MODEL\"/>";
    [Fact]
    public async Task Shipped_publish_accepts_model_only_collection_and_both_modes_share_ids_and_bound_cursors()
    {
        var root=Path.Combine(Fixtures.RepositoryRoot,".sandbox-cache","model-publish",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var products=new[]{Product(),Product("second.bpmn"),Candidate()};
            var inputs=new List<CollectionInputPlan>();
            for(var i=0;i<products.Length;i++)
            {
                var p=products[i];
                await Add(p.Profile,p.EvidenceBytes,$"evidence-{i}.json",p.UnitPath);
                if(p.SourceEnvelopeBytes is not null)
                    await Add("ce.model-source.v1",p.SourceEnvelopeBytes,$"source-{i}.json",p.UnitPath);
            }
            var plan=new CollectionPlan("ce.collection.plan.v1",new string('a',64),new string('b',64),new string('c',64),
                [new(products[0].RepositoryId,"provider","producer","tenant","rights",products[0].CommitSha,new string('e',40),["model-content","candidate-content"])],inputs);
            var planPath=Path.Combine(root,"plan.json");
            await File.WriteAllTextAsync(planPath,JsonSerializer.Serialize(plan,JsonSerializerOptions.Web));
            var bundle=Path.Combine(root,"bundle");
            var export=await CollectionExporter.ExportAsync(planPath,bundle);
            Assert.True(export.IsValid,string.Join("; ",export.Problems));
            var cap="model-cli-"+Guid.NewGuid().ToString("N");
            var start=new ProcessStartInfo("dotnet"){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false};
            start.ArgumentList.Add(Path.Combine(Fixtures.RepositoryRoot,"platform/src/CapabilityExplorer.Cli/bin/Debug/net10.0/ce.dll"));
            foreach(var arg in new[]{"publish","--scope",cap,"--collections",bundle}) start.ArgumentList.Add(arg);
            start.Environment["CE_DATABASE"]=db.ConnectionString;
            using var process=Process.Start(start)!;
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var output=process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error=process.StandardError.ReadToEndAsync(deadline.Token);
            try { await Task.WhenAll(output,error,process.WaitForExitAsync(deadline.Token)); }
            catch {if(!process.HasExited) process.Kill(true);throw;}
            Assert.True(process.ExitCode==0,await output+await error);
            using var databaseHost=new ModelDatabaseFactory(db.ConnectionString,cap,products[0].RepositoryId);
            using var filesHost=new ModelDatabaseFactory(db.ConnectionString,cap,products[0].RepositoryId,bundle);
            using var database=databaseHost.CreateClient();
            using var files=filesHost.CreateClient();
            var allDatabase=await Get(database,$"/api/models?cap={cap}");
            var allFiles=await Get(files,$"/api/models?cap={cap}");
            Assert.Equal(allFiles.GetProperty("items").GetRawText(),allDatabase.GetProperty("items").GetRawText());
            var dbPage=await Get(database,$"/api/models?cap={cap}&limit=1");
            var filePage=await Get(files,$"/api/models?cap={cap}&limit=1");
            Assert.Equal(filePage.GetProperty("items").GetRawText(),dbPage.GetProperty("items").GetRawText());
            var cursor=Uri.EscapeDataString(dbPage.GetProperty("nextCursor").GetString()!);
            var scope=$"snapshot={dbPage.GetProperty("snapshot").GetString()}&generation={dbPage.GetProperty("generation").GetInt64()}";
            var next=await Get(database,$"/api/models?cap={cap}&limit=1&cursor={cursor}&{scope}");
            Assert.NotEqual(dbPage.GetProperty("items")[0].GetProperty("id").GetString(),next.GetProperty("items")[0].GetProperty("id").GetString());
            Assert.Equal(JsonValueKind.String,next.GetProperty("nextCursor").ValueKind);
            using var changedScope=await database.GetAsync(new Uri($"/api/models?limit=1&cursor={cursor}&{scope}",UriKind.Relative));
            Assert.Equal(HttpStatusCode.ServiceUnavailable,changedScope.StatusCode);
            using var changedLimit=await database.GetAsync(new Uri($"/api/models?cap={cap}&limit=2&cursor={cursor}&{scope}",UriKind.Relative));
            Assert.Equal(HttpStatusCode.BadRequest,changedLimit.StatusCode);
            var selected=Uri.EscapeDataString(products[0].Id);
            var source=await Get(database,$"/api/models/source?cap={cap}&id={selected}&{scope}");
            Assert.Equal(Xml,source.GetProperty("xml").GetString());

            async Task Add(string profile,byte[] bytes,string path,string unit)
            {
                await File.WriteAllBytesAsync(Path.Combine(root,path),bytes);
                inputs.Add(new(products[0].RepositoryId,profile,path,unit,ModelSource.Digest(bytes),bytes.Length,true,
                    profile=="ce.candidates.v1"?"candidate-content":"model-content"));
            }

        }
        finally {Directory.Delete(root,true);}
    }

    private static ModelProduct Candidate()
    {
        var original=Product();
        const string path="candidate.cs";
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new {
            schema="ce.candidates.v1",recipe="roslyn.scalar-guards.v1",original.RepositoryId,original.CommitSha,unitPath=path,
            sourceSha256=new string('a',64),coverage=new{status="full",scope="single-file-intra-method",supportedMethods=0,unsupportedMethods=0},
            diagnostics=Array.Empty<object>(),candidates=Array.Empty<object>(),
        },JsonSerializerOptions.Web);
        var digest=ModelSource.Digest(bytes);
        return original with {Id=ValidatedModelBundle.StableId("ce.candidates.v1",original.RepositoryId,original.CommitSha,path,digest),
            Profile="ce.candidates.v1",UnitPath=path,ArtifactSha256=digest,EvidenceBytes=bytes,SourceEnvelopeBytes=null,SourceStatus="missing"};
    }

    private static ModelProduct Product(string path="model.bpmn")
    {
        var repo="model-fixture";
        var commit=new string('a',40);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new {
            schema="ce.models.v1",repositoryId=repo,commitSha=commit,unitPath=path,
            sourceSha256=ModelSource.Digest(Encoding.UTF8.GetBytes(Xml)),contentProfile="model-content",
            coverage=new{status="full",supported=Array.Empty<string>(),limitations=Array.Empty<string>()},
            diagnostics=Array.Empty<object>(),elements=Array.Empty<object>(),relationships=Array.Empty<object>(),
        });
        var source=JsonSerializer.SerializeToUtf8Bytes(new {
            schema="ce.model-source.v1",repositoryId=repo,commitSha=commit,unitPath=path,
            modelArtifactSha256=ModelSource.Digest(bytes),sourceSha256=ModelSource.Digest(Encoding.UTF8.GetBytes(Xml)),
            sourceFormat="bpmn+xml",sourceBytesBase64=Convert.ToBase64String(Encoding.UTF8.GetBytes(Xml)),
        });
        return new(ValidatedModelBundle.StableId("ce.models.v1",repo,commit,path,ModelSource.Digest(bytes)),
            "ce.models.v1",repo,commit,path,ModelSource.Digest(bytes),bytes,new string('d',64),"available",source);
    }

    private static async Task<JsonElement> Get(HttpClient client,string path)
    {
        using var response=await client.GetAsync(new Uri(path,UriKind.Relative));
        Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }
}

internal sealed class ModelDatabaseFactory : WebApplicationFactory<Program>
{
    private readonly string _connection;
    private readonly string? _bundle;
    private readonly string _root=Path.Combine(Fixtures.RepositoryRoot,".sandbox-cache","model-database-api",Guid.NewGuid().ToString("N"));
    public ModelDatabaseFactory(string connection,string cap,string repo,string? bundle=null)
    {
        _connection=connection;
        _bundle=bundle;
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root,"capabilities.json"),JsonSerializer.Serialize(new{capabilities=new[]{
            new{id=cap,name="model API",repositories=new[]{new{id=repo,kind="local",location=_root,@ref="HEAD"}}}
        }}));
    }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("source",_bundle is null?"postgres":"files");builder.UseSetting("database",_connection);
        if(_bundle is not null) builder.UseSetting("collections",_bundle);
        builder.UseSetting("capabilities",Path.Combine(_root,"capabilities.json"));
        builder.UseSetting("artifacts",Path.Combine(_root,"must-not-read-artifacts"));
        builder.UseSetting("modelSettings",Path.Combine(_root,"no-model-settings.json"));
        builder.UseSetting("workspace",_root);
        builder.UseSetting("requests",Path.Combine(_root,"requests"));
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if(disposing && Directory.Exists(_root)) Directory.Delete(_root,true);
    }
}
