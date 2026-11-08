using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Contracts.Collection;
using CapabilityExplorer.Explorer;
using CapabilityExplorer.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CapabilityExplorer.Tests;

/// <summary>The same six bounded task-query operations as <c>TaskQueryApiTests</c>, exercised over the
/// Postgres-backed <see cref="PostgresTaskEvidenceSource"/> instead of files: real snapshot publication/pinning
/// (so "superseded" freshness is actually reachable, unlike files mode which keeps no history), model products
/// imported directly through <see cref="ModelProductStore"/>, and the real golden fixture corpus imported through
/// <see cref="ArtifactImporter"/> for evidence/source-excerpt coverage.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class PostgresTaskQueryTests(PostgresFixture db)
{
    private static string Cap() => "task-query-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Snapshot_resolve_reports_current_freshness_and_complete_coverage_for_a_published_snapshot()
    {
        var cap = Cap();
        await using var host = await PostgresTaskQueryHost.CreateAsync(db, cap, ["forecast-cs"]);
        await host.PublishForecastAsync(cap, "v2-contract", 0);

        var body = await host.Client.GetFromJsonAsync<JsonElement>($"/api/tasks/v1/snapshot?cap={cap}");
        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.Equal("current", body.GetProperty("snapshot").GetProperty("freshness").GetString());
        // Unlike files mode, Postgres mode always resolves and reports the capability's current snapshot id,
        // even when the pinned/implicit snapshot being described already is that current one.
        Assert.Equal(body.GetProperty("snapshot").GetProperty("id").GetString(), body.GetProperty("snapshot").GetProperty("currentId").GetString());
        Assert.Equal("within-selection-complete", body.GetProperty("coverage").GetProperty("completeness").GetString());
        Assert.Equal("database", body.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Snapshot_resolve_pins_an_older_published_snapshot_as_superseded_with_the_current_id()
    {
        var cap = Cap();
        await using var host = await PostgresTaskQueryHost.CreateAsync(db, cap, ["forecast-cs"]);
        var older = await host.PublishForecastAsync(cap, "v1-base", 0);
        var newer = await host.PublishForecastAsync(cap, "v2-contract", 1);
        Assert.NotEqual(older.Id, newer.Id);

        var body = await host.Client.GetFromJsonAsync<JsonElement>($"/api/tasks/v1/snapshot?cap={cap}&snapshot={older.Id}");
        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.Equal("superseded", body.GetProperty("snapshot").GetProperty("freshness").GetString());
        Assert.Equal(newer.Id.ToString(), body.GetProperty("snapshot").GetProperty("currentId").GetString());

        // A pinned snapshot id that was never published is still "missing" (never silently serves a draft).
        using var missing = await host.Client.GetAsync(new Uri($"/api/tasks/v1/snapshot?cap={cap}&snapshot={Guid.NewGuid()}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Models_list_and_decision_references_resolve_over_postgres_stored_model_products()
    {
        var cap = Cap();
        var repo = "model-fixture-" + Guid.NewGuid().ToString("N")[..8];
        var commit = new string('a', 40);
        var bpmn = ModelFixture.Bpmn(repo, commit);
        var dmn = ModelFixture.Dmn(repo, commit);
        await new ModelProductStore(db.DataSource).ImportAsync([bpmn, dmn]);
        var snapshot = await new SnapshotStore(db.DataSource).CreateWithModelsAsync(cap, "model-products", [], new Dictionary<string, string>(), [bpmn, dmn]);
        await new SnapshotStore(db.DataSource).PublishAsync(snapshot.Id, 0);

        await using var host = await PostgresTaskQueryHost.CreateAsync(db, cap, [repo]);
        // Ops 2-6 bind only snapshot+generation (no cap: see TaskQueryApi.MapTaskQueryApi), so every call below
        // must round-trip the snapshot/generation resolved from the one /snapshot?cap=... lookup.
        var resolved = await host.Client.GetFromJsonAsync<JsonElement>($"/api/tasks/v1/snapshot?cap={cap}");
        var snapshotId = resolved.GetProperty("snapshot").GetProperty("id").GetString();
        var generation = resolved.GetProperty("snapshot").GetProperty("generation").GetInt64();

        var list = await host.Client.GetFromJsonAsync<JsonElement>($"/api/tasks/v1/models?snapshot={snapshotId}&generation={generation}");
        Assert.Equal("ok", list.GetProperty("status").GetString());
        var ids = list.GetProperty("data").EnumerateArray().Select(i => i.GetProperty("id").GetString()).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { bpmn.Id, dmn.Id }.Order(StringComparer.Ordinal), ids);

        var refs = await host.Client.GetFromJsonAsync<JsonElement>(
            $"/api/tasks/v1/models/decision-references?snapshot={snapshotId}&generation={generation}&id={Uri.EscapeDataString(bpmn.Id)}");
        Assert.Equal("partial", refs.GetProperty("status").GetString());
        var byElement = refs.GetProperty("data").EnumerateArray().ToDictionary(i => i.GetProperty("elementId").GetString()!);
        Assert.Equal("available", byElement["Task_Available"].GetProperty("status").GetString());
        Assert.Equal(dmn.Id, byElement["Task_Available"].GetProperty("target").GetProperty("modelId").GetString());
        Assert.Equal("missing", byElement["Task_Missing"].GetProperty("status").GetString());
        Assert.Equal("external", byElement["Task_External"].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Repositories_not_granted_are_denied_and_coverage_reports_partial()
    {
        var cap = Cap();
        await using var host = await PostgresTaskQueryHost.CreateAsync(db, cap, ["forecast-cs", "pricing-node"], grantedRepositories: ["forecast-cs"]);
        var published = await host.PublishAsync(cap, 0, "forecast-cs", "pricing-node");

        var body = await host.Client.GetFromJsonAsync<JsonElement>($"/api/tasks/v1/snapshot?cap={cap}");
        Assert.Equal("partial", body.GetProperty("status").GetString());
        Assert.Equal("not-established", body.GetProperty("coverage").GetProperty("completeness").GetString());
        var repos = body.GetProperty("coverage").GetProperty("repositories").EnumerateArray()
            .ToDictionary(r => r.GetProperty("repositoryId").GetString()!, r => r.GetProperty("status").GetString());
        Assert.Equal("partial", repos["forecast-cs"]);
        Assert.Equal("denied", repos["pricing-node"]);
        _ = published;
    }

    [Fact]
    public async Task Evidence_list_and_source_excerpt_serve_only_cited_real_golden_fixture_content()
    {
        var cap = Cap();
        await using var host = await PostgresTaskQueryHost.CreateAsync(db, cap, ["forecast-cs"]);
        await host.PublishForecastAsync(cap, "v2-contract", 0);
        var resolved = await host.Client.GetFromJsonAsync<JsonElement>($"/api/tasks/v1/snapshot?cap={cap}");
        var snapshotId = resolved.GetProperty("snapshot").GetProperty("id").GetString();
        var generation = resolved.GetProperty("snapshot").GetProperty("generation").GetInt64();

        const string key = "http|forecast-cs/Forecast.Api|GET /api/v2/forecasts/{}";
        var evidence = await host.Client.GetFromJsonAsync<JsonElement>(
            $"/api/tasks/v1/evidence?snapshot={snapshotId}&generation={generation}&key={Uri.EscapeDataString(key)}");
        Assert.Equal("ok", evidence.GetProperty("status").GetString());
        var items = evidence.GetProperty("data").EnumerateArray().ToList();
        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.Equal("forecast-cs", i.GetProperty("repositoryId").GetString()));
        var citedPath = items[0].GetProperty("path").GetString()!;
        var commit = items[0].GetProperty("commitSha").GetString()!;

        var excerpt = await host.Client.GetFromJsonAsync<JsonElement>(
            $"/api/tasks/v1/source?snapshot={snapshotId}&generation={generation}&repo=forecast-cs&commit={commit}&path={Uri.EscapeDataString(citedPath)}&start=1&end=3");
        Assert.Equal("ok", excerpt.GetProperty("status").GetString());
        Assert.Equal(citedPath, excerpt.GetProperty("data").GetProperty("path").GetString());
        Assert.NotEmpty(excerpt.GetProperty("data").GetProperty("lines").EnumerateArray());

        using var uncited = await host.Client.GetAsync(new Uri(
            $"/api/tasks/v1/source?snapshot={snapshotId}&generation={generation}&repo=forecast-cs&commit={commit}&path=not/a/cited/file.cs&start=1&end=1", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, uncited.StatusCode);
        Assert.Equal("path-not-cited", (await uncited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").GetString());
    }
}

/// <summary>Builds the two synthetic Postgres-stored model products used by the decision-reference test, mirroring
/// <c>ModelProductTests.Product()</c>'s evidence/source envelope shape without pulling in the full
/// <c>CollectionExporter</c> pipeline (decision-reference resolution reads only <c>UnitPath</c>/source XML/commit,
/// so hand-built records are both simpler and sufficient here).</summary>
internal static class ModelFixture
{
    private const string BpmnXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:cex="urn:capability-explorer:model-links:v1" id="Definitions_1">
          <process id="Process_1">
            <businessRuleTask id="Task_Available" name="Approve" cex:decisionRef="pricing.dmn#Decision_Approve"/>
            <businessRuleTask id="Task_Missing" name="Check" cex:decisionRef="other.dmn#Decision_Other"/>
            <businessRuleTask id="Task_External" name="Bad" cex:decisionRef="../escape.dmn#Decision_Bad"/>
          </process>
        </definitions>
        """;
    private const string DmnXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="https://www.omg.org/spec/DMN/20191111/MODEL/" id="Definitions_DMN" name="pricing">
          <decision id="Decision_Approve" name="Approve"/>
        </definitions>
        """;

    public static ModelProduct Bpmn(string repo, string commit) => Build(repo, commit, "models/process.bpmn", BpmnXml, "bpmn+xml", dmn: false);
    public static ModelProduct Dmn(string repo, string commit) => Build(repo, commit, "models/pricing.dmn", DmnXml, "dmn+xml", dmn: true);

    private static ModelProduct Build(string repo, string commit, string path, string xml, string sourceFormat, bool dmn)
    {
        var sourceBytes = Encoding.UTF8.GetBytes(xml);
        var evidenceBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "ce.models.v1",
            repositoryId = repo,
            commitSha = commit,
            unitPath = path,
            sourceSha256 = ModelSource.Digest(sourceBytes),
            contentProfile = "model-content",
            coverage = new { status = "full", supported = Array.Empty<string>(), limitations = Array.Empty<string>() },
            diagnostics = Array.Empty<object>(),
            relationships = Array.Empty<object>(),
            elements = dmn
                ? new object[]
                {
                    new
                    {
                        identity = "model:" + ModelSource.Digest(Encoding.UTF8.GetBytes(path + "#Decision_Approve")),
                        kind = "dmn", @namespace = "https://www.omg.org/spec/DMN/20191111/MODEL/",
                        id = "Decision_Approve", type = "decision", order = 0,
                        attributes = new Dictionary<string, string>(),
                        source = new { artifact = path },
                    },
                }
                : Array.Empty<object>(),
        });
        var sourceEnvelope = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "ce.model-source.v1",
            repositoryId = repo,
            commitSha = commit,
            unitPath = path,
            modelArtifactSha256 = ModelSource.Digest(evidenceBytes),
            sourceSha256 = ModelSource.Digest(sourceBytes),
            sourceFormat,
            sourceBytesBase64 = Convert.ToBase64String(sourceBytes),
        });
        var id = ValidatedModelBundle.StableId("ce.models.v1", repo, commit, path, ModelSource.Digest(evidenceBytes));
        return new(id, "ce.models.v1", repo, commit, path, ModelSource.Digest(evidenceBytes), evidenceBytes, new string('d', 64), "available", sourceEnvelope);
    }
}

/// <summary>Standalone TaskQueries HTTP host over the Postgres-backed evidence source, wiring the same production
/// <see cref="TaskQueryApi.AddTaskQueries"/>/<see cref="TaskQueryApi.MapTaskQueryApi"/> extension methods
/// <c>TaskQueryTestHost</c> (files mode) wires, substituting <see cref="PostgresTaskEvidenceSource"/>'s DI
/// dependencies for <see cref="ExplorerState"/>.</summary>
internal sealed class PostgresTaskQueryHost : IAsyncDisposable
{
    private readonly string _root;
    private readonly WebApplication _app;
    private readonly PostgresFixture _db;

    public HttpClient Client { get; }

    private PostgresTaskQueryHost(string root, WebApplication app, HttpClient client, PostgresFixture db)
    {
        _root = root;
        _app = app;
        Client = client;
        _db = db;
    }

    /// <summary><paramref name="repositories"/> is the capability's own repository membership (what a pinned
    /// snapshot's selection is validated against); <paramref name="grantedRepositories"/> is the separate
    /// operator-level <c>TaskQueries:Repositories</c> grant (defaults to the same list) — kept distinct so a
    /// snapshot can legitimately include a repository the operator still denies at the task-query layer.</summary>
    public static async Task<PostgresTaskQueryHost> CreateAsync(PostgresFixture db, string cap, IReadOnlyList<string> repositories,
        IReadOnlyList<string>? grantedRepositories = null)
    {
        var root = Directory.CreateDirectory(
            Path.Combine(Fixtures.RepositoryRoot, ".sandbox-cache", "postgres-task-query-tests", Guid.NewGuid().ToString("N"))).FullName;
        var capabilitiesPath = Path.Combine(root, "capabilities.json");
        // Repository ids are fixture repository ids (a real checked-out git working copy, needed for the
        // source-excerpt test's cited-file reads) except the synthetic "model-fixture-*" ids the decision
        // reference test invents, which never read source text, so any existing directory location suffices.
        string Location(string id) => (id.StartsWith("model-fixture", StringComparison.Ordinal) ? Fixtures.RepositoryRoot : Fixtures.RepoDirectory(id))
            .Replace("\\", "/", StringComparison.Ordinal);
        var repoEntries = string.Join(',', repositories.Select(id =>
            $$"""{ "id": "{{id}}", "location": "{{Location(id)}}", "kind": "local", "ref": "HEAD" }"""));
        await File.WriteAllTextAsync(capabilitiesPath,
            $$"""{ "capabilities": [ { "id": "{{cap}}", "name": "{{cap}}", "repositories": [ {{repoEntries}} ] } ] }""");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var settings = new Dictionary<string, string?>
        {
            ["capabilities"] = capabilitiesPath,
            ["workspace"] = Path.Combine(root, "workspace"),
            ["source"] = "postgres",
        };
        var granted = grantedRepositories ?? repositories;
        for (var i = 0; i < TaskQueryOperations.All.Count; i++) settings[$"TaskQueries:Operations:{i}"] = TaskQueryOperations.All[i];
        for (var i = 0; i < granted.Count; i++) settings[$"TaskQueries:Repositories:{i}"] = granted[i];
        builder.Configuration.AddInMemoryCollection(settings);

        var options = ExplorerOptions.From(builder.Configuration);
        builder.Services.AddSingleton(options);
        // The shared Testcontainers data source is owned by PostgresFixture, not this host: registering the
        // already-constructed instance (rather than a connection-string factory) means the DI container never
        // disposes it when this WebApplication shuts down between tests.
        builder.Services.AddSingleton(db.DataSource);
        builder.Services.AddSingleton<IExplorerQueries>(new PostgresExplorerQueries(db.DataSource, new ExplorerQueryCursor(new byte[32])));
        builder.Services.AddSingleton<DatabaseExplorerConfiguration>();
        builder.Services.AddTaskQueries("postgres");
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

        var app = builder.Build();
        app.MapTaskQueryApi();
        await app.StartAsync();
        return new PostgresTaskQueryHost(root, app, app.GetTestClient(), db);
    }

    /// <summary>Imports and publishes the real golden fixture jsonl for one commit of forecast-cs under
    /// <paramref name="cap"/>, returning the published snapshot (so callers can pin an older one later).</summary>
    public async Task<AnalysisSnapshot> PublishForecastAsync(string cap, string tag, long expectedGeneration)
    {
        var jsonl = Fixtures.GoldenFiles.First(f => f.StartsWith("forecast-cs/" + tag + "/", StringComparison.Ordinal));
        var imported = await new ArtifactImporter(_db.DataSource).ImportAsync(Fixtures.Golden(jsonl));
        var store = new SnapshotStore(_db.DataSource);
        var snapshot = await store.CreateAsync(cap, "postgres-task-query-" + tag, [imported.ArtifactId]);
        return await store.PublishAsync(snapshot.Id, expectedGeneration);
    }

    /// <summary>Imports and publishes every golden-fixture revision whose repository id is in
    /// <paramref name="repositoryIds"/> under the same snapshot, so a single call can seed a multi-repository
    /// coverage/denial scenario.</summary>
    public async Task<AnalysisSnapshot> PublishAsync(string cap, long expectedGeneration, params string[] repositoryIds)
    {
        var artifactIds = new List<long>();
        foreach (var repositoryId in repositoryIds)
        {
            var file = Fixtures.GoldenFiles.First(f => f.StartsWith(repositoryId + "/", StringComparison.Ordinal));
            var imported = await new ArtifactImporter(_db.DataSource).ImportAsync(Fixtures.Golden(file));
            artifactIds.Add(imported.ArtifactId);
        }

        var store = new SnapshotStore(_db.DataSource);
        var snapshot = await store.CreateAsync(cap, "postgres-task-query-multi", artifactIds);
        return await store.PublishAsync(snapshot.Id, expectedGeneration);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
