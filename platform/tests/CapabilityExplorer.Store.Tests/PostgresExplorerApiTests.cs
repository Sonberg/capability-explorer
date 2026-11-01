using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Explorer;
using CapabilityExplorer.Store;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CapabilityExplorer.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class PostgresExplorerApiTests(PostgresFixture db)
{
    private static string Scope() => "http-"+Guid.NewGuid().ToString("N");
    private static string Q(string value) => Uri.EscapeDataString(value);
    private async Task<AnalysisSnapshot> Publish(string scope,string input,string? newName=null,long expected=0)
    {
        var rows=Fixtures.Golden("web-ts/v1-base/_fixture_web.jsonl").TrimEnd('\n').Split('\n').Select(s=>JsonNode.Parse(s)!.AsObject()).ToArray();
        // A random identity, rather than a per-test fixed analysis key, keeps parallel-safe fixture imports independent.
        rows[0]["configHash"]=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray()));
        if(newName is not null) rows.First(r=>(string?)r["kind"]=="node")["name"]=newName;
        using var stream=new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n',rows.Select(r=>r.ToJsonString()))+"\n"));
        var imported=await new ArtifactImporter(db.DataSource).ImportAsync(stream);
        var store=new SnapshotStore(db.DataSource);
        var snapshot=await store.CreateAsync(scope,input,[imported.ArtifactId]);
        return await store.PublishAsync(snapshot.Id,expected);
    }
    private static async Task<JsonElement> Get(HttpClient client,string path)
    {
        using var response=await client.GetAsync(new Uri(path,UriKind.Relative));
        var bytes=await response.Content.ReadAsByteArrayAsync();
        Assert.True(response.IsSuccessStatusCode,$"{(int)response.StatusCode}: {Encoding.UTF8.GetString(bytes)}");
        Assert.True(bytes.Length<=1024*1024);
        using var json=JsonDocument.Parse(bytes);
        return json.RootElement.Clone();
    }

    [Fact]
    public async Task Database_startup_ignores_poisoned_artifacts_and_does_not_register_legacy_indexes()
    {
        var scope=Scope();
        var published=await Publish(scope,"startup");
        using var factory=new DatabaseApiFactory(db.ConnectionString,scope);
        using var client=factory.CreateClient();
        var meta=await Get(client,$"/api/meta?cap={scope}");
        Assert.Equal("postgres",meta.GetProperty("source").GetString());
        Assert.Equal(published.Id,meta.GetProperty("snapshotId").GetGuid());
        Assert.Null(factory.Services.GetService<ExplorerState>());
        Assert.Null(factory.Services.GetService<ExplorerIndex>());
        Assert.Equal("web-ts",meta.GetProperty("selection").EnumerateObject().Single().Name);
        foreach(var endpoint in new[]{"generation","view","revisions","artifacts","search","calculations"})
            await Get(client,$"/api/{endpoint}?cap={scope}");
    }

    [Fact]
    public async Task Database_startup_does_not_read_legacy_workspace_snapshot_json()
    {
        var scope=Scope();
        await Publish(scope,"poison-snapshots");
        using var factory=new DatabaseApiFactory(db.ConnectionString,scope,poisonSnapshots:true);
        using var client=factory.CreateClient();
        Assert.Equal("postgres",(await Get(client,$"/api/meta?cap={scope}")).GetProperty("source").GetString());
    }

    [Fact]
    public void Capability_configuration_is_bounded_before_loading()
    {
        var directory=Path.Combine(Path.GetTempPath(),"ce-pg-large-config-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path=Path.Combine(directory,"capabilities.json");
            File.WriteAllText(path,new string('x',1024*1024+1));
            var options=new ExplorerOptions([],null,null,path,directory);
            Assert.Throws<InvalidOperationException>(()=>new DatabaseExplorerConfiguration(options));
        }
        finally { Directory.Delete(directory,true); }
    }

    [Fact]
    public async Task Capability_repository_revocation_applies_to_retained_pages_without_restart()
    {
        var scope=Scope();
        var snapshot=await Publish(scope,"revocation");
        using var factory=new DatabaseApiFactory(db.ConnectionString,scope);
        using var client=factory.CreateClient();
        var page=await Get(client,$"/api/search?cap={scope}&limit=1");
        var cursor=page.GetProperty("nextCursor").GetString()!;
        var config=JsonNode.Parse(File.ReadAllText(factory.CapabilitiesPath))!;
        config["capabilities"]![0]!["repositories"]=new JsonArray();
        File.WriteAllText(factory.CapabilitiesPath,config.ToJsonString());
        using var revoked=await client.GetAsync(new Uri($"/api/search?cap={scope}&snapshot={snapshot.Id}&cursor={Q(cursor)}",UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden,revoked.StatusCode);
        File.WriteAllText(factory.CapabilitiesPath,"{\"capabilities\":[]}");
        using var removed=await client.GetAsync(new Uri($"/api/meta?cap={scope}",UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest,removed.StatusCode);
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("br")]
    public async Task Responses_negotiate_compression_and_decompress_to_the_original_bounded_json(string encoding)
    {
        var scope=Scope();
        await Publish(scope,"compression");
        using var factory=new DatabaseApiFactory(db.ConnectionString,scope);
        using var client=factory.CreateClient();
        using var request=new HttpRequestMessage(HttpMethod.Get,new Uri($"/api/meta?cap={scope}",UriKind.Relative));
        request.Headers.AcceptEncoding.ParseAdd(encoding);
        using var response=await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        Assert.Contains(encoding,response.Content.Headers.ContentEncoding);
        await using var compressed=await response.Content.ReadAsStreamAsync();
        await using Stream decoded=encoding=="gzip"
            ?new System.IO.Compression.GZipStream(compressed,System.IO.Compression.CompressionMode.Decompress)
            :new System.IO.Compression.BrotliStream(compressed,System.IO.Compression.CompressionMode.Decompress);
        using var json=await JsonDocument.ParseAsync(decoded);
        Assert.Equal("postgres",json.RootElement.GetProperty("source").GetString());
        Assert.Equal(scope,json.RootElement.GetProperty("capability").GetString());
        Assert.True(Encoding.UTF8.GetByteCount(json.RootElement.GetRawText())<=1024*1024);
    }

    [Fact]
    public async Task Canceled_source_reads_do_not_even_resolve_the_repository()
    {
        var resolutions=0;
        var git=new GitSource(_=>{resolutions++;return Fixtures.RepoDirectory("web-ts");});
        using var canceled=new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>git.ReadFileAsync("web-ts",Fixtures.Lock["web-ts"]["v1-base"],"src/index.ts",canceled.Token));
        Assert.Equal(0,resolutions);
    }

    [Fact]
    public async Task Search_pages_keep_their_snapshot_while_a_new_publication_becomes_current()
    {
        var scope=Scope();
        var old=await Publish(scope,"old");
        using var factory=new DatabaseApiFactory(db.ConnectionString,scope);
        using var client=factory.CreateClient();
        var first=await Get(client,$"/api/search?cap={scope}&limit=1");
        var cursor=first.GetProperty("nextCursor").GetString()!;
        await Publish(scope,"new","ZZ-changed-name",1);
        Assert.Equal(2,(await Get(client,$"/api/meta?cap={scope}")).GetProperty("generation").GetInt64());
        Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync(new Uri($"/api/search?cap={scope}&cursor={Q(cursor)}",UriKind.Relative))).StatusCode);
        var names=new Dictionary<string,string>(StringComparer.Ordinal);
        var page=first;
        for(var count=0;count<100;count++)
        {
            Assert.Equal(old.Id,page.GetProperty("snapshotId").GetGuid());
            foreach(var hit in page.GetProperty("items").EnumerateArray())
                Assert.True(names.TryAdd(hit.GetProperty("key").GetString()!,hit.GetProperty("name").GetString()!));
            if(!page.TryGetProperty("nextCursor",out var next)) break;
            page=await Get(client,$"/api/search?cap={scope}&limit=1&snapshot={old.Id}&cursor={Q(next.GetString()!)}");
        }
        var expected=ArtifactValidator.Validate(Fixtures.Golden("web-ts/v1-base/_fixture_web.jsonl")).Artifact!.Nodes;
        Assert.Equal(expected.Count,names.Count);
        Assert.All(expected,node=>Assert.Equal(node.Name,names[node.SymbolKey]));
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=101")]
    [InlineData("limit=-1")]
    [InlineData("limit=invalid")]
    [InlineData("limit=1&limit=2")]
    [InlineData("q=one&q=two")]
    [InlineData("snapshot=invalid")]
    [InlineData("cap=unknown-capability")]
    public async Task Invalid_queries_are_rejected_without_file_fallback(string query)
    {
        var scope=Scope();
        await Publish(scope,"queries");
        using var factory=new DatabaseApiFactory(db.ConnectionString,scope);
        using var client=factory.CreateClient();
        var cap=query.StartsWith("cap=",StringComparison.Ordinal)?"":$"cap={scope}&";
        using var response=await client.GetAsync(new Uri($"/api/search?{cap}{query}",UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);
        Assert.DoesNotContain(db.ConnectionString,await response.Content.ReadAsStringAsync(),StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsupported_views_and_writes_explicitly_report_pending_and_missing_snapshot_reports_unavailable()
    {
        var scope=Scope();
        using var factory=new DatabaseApiFactory(db.ConnectionString,scope);
        using var client=factory.CreateClient();
        using var meta=await client.GetAsync(new Uri($"/api/meta?cap={scope}",UriKind.Relative));
        Assert.Equal(HttpStatusCode.ServiceUnavailable,meta.StatusCode);
        foreach(var endpoint in new[]{"architecture","integrations","changes","glossary"})
        {
            using var response=await client.GetAsync(new Uri($"/api/{endpoint}?cap={scope}",UriKind.Relative));
            Assert.Equal(HttpStatusCode.NotImplemented,response.StatusCode);
            Assert.Contains("pending",await response.Content.ReadAsStringAsync(),StringComparison.Ordinal);
        }
        using var post=await client.PostAsync(new Uri("/api/analyze",UriKind.Relative),new StringContent("{}"));
        Assert.Equal(HttpStatusCode.NotImplemented,post.StatusCode);
    }

    [Fact]
    public async Task Source_requires_exact_selected_evidence_and_returns_bounded_authorized_lines()
    {
        var scope=Scope();
        await Publish(scope,"source");
        using var factory=new DatabaseApiFactory(db.ConnectionString,scope);
        using var client=factory.CreateClient();
        var artifact=ArtifactValidator.Validate(Fixtures.Golden("web-ts/v1-base/_fixture_web.jsonl")).Artifact!;
        var evidence=artifact.Evidence[0];
        var source=await Get(client,$"/api/source?cap={scope}&repo=web-ts&commit={evidence.CommitSha}&path={Q(evidence.Path)}&start={evidence.Span.StartLine}&end={evidence.Span.EndLine}");
        Assert.True(source.GetProperty("lines").GetArrayLength()<=209);
        foreach(var path in new[]{".git/config","../capabilities.json","README-not-cited.md"})
        {
            using var response=await client.GetAsync(new Uri($"/api/source?cap={scope}&repo=web-ts&commit={evidence.CommitSha}&path={Q(path)}",UriKind.Relative));
            Assert.Equal(HttpStatusCode.Forbidden,response.StatusCode);
        }
    }

    [Fact]
    public async Task A_snapshot_outside_configured_repository_scope_is_forbidden()
    {
        var scope=Scope();
        var artifact=await new ArtifactImporter(db.DataSource).ImportAsync(Fixtures.Golden("api-cs/v1-base/Sales.Domain.jsonl"));
        var store=new SnapshotStore(db.DataSource);
        await store.PublishAsync((await store.CreateAsync(scope,"outside",[artifact.ArtifactId])).Id,0);
        using var factory=new DatabaseApiFactory(db.ConnectionString,scope);
        using var client=factory.CreateClient();
        using var response=await client.GetAsync(new Uri($"/api/search?cap={scope}",UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden,response.StatusCode);
    }

    [Fact]
    public async Task Serialized_response_limit_is_enforced_before_success_headers_for_compact_unicode_configuration()
    {
        var scope=Scope();
        await Publish(scope,"response-limit");
        using var factory=new DatabaseApiFactory(db.ConnectionString,scope,capabilityName:new string('界',200000));
        using var client=factory.CreateClient();
        using var response=await client.GetAsync(new Uri($"/api/meta?cap={scope}",UriKind.Relative),HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge,response.StatusCode);
        var bytes=await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length<1024);
        using var error=JsonDocument.Parse(bytes);
        Assert.True(error.RootElement.TryGetProperty("error",out _));
    }

    [Fact]
    public async Task Oversized_dto_is_rejected_before_any_success_headers_or_partial_json_is_sent()
    {
        var scope=Scope();
        await Publish(scope,"oversized",new string('x',1100000));
        using var factory=new DatabaseApiFactory(db.ConnectionString,scope);
        using var client=factory.CreateClient();
        using var response=await client.GetAsync(new Uri($"/api/search?cap={scope}&limit=100",UriKind.Relative),HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge,response.StatusCode);
        var bytes=await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length<1024);
        using var error=JsonDocument.Parse(bytes);
        Assert.True(error.RootElement.TryGetProperty("error",out _));
    }
}

internal sealed class DatabaseApiFactory : WebApplicationFactory<Program>
{
    private static readonly JsonSerializerOptions ConfigurationJson=new(){Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
    public string CapabilitiesPath=>Path.Combine(_directory,"capabilities.json");
    private readonly string _connection;
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"ce-pg-api-"+Guid.NewGuid().ToString("N"));
    public DatabaseApiFactory(string connection,string scope,bool poisonSnapshots=false,string capabilityName="Database HTTP test")
    {
        _connection=connection;
        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(Path.Combine(_directory,"artifacts"));
        File.WriteAllText(Path.Combine(_directory,"artifacts","poison.jsonl"),"this is not an artifact and must not be loaded");
        File.WriteAllText(Path.Combine(_directory,"capabilities.json"),JsonSerializer.Serialize(new{capabilities=new[]{new{
            id=scope,name=capabilityName,repositories=new[]{new{id="web-ts",kind="local",location=Fixtures.RepoDirectory("web-ts"),@ref="HEAD"}}
        }}},ConfigurationJson));
        if(poisonSnapshots) File.WriteAllText(Path.Combine(_directory,"snapshots.json"),"not JSON; database source must not read this legacy generated state");
    }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("source","postgres");
        builder.UseSetting("database",_connection);
        builder.UseSetting("capabilities",Path.Combine(_directory,"capabilities.json"));
        builder.UseSetting("workspace",_directory);
        builder.UseSetting("artifacts",Path.Combine(_directory,"artifacts")+","+Path.Combine(_directory,"missing"));
        builder.UseSetting("modelSettings",Path.Combine(_directory,"no-model-settings.json"));
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if(disposing && Directory.Exists(_directory)) Directory.Delete(_directory,true);
    }
}
