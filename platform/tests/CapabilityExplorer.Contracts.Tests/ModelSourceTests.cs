using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Contracts.Collection;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class ModelSourceTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("hash")]
    [InlineData("base64")]
    [InlineData("utf8")]
    [InlineData("dtd")]
    [InlineData("format")]
    [InlineData("limit")]
    [InlineData("extra")]
    public void Independent_source_validation_preserves_BOM_or_rejects_bad_bytes_and_XML(string mutation)
    {
        var bytes=Encoding.UTF8.GetBytes("\uFEFF<definitions xmlns=\"http://www.omg.org/spec/BPMN/20100524/MODEL\"/>");
        if(mutation=="dtd") bytes=Encoding.UTF8.GetBytes("<!DOCTYPE definitions [<!ENTITY x 'x'>]><definitions/>");
        if(mutation=="utf8") bytes=[0xff,0xff];
        if(mutation=="limit") bytes=new byte[1_048_577];
        var source=JsonSerializer.SerializeToNode(new {
            schema="ce.model-source.v1",repositoryId="repo",commitSha=new string('a',40),unitPath="model.bpmn",
            modelArtifactSha256=new string('b',64),sourceSha256=ModelSource.Digest(bytes),sourceFormat="bpmn+xml",
            sourceBytesBase64=Convert.ToBase64String(bytes),
        })!.AsObject();
        if(mutation=="hash") source["sourceSha256"]=new string('c',64);
        if(mutation=="base64") source["sourceBytesBase64"]=source["sourceBytesBase64"]!.GetValue<string>()+"\n";
        if(mutation=="format") source["sourceFormat"]="dmn+xml";
        if(mutation=="extra") source["rawPath"]="../../not-permitted";
        var document=JsonSerializer.SerializeToElement(source);
        if(mutation=="none") Assert.Equal(bytes,ModelSource.Decode(document));
        else Assert.ThrowsAny<Exception>(()=>ModelSource.Decode(document));
    }

    [Fact]
    public void Exact_companion_bytes_and_revision_are_required_not_merely_matching_source_digest()
    {
        var model=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            schema="ce.models.v1",repositoryId="repo",commitSha=new string('a',40),unitPath="model.bpmn",sourceSha256=new string('f',64),
        }));
        var source=JsonSerializer.SerializeToElement(new {
            repositoryId="repo",commitSha=new string('a',40),unitPath="model.bpmn",sourceSha256=new string('f',64),
            modelArtifactSha256=ModelSource.Digest(model),
        });
        ModelSource.CheckCompanion(source,model);
        Assert.Throws<FormatException>(()=>ModelSource.CheckCompanion(source,Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(model)+" ")));
        var changed=JsonNode.Parse(model)!.AsObject();
        changed["commitSha"]=new string('b',40);
        Assert.Throws<FormatException>(()=>ModelSource.CheckCompanion(source,JsonSerializer.SerializeToUtf8Bytes(changed)));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("permission")]
    [InlineData("companion")]
    [InlineData("missing-companion")]
    public async Task Independent_collection_consumer_requires_negotiated_permission_and_exact_companion(string mutation)
    {
        var root=Path.Combine(Fixtures.RepositoryRoot,".sandbox-cache","source-collection-dotnet",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var raw=Encoding.UTF8.GetBytes("\uFEFF<definitions xmlns=\"http://www.omg.org/spec/BPMN/20100524/MODEL\"/>");
            var model=JsonSerializer.SerializeToUtf8Bytes(new {
                schema="ce.models.v1",repositoryId="repo",commitSha=new string('a',40),unitPath="model.bpmn",
                sourceSha256=ModelSource.Digest(raw),contentProfile="model-content",
                coverage=new{status="full",supported=Array.Empty<string>(),limitations=Array.Empty<string>()},
                diagnostics=Array.Empty<object>(),elements=Array.Empty<object>(),relationships=Array.Empty<object>(),
            });
            var source=JsonSerializer.SerializeToUtf8Bytes(new {
                schema="ce.model-source.v1",repositoryId="repo",commitSha=new string('a',40),unitPath="model.bpmn",
                modelArtifactSha256=mutation=="companion"?new string('f',64):ModelSource.Digest(model),
                sourceSha256=ModelSource.Digest(raw),sourceFormat="bpmn+xml",sourceBytesBase64=Convert.ToBase64String(raw),
            });
            await File.WriteAllBytesAsync(Path.Combine(root,"model.json"),model);
            await File.WriteAllBytesAsync(Path.Combine(root,"source.json"),source);
            var inputs=new List<CollectionInputPlan> {
                new("repo","ce.model-source.v1","source.json","model.bpmn",ModelSource.Digest(source),source.Length,true,"model-content"),
            };
            if(mutation!="missing-companion")
                inputs.Add(new("repo","ce.models.v1","model.json","model.bpmn",ModelSource.Digest(model),model.Length,true,"model-content"));
            var plan=new CollectionPlan("ce.collection.plan.v1",new string('a',64),new string('b',64),new string('c',64),
                [new("repo","provider","producer","tenant","rights",new string('a',40),new string('e',40),
                    mutation=="permission"?["candidate-content"]:["model-content"])],inputs);
            var path=Path.Combine(root,"plan.json");
            await File.WriteAllTextAsync(path,JsonSerializer.Serialize(plan,JsonSerializerOptions.Web));
            var bundle=Path.Combine(root,"bundle");
            var result=await CollectionExporter.ExportAsync(path,bundle);
            Assert.Equal(mutation=="none",result.IsValid);
            if(result.IsValid)
            {
                Assert.True((await CollectionExporter.ValidateAsync(bundle)).IsValid);
                var product=Assert.Single((await ValidatedModelBundle.ReadAsync(bundle)).Products);
                Assert.Equal(raw,ModelSource.Decode(product.Source!.Value));
            }
            else Assert.False(File.Exists(Path.Combine(bundle,"collection.json")));
        }
        finally {Directory.Delete(root,true);}
    }
}
