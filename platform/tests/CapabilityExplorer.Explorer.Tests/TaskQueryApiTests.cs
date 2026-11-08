using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Contracts.Collection;
using CapabilityExplorer.Explorer;
using CapabilityExplorer.Store;
using Json.Schema;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CapabilityExplorer.Tests;

/// <summary>Standalone TaskQueries HTTP host over the real golden fixture corpus (forecast-cs, pricing-node) plus
/// one synthetic BPMN/DMN model bundle layered on top via <c>CollectionDirectories</c>. This fixture wires the
/// production extension methods over <c>TestServer</c>; real-entry transport proof is in TaskQueryProcessTests.</summary>
internal sealed class TaskQueryTestHost : IAsyncDisposable
{
    // forecast-cs's latest indexed revision is v2-contract (later commit time than v1-base), so this is what
    // ExplorerIndex.DefaultSelection actually picks with no pinned snapshot/cap — the synthetic model bundle and
    // every "current selection" assertion below must claim this commit, not the older v1-base tag.
    public static readonly string ForecastCommit = Fixtures.Lock["forecast-cs"]["v2-contract"];
    public static readonly string PricingCommit = Fixtures.Lock["pricing-node"]["v2-move"];

    private readonly string _root;
    private readonly WebApplication _app;

    public HttpClient Client { get; }
    public string Root => _root;
    public string BpmnModelId { get; }
    public string DmnModelId { get; }
    public JsonSchema EnvelopeSchema { get; }
    public TaskQuerySnapshotInfo? Snapshot { get; private set; }
    public string Pin => $"snapshot={Uri.EscapeDataString(Snapshot!.Id)}&generation={Snapshot.Generation}";

    private TaskQueryTestHost(string root, WebApplication app, HttpClient client, string bpmnModelId, string dmnModelId, JsonSchema envelopeSchema)
    {
        _root = root;
        _app = app;
        Client = client;
        BpmnModelId = bpmnModelId;
        DmnModelId = dmnModelId;
        EnvelopeSchema = envelopeSchema;
    }

    public ExplorerState State => _app.Services.GetRequiredService<ExplorerState>();

    /// <summary>Reused by <c>TaskQueryMcpTests</c> so the MCP stdio round trip exercises the exact same
    /// <see cref="TaskQueryService"/>/fixture this HTTP host does, instead of standing up a second one.</summary>
    public TaskQueryService Service => _app.Services.GetRequiredService<TaskQueryService>();

    /// <summary>Passed to <c>McpServer.Create</c> by the MCP test session; the real host passes its own generic
    /// <c>Host</c>'s <c>Services</c> the same way.</summary>
    public IServiceProvider Services => _app.Services;

    public static async Task<TaskQueryTestHost> CreateAsync(IReadOnlyList<string>? operations = null, IReadOnlyList<string>? repositories = null)
    {
        var root = Directory.CreateDirectory(
            Path.Combine(Fixtures.RepositoryRoot, ".sandbox-cache", "task-query-api-tests", Guid.NewGuid().ToString("N"))).FullName;
        var capabilitiesPath = Path.Combine(root, "capabilities.json");
        string Repo(string id) => Fixtures.RepoDirectory(id).Replace("\\", "/", StringComparison.Ordinal);
        await File.WriteAllTextAsync(capabilitiesPath, $$"""
            { "capabilities": [ { "id": "demand", "name": "Demand", "repositories": [
              { "id": "forecast-cs", "location": "{{Repo("forecast-cs")}}", "kind": "local", "ref": "v1-base" },
              { "id": "pricing-node", "location": "{{Repo("pricing-node")}}", "kind": "local", "ref": "v2-move" } ] } ] }
            """);

        var bundleDir = Path.Combine(root, "bundle");
        await BuildModelBundleAsync(root, bundleDir);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var settings = new Dictionary<string, string?>
        {
            ["artifacts"] = Fixtures.ExpectedDirectory,
            ["repos"] = Path.Combine(Fixtures.RepositoryRoot, "fixtures", ".repos"),
            ["capabilities"] = capabilitiesPath,
            ["workspace"] = Path.Combine(root, "workspace"),
            ["requests"] = Path.Combine(root, "requests"),
            ["collections"] = bundleDir,
            ["source"] = "files",
        };
        for (var i = 0; i < (operations ?? TaskQueryOperations.All).Count; i++) settings[$"TaskQueries:Operations:{i}"] = (operations ?? TaskQueryOperations.All)[i];
        // cap="*" (the default request scope) ignores capabilities.json entirely and selects across every
        // repository ExplorerState has indexed (fixtures/expected/*): forecast-cs, pricing-node and the three
        // other golden fixtures. A grant list narrower than that set leaves repositories denied, which the
        // completeness/status fields would then (correctly) report as "partial" rather than "ok".
        var repos = repositories ?? ["forecast-cs", "pricing-node", "api-cs", "worker-cs", "web-ts"];
        for (var i = 0; i < repos.Count; i++) settings[$"TaskQueries:Repositories:{i}"] = repos[i];
        builder.Configuration.AddInMemoryCollection(settings);

        var options = ExplorerOptions.From(builder.Configuration);
        builder.Services.AddSingleton(options);
        var state = new ExplorerState(options);
        builder.Services.AddSingleton(state);
        builder.Services.AddHostedService(_ => state);
        builder.Services.AddSingleton<ITaskEvidenceSource, FileTaskEvidenceSource>();
        builder.Services.AddTaskQueries("files");
        // Same baseline the real Program.cs configures: ASP.NET's default HttpJsonOptions already starts from
        // JsonSerializerDefaults.Web (camelCase); only the null-suppression and enum converter are added here.
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

        var app = builder.Build();
        app.MapTaskQueryApi();
        await app.StartAsync();
        var client = app.GetTestClient();

        // Resolve the two synthetic model ids directly from the indexed products (not by predicting their
        // content-hash ids up front): the collection pipeline re-serializes/canonicalizes artifact bytes, so an
        // id computed over this test's own pre-export JSON would not match the one the real pipeline assigns.
        var synthetic = state.Current.Models.Products
            .Where(p => p.RepositoryId == "forecast-cs" && p.CommitSha == ForecastCommit).ToList();
        var bpmnId = synthetic.Single(p => ModelSummary.From(p).ModelKind == "bpmn").Id;
        var dmnId = synthetic.Single(p => ModelSummary.From(p).ModelKind == "dmn").Id;

        var envelopeSchema = LoadEnvelopeSchema();
        var host = new TaskQueryTestHost(root, app, client, bpmnId, dmnId, envelopeSchema);
        host.Snapshot = (await host.Service.SnapshotResolveAsync(new TaskQueryRequest(), CancellationToken.None)).Snapshot;
        return host;
    }

    /// <summary>Builds one authored BPMN model whose source XML carries three decision-link attributes (available,
    /// missing, external) and one authored DMN model it can resolve to, both claiming the real forecast-cs v1-base
    /// repository/commit so the synthetic bundle layers onto (never replaces) the real golden corpus selection.</summary>
    private static async Task BuildModelBundleAsync(string root, string bundleDir)
    {
        const string bpmnXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:cex="urn:capability-explorer:model-links:v1" id="Definitions_1">
              <process id="Process_1">
                <businessRuleTask id="Task_Available" name="Approve" cex:decisionRef="pricing.dmn#Decision_Approve"/>
                <businessRuleTask id="Task_Missing" name="Check" cex:decisionRef="other.dmn#Decision_Other"/>
                <businessRuleTask id="Task_External" name="Bad" cex:decisionRef="../escape.dmn#Decision_Bad"/>
              </process>
            </definitions>
            """;
        const string dmnXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="https://www.omg.org/spec/DMN/20191111/MODEL/" id="Definitions_DMN" name="pricing">
              <decision id="Decision_Approve" name="Approve"/>
            </definitions>
            """;

        var inputs = new List<CollectionInputPlan>();
        await AddAsync("models/process.bpmn", bpmnXml, "bpmn+xml", "process", []);
        await AddAsync("models/pricing.dmn", dmnXml, "dmn+xml", "pricing", [new
        {
            identity = "model:" + new string('f', 64),
            kind = "dmn",
            @namespace = "urn:capability-explorer:model-links:v1",
            id = "Decision_Approve",
            type = "decision",
            order = 0,
            attributes = new { },
            source = new { artifact = "pricing.dmn" },
        }]);

        var plan = new CollectionPlan("ce.collection.plan.v1", new string('a', 64), new string('b', 64), new string('c', 64),
            [new("forecast-cs", "provider", "producer", "tenant", "rights", TaskQueryTestHost.ForecastCommit, new string('e', 40), ["model-content"])], inputs);
        var planPath = Path.Combine(root, "plan.json");
        await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan, JsonSerializerOptions.Web));
        var result = await CollectionExporter.ExportAsync(planPath, bundleDir);
        if (!result.IsValid) throw new InvalidOperationException("synthetic model bundle invalid: " + string.Join("; ", result.Problems));

        async Task AddAsync(string unit, string xml, string format, string name, object[] elements)
        {
            var xmlBytes = Encoding.UTF8.GetBytes(xml);
            var modelBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = "ce.models.v1",
                repositoryId = "forecast-cs",
                commitSha = ForecastCommit,
                unitPath = unit,
                sourceSha256 = ModelSource.Digest(xmlBytes),
                contentProfile = "model-content",
                coverage = new { status = "full", supported = Array.Empty<string>(), limitations = Array.Empty<string>() },
                diagnostics = Array.Empty<object>(),
                elements,
                relationships = Array.Empty<object>(),
            });
            var modelPath = $"{name}.json";
            await File.WriteAllBytesAsync(Path.Combine(root, modelPath), modelBytes);
            inputs.Add(new("forecast-cs", "ce.models.v1", modelPath, unit, ModelSource.Digest(modelBytes), modelBytes.Length, true, "model-content"));

            var sourceBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = "ce.model-source.v1",
                repositoryId = "forecast-cs",
                commitSha = ForecastCommit,
                unitPath = unit,
                modelArtifactSha256 = ModelSource.Digest(modelBytes),
                sourceSha256 = ModelSource.Digest(xmlBytes),
                sourceFormat = format,
                sourceBytesBase64 = Convert.ToBase64String(xmlBytes),
            });
            var sourcePath = $"{name}-source.json";
            await File.WriteAllBytesAsync(Path.Combine(root, sourcePath), sourceBytes);
            inputs.Add(new("forecast-cs", "ce.model-source.v1", sourcePath, unit, ModelSource.Digest(sourceBytes), sourceBytes.Length, true, "model-content"));
        }
    }

    /// <summary>Rewrites <c>#/components/schemas/X</c> pointers into a self-contained <c>$defs</c> document so
    /// JsonSchema.Net can resolve <c>TaskQueryEnvelope</c>'s internal references without a registered base URI.</summary>
    /// <summary>Also reused by <c>TaskQueryMcpTests</c> to validate MCP tool-call envelopes against the same
    /// schema the HTTP front end is checked against.</summary>
    internal static JsonSchema LoadEnvelopeSchema()
    {
        var path = Path.Combine(Fixtures.RepositoryRoot, "schema", "openapi", "task-queries.openapi.json");
        var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var schemas = document["components"]!["schemas"]!.AsObject();
        var rewritten = JsonNode.Parse(schemas.ToJsonString())!;
        Rewrite(rewritten);
        var wrapper = new JsonObject { ["$defs"] = rewritten, ["$ref"] = "#/$defs/TaskQueryEnvelope" };
        return JsonSchema.FromText(wrapper.ToJsonString());

        static void Rewrite(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    if (obj.TryGetPropertyValue("$ref", out var refValue) && refValue is JsonValue v && v.TryGetValue(out string? s)
                        && s!.StartsWith("#/components/schemas/", StringComparison.Ordinal))
                    {
                        obj["$ref"] = "#/$defs/" + s["#/components/schemas/".Length..];
                    }

                    foreach (var child in obj.ToList()) Rewrite(child.Value);
                    break;
                case JsonArray arr:
                    foreach (var child in arr) Rewrite(child);
                    break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

/// <summary>A minimal synthetic <see cref="ITaskEvidenceSource"/> used only to exercise
/// <see cref="TaskQueryService.ContractConsumersAsync"/> directly against a constructed <see cref="HttpLink"/>
/// with a genuinely resolved candidate - the real golden fixture corpus happens to resolve zero HttpLink
/// candidates for its indexed revisions, so it cannot alone prove the consumer identity/direction is correct.</summary>
internal sealed class SyntheticIntegrationSource(HttpLink? link = null, bool truncated = false, bool failResolve = false, bool failModels = false) : ITaskEvidenceSource
{
    public string Mode => "files";
    public int ContentReads { get; private set; }
    public int ResolveReads { get; private set; }
    private static readonly TaskSnapshotScope Scope = new("synthetic", 1, new Dictionary<string, string> { ["svc-a"] = new string('1', 40) }, true, null, new object());

    public Task<TaskSnapshotScope?> ResolveAsync(string cap, IReadOnlyDictionary<string, string> specifiedView, string? snapshotId, long? generation, CancellationToken ct)
    {
        ResolveReads++;
        return failResolve ? throw new IOException("private-storage-path") : Task.FromResult<TaskSnapshotScope?>(Scope);
    }
    public TaskSnapshotScope Narrow(TaskSnapshotScope scope, IReadOnlyDictionary<string, string> allowed) => scope;
    public Task<TaskModelPage> ModelsAsync(TaskSnapshotScope scope, string? kind, CancellationToken ct) =>
        failModels ? throw new IOException("private-storage-path") : Task.FromResult(new TaskModelPage([], truncated));
    public Task<ModelProductHead?> ModelHeadAsync(TaskSnapshotScope scope, string modelId, CancellationToken ct) =>
        Task.FromResult<ModelProductHead?>(new(modelId, "ce.models.v1", "svc-a", new string('1', 40),
            "models/process.bpmn", "bpmn", "full", 0, "available", new string('a', 64)));
    public Task<ModelProduct?> ModelAsync(TaskSnapshotScope scope, string modelId, CancellationToken ct)
    {
        ContentReads++;
        return Task.FromResult<ModelProduct?>(null);
    }
    public Task<Integrations> IntegrationsAsync(TaskSnapshotScope scope, CancellationToken ct) =>
        Task.FromResult(new Integrations([], link is null ? [] : [link], [], [], []));
    public Task<TaskEvidencePage> EvidenceAsync(TaskSnapshotScope scope, string key, CancellationToken ct) => Task.FromResult(new TaskEvidencePage([], truncated));
    public Task<bool> IsCitedAsync(TaskSnapshotScope scope, string repositoryId, string commitSha, string path, CancellationToken ct) => Task.FromResult(false);
    public Task<string?> ReadCitedFileAsync(TaskSnapshotScope scope, string repositoryId, string commitSha, string path, CancellationToken ct) => Task.FromResult<string?>(null);
}

public sealed partial class TaskQueryApiTests
{
    /// <summary>Regression for spec rev4's consumer direction: constructs one synthetic <see cref="HttpLink"/>
    /// with its own non-trivial <c>Request.From</c> (the caller, "svc-a/callForecast") distinct from the resolved
    /// <c>Candidates</c> entry (the queried provider endpoint, "svc-b/getForecast"). The OLD (pre-fix) direction -
    /// matching on <c>Request.To</c>/<c>Request.From</c> as the provider key - would resolve zero rows for this
    /// fixture (there is no "svc-b/getForecast"-keyed edge at all, only a candidate), so this fixture fails under
    /// the old implementation and only passes once consumers are correctly derived from <c>Candidates</c>.</summary>
    [Theory]
    [InlineData("route", "declared", "not-established")]
    [InlineData("Endpoint", "binding", "evidenced")]
    public async Task Contract_consumers_resolves_the_correct_caller_identity_from_a_genuinely_matched_candidate(
        string kind, string match, string binding)
    {
        var from = new SymbolRef("svc-a/callForecast", "callForecast", "method", true, "svc-a");
        var request = new EdgeDto("calls", from, null, "static", "unresolved", null, null, null, "svc-a", new string('1', 40), []);
        var candidate = new SymbolRef("svc-b/getForecast", "getForecast", kind, true, "svc-b");
        var link = new HttpLink(request, [candidate], match, "note", SourcePath: "src/Caller.cs");
        var service = new TaskQueryService(new SyntheticIntegrationSource(link),
            new TaskQueryAccessPolicy(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskQueries:Operations:0"] = TaskQueryOperations.ContractConsumers,
                ["TaskQueries:Repositories:0"] = "svc-a",
            }).Build()),
            new TaskQueryCursorCodec(), NullLogger<TaskQueryService>.Instance);

        var matched = await service.ContractConsumersAsync(new TaskQueryRequest(SnapshotId: "synthetic", Generation: 1, Key: "svc-b/getForecast"), CancellationToken.None);
        Assert.Equal("ok", matched.Status);
        var item = Assert.Single((IEnumerable<TaskQueryConsumerDto>)matched.Data!);
        Assert.Equal("svc-a/callForecast", item.Consumer.Key);
        Assert.Equal("callForecast", item.Consumer.Name);
        Assert.Equal("svc-a", item.Consumer.RepositoryId);
        Assert.Equal("symbol", item.Consumer.Kind);
        Assert.Equal(binding, item.Connection.ConfiguredBinding);
        var serialized = JsonSerializer.SerializeToElement(matched, TaskQueryJson.Options);
        Assert.True(TaskQueryTestHost.LoadEnvelopeSchema().Evaluate(serialized).IsValid);

        // Querying by the consumer's own key (the old, backwards direction) must resolve nothing: it is not a
        // provider endpoint any HttpLink's Candidates resolve to.
        var reversed = await service.ContractConsumersAsync(new TaskQueryRequest(SnapshotId: "synthetic", Generation: 1, Key: "svc-a/callForecast"), CancellationToken.None);
        Assert.Equal("ok", reversed.Status);
        Assert.Empty((IEnumerable<TaskQueryConsumerDto>)reversed.Data!);
    }


    [Fact]
    public async Task Access_is_denied_by_default_with_no_snapshot_when_task_queries_are_not_configured()
    {
        await using var host = await TaskQueryTestHost.CreateAsync(operations: [], repositories: []);
        using var response = await host.Client.GetAsync(new Uri("/api/tasks/v1/snapshot", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("denied", body.GetProperty("status").GetString());
        Assert.Equal("task-queries-not-configured", body.GetProperty("reason").GetString());
        Assert.False(body.TryGetProperty("snapshot", out _));
    }

    [Fact]
    public async Task Snapshot_resolve_defaults_to_every_granted_repository_and_rejects_an_unknown_capability()
    {
        await using var host = await TaskQueryTestHost.CreateAsync();
        var ok = await host.Client.GetFromJsonAsync<JsonElement>("/api/tasks/v1/snapshot");
        Assert.Equal("ok", ok.GetProperty("status").GetString());
        var selection = ok.GetProperty("snapshot").GetProperty("selection").EnumerateArray()
            .ToDictionary(s => s.GetProperty("repo").GetString()!, s => s.GetProperty("sha").GetString());
        Assert.Equal(TaskQueryTestHost.ForecastCommit, selection["forecast-cs"]);
        Assert.Equal(TaskQueryTestHost.PricingCommit, selection["pricing-node"]);
        Assert.Equal("files", ok.GetProperty("mode").GetString());
        Assert.True(host.EnvelopeSchema.Evaluate(ok, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid);

        using var bad = await host.Client.GetAsync(new Uri("/api/tasks/v1/snapshot?cap=not-a-real-capability", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var badBody = await bad.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unknown-capability", badBody.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task An_operator_denied_repository_is_reported_as_partial_coverage_not_as_a_denied_snapshot()
    {
        await using var host = await TaskQueryTestHost.CreateAsync(repositories: ["forecast-cs"]);
        var body = await host.Client.GetFromJsonAsync<JsonElement>("/api/tasks/v1/snapshot");
        Assert.Equal("partial", body.GetProperty("status").GetString());
        var repos = body.GetProperty("coverage").GetProperty("repositories").EnumerateArray()
            .ToDictionary(r => r.GetProperty("repositoryId").GetString()!, r => r.GetProperty("status").GetString());
        Assert.Equal("partial", repos["forecast-cs"]);
        Assert.Equal("denied", repos["pricing-node"]);
        Assert.Equal("not-established", body.GetProperty("coverage").GetProperty("completeness").GetString());
    }

    [Fact]
    public async Task A_pinned_snapshot_that_is_not_the_current_one_is_stale_in_files_mode()
    {
        await using var host = await TaskQueryTestHost.CreateAsync();
        var current = (await host.Client.GetFromJsonAsync<JsonElement>("/api/tasks/v1/snapshot")).GetProperty("snapshot").GetProperty("id").GetString();
        using var response = await host.Client.GetAsync(new Uri($"/api/tasks/v1/snapshot?snapshot=not-{current}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("stale", body.GetProperty("status").GetString());
        Assert.Equal("historical-not-retained", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Models_list_pages_and_rejects_a_forged_cursor()
    {
        await using var host = await TaskQueryTestHost.CreateAsync();
        var first = await host.Client.GetFromJsonAsync<JsonElement>($"/api/tasks/v1/models?{host.Pin}&limit=1");
        Assert.Equal("ok", first.GetProperty("status").GetString());
        var items = first.GetProperty("data").EnumerateArray().ToList();
        var firstId = Assert.Single(items).GetProperty("id").GetString();
        var cursor = first.GetProperty("page").GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));

        var second = await host.Client.GetFromJsonAsync<JsonElement>($"/api/tasks/v1/models?{host.Pin}&limit=1&cursor={Uri.EscapeDataString(cursor!)}");
        var secondId = Assert.Single(second.GetProperty("data").EnumerateArray()).GetProperty("id").GetString();
        Assert.NotEqual(firstId, secondId);
        // The real pipeline's content-hash ids have no fixed relative ordering, so compare both sides sorted
        // (asserting the paged set equals {BpmnModelId, DmnModelId}, not any particular hash-value ordering).
        Assert.Equal(
            new[] { host.BpmnModelId, host.DmnModelId }.Order(StringComparer.Ordinal),
            new[] { firstId, secondId }.Order(StringComparer.Ordinal));

        using var forged = await host.Client.GetAsync(new Uri($"/api/tasks/v1/models?{host.Pin}&limit=1&cursor=not-a-real-cursor", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal("invalid", (await forged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Decision_references_resolve_available_and_missing_targets_and_reject_a_short_model_id()
    {
        await using var host = await TaskQueryTestHost.CreateAsync();
        var page = await host.Client.GetFromJsonAsync<JsonElement>($"/api/tasks/v1/models/decision-references?{host.Pin}&id={Uri.EscapeDataString(host.BpmnModelId)}");
        Assert.Equal("partial", page.GetProperty("status").GetString()); // "missing" item makes this partial, not an error
        var byElement = page.GetProperty("data").EnumerateArray().ToDictionary(d => d.GetProperty("elementId").GetString()!);
        Assert.Equal("available", byElement["Task_Available"].GetProperty("status").GetString());
        Assert.Equal(host.DmnModelId, byElement["Task_Available"].GetProperty("target").GetProperty("modelId").GetString());
        Assert.Equal("Decision_Approve", byElement["Task_Available"].GetProperty("target").GetProperty("decisionId").GetString());
        Assert.Equal("missing", byElement["Task_Missing"].GetProperty("status").GetString());
        // "target" is null for a "missing" decision link, and the production serializer (WhenWritingNull) omits
        // null-valued properties entirely rather than emitting "target":null, so assert key-absence, not a value.
        Assert.False(byElement["Task_Missing"].TryGetProperty("target", out _));
        Assert.Equal("external", byElement["Task_External"].GetProperty("status").GetString());
        Assert.All(page.GetProperty("data").EnumerateArray(), d => Assert.False(d.GetProperty("reviewed").GetBoolean()));

        using var invalid = await host.Client.GetAsync(new Uri($"/api/tasks/v1/models/decision-references?{host.Pin}&id=too-short", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("invalid-model-id", (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Contract_consumers_reports_a_real_golden_fixture_http_link_as_an_honest_unverified_candidate()
    {
        await using var host = await TaskQueryTestHost.CreateAsync();
        var view = new ViewIndex(host.State.Current.Index,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["forecast-cs"] = TaskQueryTestHost.ForecastCommit, ["pricing-node"] = TaskQueryTestHost.PricingCommit });
        var integrations = Queries.Integrations(view);
        var withCandidate = integrations.Http.FirstOrDefault(link => link.Candidates.Count > 0);
        // This golden fixture's HttpLink set resolves no candidates at all for the selected revisions: the
        // corrected (spec rev4) direction - filter on Candidates containing the queried provider endpoint - must
        // therefore report an empty, still-"ok" result, never the old backwards from/to match it used to report.
        Assert.Null(withCandidate);
        var anyLink = Assert.Single(integrations.Http, link => link.Request.From.Key.Contains("getForecast", StringComparison.Ordinal));

        var body = await host.Client.GetFromJsonAsync<JsonElement>($"/api/tasks/v1/contracts/consumers?{host.Pin}&endpoint={Uri.EscapeDataString(anyLink.Request.From.Key)}");
        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.Empty(body.GetProperty("data").EnumerateArray());

        // Querying by the (non-existent-as-provider) candidate-shaped key the old code used to accept must also
        // stay empty, not resurrect a false match via the old Request.To/From direction.
        var reversed = await host.Client.GetFromJsonAsync<JsonElement>($"/api/tasks/v1/contracts/consumers?{host.Pin}&endpoint={Uri.EscapeDataString(anyLink.Request.Relation)}");
        Assert.Empty(reversed.GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task Evidence_list_returns_the_real_golden_fixture_evidence_for_a_declared_symbol()
    {
        await using var host = await TaskQueryTestHost.CreateAsync();
        // v2-contract (the default-selected commit) renamed this route to /api/v2/forecasts/{} — v1-base's
        // "/api/forecasts/{}" symbol key no longer exists in the default selection's indexed golden fixture.
        const string key = "http|forecast-cs/Forecast.Api|GET /api/v2/forecasts/{}";
        var body = await host.Client.GetFromJsonAsync<JsonElement>($"/api/tasks/v1/evidence?{host.Pin}&key={Uri.EscapeDataString(key)}");
        Assert.Equal("ok", body.GetProperty("status").GetString());
        var items = body.GetProperty("data").EnumerateArray().ToList();
        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.Equal("forecast-cs", i.GetProperty("repositoryId").GetString()));
        Assert.All(items, i => Assert.Equal(TaskQueryTestHost.ForecastCommit, i.GetProperty("commitSha").GetString()));
    }

    [Fact]
    public async Task Source_excerpt_serves_only_cited_files_within_the_pinned_commit_and_bounds_its_span()
    {
        await using var host = await TaskQueryTestHost.CreateAsync();
        const string path = "src/Forecast.Api/Forecast.Api.csproj";
        var body = await host.Client.GetFromJsonAsync<JsonElement>(
            $"/api/tasks/v1/source?{host.Pin}&repo=forecast-cs&commit={TaskQueryTestHost.ForecastCommit}&path={Uri.EscapeDataString(path)}&start=1&end=3");
        Assert.Equal("ok", body.GetProperty("status").GetString());
        var excerpt = body.GetProperty("data");
        Assert.Equal(path, excerpt.GetProperty("path").GetString());
        Assert.NotEmpty(excerpt.GetProperty("lines").EnumerateArray());
        Assert.True(host.EnvelopeSchema.Evaluate(body, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid);

        using var uncited = await host.Client.GetAsync(new Uri(
            $"/api/tasks/v1/source?{host.Pin}&repo=forecast-cs&commit={TaskQueryTestHost.ForecastCommit}&path=not/a/cited/file.cs&start=1&end=1", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, uncited.StatusCode);
        Assert.Equal("path-not-cited", (await uncited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").GetString());

        using var oversized = await host.Client.GetAsync(new Uri(
            $"/api/tasks/v1/source?{host.Pin}&repo=forecast-cs&commit={TaskQueryTestHost.ForecastCommit}&path={Uri.EscapeDataString(path)}&start=1&end=250", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        Assert.Equal("invalid-excerpt-request", (await oversized.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").GetString());
    }
}
