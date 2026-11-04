using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class ArchitectureConnectionsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ce-graph-connections-").FullName;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ArchitectureConnectionsTests()
    {
        var capabilities = Path.Combine(_root, "capabilities.json");
        File.WriteAllText(capabilities, JsonSerializer.Serialize(new
        {
            capabilities = new[]
            {
                new { id = "first", name = "First", repositories = new[] { new { id = "api-cs", kind = "local", location = Fixtures.RepoDirectory("api-cs"), @ref = "HEAD" } } },
                new { id = "second", name = "Second", repositories = new[] { new { id = "forecast-cs", kind = "local", location = Fixtures.RepoDirectory("forecast-cs"), @ref = "HEAD" } } },
            },
        }));
        _factory = new ExplorerFactory().WithWebHostBuilder(builder => builder
            .UseSetting("capabilities", capabilities).UseSetting("workspace", Path.Combine(_root, "sources")));
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
            .UseSetting("workspace", Path.Combine(_root, "sources")));
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
}