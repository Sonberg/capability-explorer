using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CapabilityExplorer.Binding;
using CapabilityExplorer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class ArchitectureConnectionsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ce-graph-connections-").FullName
        .Replace("/var/", "/private/var/", StringComparison.Ordinal);
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    [Fact]
    public async Task Given_an_old_matching_algorithm_when_reading_cached_results_then_the_catalogue_is_unavailable_not_reused()
    {
        var source = ArtifactValidator.Validate(Fixtures.Golden("api-cs/v1-base/Sales.Api.jsonl")).Artifact!;
        var from = new BindingPreviewSource(source.Header.RepositoryId, source.Header.CommitSha, source.Header.Unit.Path, source.ContentSha256);
        var match = new IntegrationMatch("old-match", "first", "second", "source-run", "target-run", from, from,
            "old-edge", source.Nodes[0].SymbolKey, source.Nodes[0].SymbolKey, "REQUESTS", "http", "matched",
            "An old algorithm's conclusion.", [], []);
        var store = new IntegrationMatchCatalogueStore(_root);
        using (store.AcquireWriter()) store.Write(new("integration-cluster-v1", new string('a', 64), DateTimeOffset.UtcNow,
            [new("first", "source-run", from)], 1, 0, [match]));
        var path = Path.Combine(_root, ".sandbox-cache", "integration-matches.json");
        var before = File.ReadAllBytes(path);
        using var response = await _client.GetAsync("/api/architecture/integration-matches");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var graph = await _client.GetFromJsonAsync<JsonElement>("/api/architecture?level=landscape");
        Assert.DoesNotContain(graph.GetProperty("edges").EnumerateArray(),
            edge => edge.GetProperty("id").GetString() == "automatic:old-match");
        Assert.Contains(graph.GetProperty("notes").EnumerateArray(),
            note => note.GetString()!.Contains("unavailable", StringComparison.Ordinal));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Given_shared_target_clusters_when_viewing_one_capability_then_only_its_incoming_and_outgoing_connections_are_shown()
    {
        var source = ArtifactValidator.Validate(Fixtures.Golden("api-cs/v1-base/Sales.Api.jsonl")).Artifact!;
        var target = ArtifactValidator.Validate(Fixtures.Golden("forecast-cs/v1-base/Forecast.Api.jsonl")).Artifact!;
        var other = ArtifactValidator.Validate(Fixtures.Golden("pricing-node/v1-base/_fixture_pricing.jsonl")).Artifact!;
        var from = new BindingPreviewSource(source.Header.RepositoryId, source.Header.CommitSha, source.Header.Unit.Path, source.ContentSha256);
        var to = new BindingPreviewSource(target.Header.RepositoryId, target.Header.CommitSha, target.Header.Unit.Path, target.ContentSha256);
        var unrelated = new BindingPreviewSource(other.Header.RepositoryId, other.Header.CommitSha, other.Header.Unit.Path, other.ContentSha256);
        var match = new IntegrationMatch("outgoing", "first", "second", "source-run", "target-run", from, to,
            "observed-one", source.Nodes[0].SymbolKey, target.Nodes[0].SymbolKey, "REQUESTS", "http", "inferred",
            "Caller cluster with unproven deployment identity.", [], [], ClusterId: "outgoing-cluster");
        var store = new IntegrationMatchCatalogueStore(_root);
        using (store.AcquireWriter()) store.Write(new(IntegrationMatching.AlgorithmVersion, new string('c', 64), DateTimeOffset.UtcNow,
            [new("first", "source-run", from), new("second", "target-run", to), new("third", "other-run", unrelated)], 3, 3,
            [match,
             match with { Id = "unrelated", SourceCapabilityId = "third", SourceRunId = "other-run", Source = unrelated,
                 SourceKey = other.Nodes[0].SymbolKey, ClusterId = "unrelated-cluster" },
             match with { Id = "incoming", SourceCapabilityId = "third", SourceRunId = "other-run", Source = unrelated,
                 SourceKey = other.Nodes[0].SymbolKey, TargetCapabilityId = "first", TargetRunId = "source-run", Target = from,
                 TargetKey = source.Nodes[0].SymbolKey, ClusterId = "incoming-cluster" }]));
        var view = $"api-cs:{source.Header.CommitSha},forecast-cs:{target.Header.CommitSha},pricing-node:{other.Header.CommitSha}";
        var graph = await _client.GetFromJsonAsync<JsonElement>("/api/architecture?level=system&cap=first&view=" + view);
        var edges = graph.GetProperty("edges").EnumerateArray().Where(edge => edge.GetProperty("id").GetString()!.StartsWith("automatic:", StringComparison.Ordinal)).ToList();
        Assert.Contains(edges, edge => edge.GetProperty("id").GetString() == "automatic:outgoing-cluster:second");
        Assert.Contains(edges, edge => edge.GetProperty("id").GetString() == "automatic:incoming-cluster:first");
        Assert.DoesNotContain(edges, edge => edge.GetProperty("id").GetString() == "automatic:unrelated-cluster:second");
    }

    [Fact]
    public async Task Given_an_inferred_cluster_when_projecting_then_one_candidate_edge_keeps_all_facts_and_contract_warnings()
    {
        var source = ArtifactValidator.Validate(Fixtures.Golden("api-cs/v1-base/Sales.Api.jsonl")).Artifact!;
        var target = ArtifactValidator.Validate(Fixtures.Golden("forecast-cs/v1-base/Forecast.Api.jsonl")).Artifact!;
        var from = new BindingPreviewSource(source.Header.RepositoryId, source.Header.CommitSha, source.Header.Unit.Path, source.ContentSha256);
        var to = new BindingPreviewSource(target.Header.RepositoryId, target.Header.CommitSha, target.Header.Unit.Path, target.ContentSha256);
        var contract = new IntegrationContractAssessment("incompatible", 2, 0, ["HTTP method differs: PUT versus PATCH."], ["Payload schema unavailable."]);
        var match = new IntegrationMatch("cluster-call-one", "first", "second", "source-run", "target-run", from, to,
            "observed-one", source.Nodes[0].SymbolKey, target.Nodes[0].SymbolKey, "REQUESTS", "http", "inferred",
            "Clustered caller observations; contract differs.", [], [], ClusterId: "cluster-test", Contract: contract);
        var store = new IntegrationMatchCatalogueStore(_root);
        using (store.AcquireWriter()) store.Write(new(IntegrationMatching.AlgorithmVersion, new string('b', 64), DateTimeOffset.UtcNow,
            [new("first", "source-run", from), new("second", "target-run", to)], 2, 2,
            [match, match with { Id = "cluster-call-two", EdgeId = "observed-two" }]));
        var view = $"api-cs:{source.Header.CommitSha},forecast-cs:{target.Header.CommitSha}";
        var graph = await _client.GetFromJsonAsync<JsonElement>("/api/architecture?level=landscape&view=" + view);
        var edges = graph.GetProperty("edges").EnumerateArray().Where(edge => edge.GetProperty("id").GetString()!.StartsWith("automatic:", StringComparison.Ordinal)).ToList();
        var edge = Assert.Single(edges);
        Assert.Equal("candidate", edge.GetProperty("certainty").GetString());
        Assert.Equal(2, edge.GetProperty("count").GetInt32());
        Assert.Equal(2, edge.GetProperty("facts").GetArrayLength());
        Assert.Contains("incompatible", edge.GetProperty("note").GetString(), StringComparison.Ordinal);
        Assert.All(edge.GetProperty("facts").EnumerateArray(), fact => Assert.Equal("candidate", fact.GetProperty("resolution").GetString()));
        Assert.False(File.Exists(Path.Combine(_root, ".sandbox-cache", "architecture-connections.json")));
    }

    [Fact]
    public async Task Given_automatic_results_when_inspecting_then_only_selected_revisions_connect_and_suggestions_remain_separate()
    {
        var source = ArtifactValidator.Validate(Fixtures.Golden("api-cs/v1-base/Sales.Api.jsonl")).Artifact!;
        var target = ArtifactValidator.Validate(Fixtures.Golden("forecast-cs/v1-base/Forecast.Api.jsonl")).Artifact!;
        var from = new BindingPreviewSource(source.Header.RepositoryId, source.Header.CommitSha, source.Header.Unit.Path, source.ContentSha256);
        var to = new BindingPreviewSource(target.Header.RepositoryId, target.Header.CommitSha, target.Header.Unit.Path, target.ContentSha256);
        var match = new IntegrationMatch("verified", "first", "second", "source-run", "target-run", from, to,
            "observed-edge", source.Nodes[0].SymbolKey, target.Nodes[0].SymbolKey, "REQUESTS", "http", "matched",
            "Exact declared endpoint identity.", [], []);
        var store = new IntegrationMatchCatalogueStore(_root);
        using (store.AcquireWriter()) store.Write(new(IntegrationMatching.AlgorithmVersion, new string('a', 64), DateTimeOffset.UtcNow,
            [new("first", "source-run", from), new("second", "target-run", to)], 2, 1,
            [match, match with { Id = "uncertain", Status = "suggested" }]));
        var view = $"api-cs:{source.Header.CommitSha},forecast-cs:{target.Header.CommitSha}";
        using var response = await _client.GetAsync("/api/architecture/integration-matches?view=" + view);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, result.GetProperty("matched").GetInt32());
        Assert.Equal(1, result.GetProperty("suggested").GetInt32());
        var graph = await _client.GetFromJsonAsync<JsonElement>("/api/architecture?level=landscape&view=" + view);
        Assert.Single(graph.GetProperty("edges").EnumerateArray(), edge => edge.GetProperty("id").GetString() == "automatic:verified");
        Assert.DoesNotContain(graph.GetProperty("edges").EnumerateArray(), edge => edge.GetProperty("id").GetString() == "automatic:uncertain");
        var changed = ArtifactValidator.Validate(Fixtures.Golden("forecast-cs/v2-contract/Forecast.Api.jsonl")).Artifact!;
        var historical = await _client.GetFromJsonAsync<JsonElement>("/api/architecture/integration-matches?view=" +
            $"api-cs:{source.Header.CommitSha},forecast-cs:{changed.Header.CommitSha}");
        Assert.Equal(0, historical.GetProperty("matched").GetInt32());
        Assert.False(File.Exists(Path.Combine(_root, ".sandbox-cache", "architecture-connections.json")));
    }

    public ArchitectureConnectionsTests()
    {
        var capabilities = Path.Combine(_root, "capabilities.json");
        File.WriteAllText(capabilities, JsonSerializer.Serialize(new
        {
            capabilities = new[]
            {
                new { id = "first", name = "First", repositories = new[] { new { id = "api-cs", kind = "local", location = Fixtures.RepoDirectory("api-cs"), @ref = "HEAD" } } },
                new { id = "second", name = "Second", repositories = new[] { new { id = "forecast-cs", kind = "local", location = Fixtures.RepoDirectory("forecast-cs"), @ref = "HEAD" } } },
                new { id = "third", name = "Third", repositories = new[] { new { id = "pricing-node", kind = "local", location = Fixtures.RepoDirectory("pricing-node"), @ref = "HEAD" } } },
            },
        }));
        _factory = new ExplorerFactory().WithWebHostBuilder(builder => builder
            .UseSetting("capabilities", capabilities).UseSetting("workspace", Path.Combine(_root, "sources"))
            .UseSetting("requests", Path.Combine(_root, "requests")));
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-CE-Architecture-Connections", "1");
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Given_capabilities_when_connected_then_the_manual_link_persists_and_can_be_removed()
    {
        var initial = await _client.GetAsync("/api/architecture/connections");
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        using var created = await _client.PostAsJsonAsync("/api/architecture/connections", new
        {
            fromCapabilityId = "first", toCapabilityId = "second", kind = "navigation",
            note = "Reviewed browser navigation", expectedVersion = 0,
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var saved = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, saved.GetProperty("version").GetInt64());
        var connection = Assert.Single(saved.GetProperty("connections").EnumerateArray());
        var id = connection.GetProperty("id").GetString();
        var graph = await _client.GetFromJsonAsync<JsonElement>("/api/architecture?level=landscape");
        var edge = Assert.Single(graph.GetProperty("edges").EnumerateArray(), edge => edge.GetProperty("id").GetString() == "manual:" + id);
        Assert.Equal("cap:first", edge.GetProperty("from").GetString());
        Assert.Equal("cap:second", edge.GetProperty("to").GetString());
        Assert.Equal("human", edge.GetProperty("certainty").GetString());
        Assert.Equal("navigation", edge.GetProperty("kind").GetString());
        using var secondClient = _factory.CreateClient();
        var reloaded = await secondClient.GetFromJsonAsync<JsonElement>("/api/architecture/connections");
        Assert.Single(reloaded.GetProperty("connections").EnumerateArray());

        using var removal = new HttpRequestMessage(HttpMethod.Delete, $"/api/architecture/connections/{id}?expectedVersion=1")
        {
            Content = JsonContent.Create(new { }),
        };
        using var removed = await _client.SendAsync(removal);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        var after = await _client.GetFromJsonAsync<JsonElement>("/api/architecture?level=landscape");
        Assert.DoesNotContain(after.GetProperty("edges").EnumerateArray(), edge => edge.GetProperty("id").GetString() == "manual:" + id);
    }

    [Fact]
    public async Task Given_manual_connections_when_writes_are_stale_invalid_or_cross_origin_then_they_are_rejected()
    {
        using var invalid = await _client.PostAsJsonAsync("/api/architecture/connections", new
        {
            fromCapabilityId = "first", toCapabilityId = "missing", kind = "http", expectedVersion = 0,
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var created = await _client.PostAsJsonAsync("/api/architecture/connections", new
        {
            fromCapabilityId = "first", toCapabilityId = "second", kind = "http", expectedVersion = 0,
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using var stale = await _client.PostAsJsonAsync("/api/architecture/connections", new
        {
            fromCapabilityId = "second", toCapabilityId = "first", kind = "message", expectedVersion = 0,
        });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var crossOrigin = new HttpRequestMessage(HttpMethod.Post, "/api/architecture/connections")
        {
            Content = JsonContent.Create(new { fromCapabilityId = "second", toCapabilityId = "first", kind = "http", expectedVersion = 1 }),
        };
        crossOrigin.Headers.Add("Origin", "https://untrusted.example");
        using var denied = await _client.SendAsync(crossOrigin);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Given_a_saved_connection_when_updated_and_the_host_restarts_then_the_edit_is_preserved()
    {
        using var created = await _client.PostAsJsonAsync("/api/architecture/connections", new
        {
            fromCapabilityId = "first", toCapabilityId = "second", kind = "http", expectedVersion = 0,
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var saved = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = Assert.Single(saved.GetProperty("connections").EnumerateArray()).GetProperty("id").GetString();
        using var updated = await _client.PutAsJsonAsync($"/api/architecture/connections/{id}", new
        {
            fromCapabilityId = "first", toCapabilityId = "second", kind = "navigation", note = "Reviewed source", expectedVersion = 1,
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var restarted = new ExplorerFactory().WithWebHostBuilder(builder => builder
            .UseSetting("capabilities", Path.Combine(_root, "capabilities.json"))
            .UseSetting("workspace", Path.Combine(_root, "sources"))
            .UseSetting("requests", Path.Combine(_root, "requests")));
        using var client = restarted.CreateClient();
        var loaded = await client.GetFromJsonAsync<JsonElement>("/api/architecture/connections");
        Assert.Equal(2, loaded.GetProperty("version").GetInt64());
        var connection = Assert.Single(loaded.GetProperty("connections").EnumerateArray());
        Assert.Equal(id, connection.GetProperty("id").GetString());
        Assert.Equal("navigation", connection.GetProperty("kind").GetString());
        Assert.Equal("Reviewed source", connection.GetProperty("note").GetString());
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(Path.Combine(_root, ".sandbox-cache", "architecture-connections.json"));
            Assert.Equal(UnixFileMode.None, mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite));
        }
    }

    [Fact]
    public async Task Given_another_writer_holds_the_store_lock_when_saving_then_a_conflict_preserves_existing_data()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, ".sandbox-cache")).FullName;
        using (var competingWriter = new FileStream(Path.Combine(directory, "architecture-connections.json.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            using var response = await _client.PostAsJsonAsync("/api/architecture/connections", new
            {
                fromCapabilityId = "first", toCapabilityId = "second", kind = "http", expectedVersion = 0,
            });
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
        var state = await _client.GetFromJsonAsync<JsonElement>("/api/architecture/connections");
        Assert.Equal(0, state.GetProperty("version").GetInt64());
        Assert.Empty(state.GetProperty("connections").EnumerateArray());
    }

    [Fact]
    public async Task Given_analyzed_nodes_when_connected_then_both_repository_and_node_anchors_are_preserved()
    {
        var sourceNodes = await _client.GetFromJsonAsync<JsonElement>("/api/search?cap=first&repo=api-cs&kind=Method&q=Average");
        var targetNodes = await _client.GetFromJsonAsync<JsonElement>("/api/search?cap=second&repo=forecast-cs&kind=Type&q=StrategyLoader");
        var sourceKey = sourceNodes.EnumerateArray().First().GetProperty("key").GetString();
        var targetKey = targetNodes.EnumerateArray().First().GetProperty("key").GetString();
        using var created = await _client.PostAsJsonAsync("/api/architecture/connections", new
        {
            fromCapabilityId = "first", toCapabilityId = "second", kind = "http", expectedVersion = 0,
            sourceRepositoryId = "api-cs", sourceKey, targetRepositoryId = "forecast-cs", targetKey,
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var saved = await created.Content.ReadFromJsonAsync<JsonElement>();
        var connection = Assert.Single(saved.GetProperty("connections").EnumerateArray());
        Assert.True(connection.TryGetProperty("targetRepositoryId", out var repository), "The selected target repository must be persisted.");
        Assert.Equal("forecast-cs", repository.GetString());
        Assert.Equal(targetKey, connection.GetProperty("targetKey").GetString());
        Assert.Equal(sourceKey, connection.GetProperty("sourceKey").GetString());
        var system = await _client.GetFromJsonAsync<JsonElement>("/api/architecture?level=system");
        Assert.Contains(system.GetProperty("edges").EnumerateArray(), edge => edge.TryGetProperty("manualConnectionId", out var manual) && manual.GetString() == connection.GetProperty("id").GetString()
            && edge.GetProperty("from").GetString() == "repo:api-cs" && edge.GetProperty("to").GetString() == "repo:forecast-cs");
    }

    [Fact]
    public async Task Given_a_target_anchor_outside_its_capability_when_saving_then_it_is_rejected()
    {
        using var invalidRepository = await _client.PostAsJsonAsync("/api/architecture/connections", new
        {
            fromCapabilityId = "first", toCapabilityId = "second", kind = "http", expectedVersion = 0,
            sourceRepositoryId = "api-cs", targetRepositoryId = "api-cs",
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidRepository.StatusCode);
        using var unknownNode = await _client.PostAsJsonAsync("/api/architecture/connections", new
        {
            fromCapabilityId = "first", toCapabilityId = "second", kind = "http", expectedVersion = 0,
            targetRepositoryId = "forecast-cs", targetKey = "cs|forecast-cs|Forecast.Api|M:Unknown.Node",
        });
        Assert.Equal(HttpStatusCode.BadRequest, unknownNode.StatusCode);
    }

    [Fact]
    public async Task Given_a_malformed_manual_catalogue_when_browsing_then_native_graph_survives_and_writes_preserve_the_file()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, ".sandbox-cache")).FullName;
        var path = Path.Combine(directory, "architecture-connections.json");
        const string malformed = """{"version":1,"connections":[null]}""";
        File.WriteAllText(path, malformed);
        using var catalogue = await _client.GetAsync("/api/architecture/connections");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, catalogue.StatusCode);
        using var graph = await _client.GetAsync("/api/architecture?level=landscape");
        Assert.Equal(HttpStatusCode.OK, graph.StatusCode);
        var body = await graph.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEmpty(body.GetProperty("nodes").EnumerateArray());
        Assert.Contains(body.GetProperty("notes").EnumerateArray(), note => note.GetString()!.Contains("Manual connections", StringComparison.Ordinal));
        using var write = await _client.PostAsJsonAsync("/api/architecture/connections", new
        {
            fromCapabilityId = "first", toCapabilityId = "second", kind = "http", expectedVersion = 1,
        });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, write.StatusCode);
        Assert.Equal(malformed, File.ReadAllText(path));
    }
}