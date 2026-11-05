using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Contracts.Collection;
using CapabilityExplorer.Explorer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class ModelApiTests
{
    [Fact]
    public async Task Files_membership_pages_summaries_and_serves_only_selected_verified_BOM_source()
    {
        using var fixture = await ModelFactory.Create();
        using var client = fixture.CreateClient();
        var first = await Get(client,"/api/models?limit=1");
        Assert.Equal("ce.model-views.v1",first.GetProperty("schema").GetString());
        var item = Assert.Single(first.GetProperty("items").EnumerateArray());
        Assert.False(item.TryGetProperty("evidence",out _));
        Assert.False(item.TryGetProperty("xml",out _));
        Assert.Equal(JsonValueKind.Number,first.GetProperty("generation").ValueKind);
        var scope = Scope(first);
        var second = await Get(client,$"/api/models?limit=1&cursor={Q(first.GetProperty("nextCursor").GetString()!)}&{scope}");
        Assert.NotEqual(item.GetProperty("id").GetString(),Assert.Single(second.GetProperty("items").EnumerateArray()).GetProperty("id").GetString());
        var id = item.GetProperty("id").GetString()!;
        var evidence = await Get(client,$"/api/models/evidence?id={Q(id)}&{scope}");
        Assert.Equal("ce.models.v1",evidence.GetProperty("evidence").GetProperty("schema").GetString());
        var source = await Get(client,$"/api/models/source?id={Q(id)}&{scope}");
        Assert.Equal(ModelFactory.Xml,source.GetProperty("xml").GetString());
        Assert.Equal(ModelSource.Digest(Encoding.UTF8.GetBytes(ModelFactory.Xml)),source.GetProperty("sha256").GetString());
        foreach(var body in new[]{evidence,source})
        {
            Assert.Equal(first.GetProperty("snapshot").GetString(),body.GetProperty("snapshot").GetString());
            Assert.Equal(first.GetProperty("selection").GetRawText(),body.GetProperty("selection").GetRawText());
        }
        await Status(client,$"/api/models/source?id={Q(id)}",HttpStatusCode.BadRequest);
        await Status(client,$"/api/models/source?id={Q(id)}&{scope}&path=../../secret",HttpStatusCode.OK);
        await Status(client,$"/api/models?cursor=forged&{scope}",HttpStatusCode.BadRequest);
        await Status(client,$"/api/models?limit=51",HttpStatusCode.BadRequest);
        await Status(client,$"/api/models?cap=unknown",HttpStatusCode.BadRequest);
        await Status(client,$"/api/models/source?id={Q("model:"+new string('0',64))}&{scope}",HttpStatusCode.NotFound);
        await Status(client,$"/api/models/source?id={Q(id)}&snapshot={first.GetProperty("snapshot").GetString()}&generation=999",HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Changing_revision_or_generation_cannot_reuse_selected_membership_or_cursor()
    {
        using var fixture=await ModelFactory.Create();
        using var client=fixture.CreateClient();
        var first=await Get(client,"/api/models?limit=1");
        var scope=Scope(first);
        var id=first.GetProperty("items")[0].GetProperty("id").GetString()!;
        await File.AppendAllTextAsync(fixture.CapabilitiesPath,"\n");
        var state=fixture.Services.GetRequiredService<ExplorerState>();
        state.Refresh(); // the background poll may have applied the change first
        Assert.True(state.Current.Generation>first.GetProperty("generation").GetInt64());
        await Status(client,$"/api/models/evidence?id={Q(id)}&{scope}",HttpStatusCode.Conflict);
        await Status(client,$"/api/models?limit=1&cursor={Q(first.GetProperty("nextCursor").GetString()!)}&{scope}",HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Missing_source_is_explicit_and_never_uses_repository_files()
    {
        using var fixture=await ModelFactory.Create(withSource:false);
        using var client=fixture.CreateClient();
        var page=await Get(client,"/api/models");
        var item=page.GetProperty("items")[0];
        Assert.Equal("missing",item.GetProperty("source").GetProperty("status").GetString());
        await Status(client,$"/api/models/source?id={Q(item.GetProperty("id").GetString()!)}&{Scope(page)}",HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Evidence_response_limit_includes_wrapper_and_never_truncates_a_complete_artifact()
    {
        using var fixture=await ModelFactory.Create(withSource:false,evidenceByteLength:16_777_216);
        using var client=fixture.CreateClient();
        var page=await Get(client,"/api/models");
        Assert.True(Encoding.UTF8.GetByteCount(page.GetRawText())<1_048_576);
        var id=page.GetProperty("items")[0].GetProperty("id").GetString()!;
        await Status(client,$"/api/models/evidence?id={Q(id)}&{Scope(page)}",HttpStatusCode.RequestEntityTooLarge);
    }

    private static string Q(string value)=>Uri.EscapeDataString(value);
    private static string Scope(JsonElement page)=>$"snapshot={page.GetProperty("snapshot").GetString()}&generation={page.GetProperty("generation").GetInt64()}";
    private static async Task<JsonElement> Get(HttpClient client,string path)
    {
        using var response=await client.GetAsync(new Uri(path,UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }
    private static async Task Status(HttpClient client,string path,HttpStatusCode expected)
    {
        using var response=await client.GetAsync(new Uri(path,UriKind.Relative));
        Assert.Equal(expected,response.StatusCode);
    }
}

internal sealed class ModelFactory : WebApplicationFactory<Program>
{
    public const string Xml="\uFEFF<definitions xmlns=\"http://www.omg.org/spec/BPMN/20100524/MODEL\" id=\"model\"/>";
    private readonly string _root=Path.Combine(Fixtures.RepositoryRoot,".sandbox-cache","model-api-tests",Guid.NewGuid().ToString("N"));
    public string CapabilitiesPath=>Path.Combine(_root,"capabilities.json");
    private string Bundle=>Path.Combine(_root,"bundle");

    public static async Task<ModelFactory> Create(bool withSource=true,int evidenceByteLength=0)
    {
        var factory=new ModelFactory();
        Directory.CreateDirectory(factory._root);
        await File.WriteAllTextAsync(factory.CapabilitiesPath,"{\"capabilities\":[]}");
        var inputs=new List<CollectionInputPlan>();
        for(var i=0;i<2;i++)
        {
            var unit=$"models/{i}.bpmn";
            var bytes=JsonSerializer.SerializeToUtf8Bytes(new {
                schema="ce.models.v1",repositoryId="models-test",commitSha=new string('d',40),unitPath=unit,
                sourceSha256=ModelSource.Digest(Encoding.UTF8.GetBytes(Xml)),contentProfile="model-content",
                coverage=new{status="full",supported=Array.Empty<string>(),limitations=Array.Empty<string>()},
                diagnostics=Array.Empty<object>(),elements=Array.Empty<object>(),relationships=Array.Empty<object>(),
            });
            if(evidenceByteLength!=0)
            {
                var padded=JsonNode.Parse(bytes)!.AsObject();
                padded["diagnostics"]=JsonSerializer.SerializeToNode(new[]{new{code="fixture-padding",message="",disposition="partial"}});
                var initial=JsonSerializer.SerializeToUtf8Bytes(padded).Length;
                padded["diagnostics"]![0]!["message"]=new string('x',evidenceByteLength-initial);
                bytes=JsonSerializer.SerializeToUtf8Bytes(padded);
                Assert.Equal(evidenceByteLength,bytes.Length);
            }
            await Add("ce.models.v1",bytes,$"model-{i}.json",unit);
            if(withSource)
            {
                var source=JsonSerializer.SerializeToUtf8Bytes(new {
                    schema="ce.model-source.v1",repositoryId="models-test",commitSha=new string('d',40),unitPath=unit,
                    modelArtifactSha256=ModelSource.Digest(bytes),sourceSha256=ModelSource.Digest(Encoding.UTF8.GetBytes(Xml)),
                    sourceFormat="bpmn+xml",sourceBytesBase64=Convert.ToBase64String(Encoding.UTF8.GetBytes(Xml)),
                });
                await Add("ce.model-source.v1",source,$"source-{i}.json",unit);
            }
        }
        var plan=new CollectionPlan("ce.collection.plan.v1",new string('a',64),new string('b',64),new string('c',64),
            [new("models-test","provider","producer","tenant","rights",new string('d',40),new string('e',40),["model-content"])],inputs);
        var planPath=Path.Combine(factory._root,"plan.json");
        await File.WriteAllTextAsync(planPath,JsonSerializer.Serialize(plan,JsonSerializerOptions.Web));
        var result=await CollectionExporter.ExportAsync(planPath,factory.Bundle);
        Assert.True(result.IsValid,string.Join("; ",result.Problems));
        return factory;

        async Task Add(string profile,byte[] bytes,string path,string unit)
        {
            await File.WriteAllBytesAsync(Path.Combine(factory._root,path),bytes);
            inputs.Add(new("models-test",profile,path,unit,ModelSource.Digest(bytes),bytes.Length,true,"model-content"));
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("source","files");
        builder.UseSetting("artifacts",Path.Combine(_root,"no-native-artifacts"));
        builder.UseSetting("collections",Bundle);
        builder.UseSetting("capabilities",CapabilitiesPath);
        builder.UseSetting("workspace",_root);
        builder.UseSetting("requests",Path.Combine(_root,"requests"));
        builder.UseSetting("modelSettings",Path.Combine(_root,"no-model-settings.json"));
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if(disposing && Directory.Exists(_root)) Directory.Delete(_root,true);
    }
}
