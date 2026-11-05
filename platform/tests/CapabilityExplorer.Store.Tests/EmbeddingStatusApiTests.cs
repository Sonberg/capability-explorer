using System.Net;
using System.Text.Json;
using CapabilityExplorer.Interpretation;
using CapabilityExplorer.Store;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CapabilityExplorer.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class EmbeddingStatusApiTests(PostgresFixture db)
{
    [Fact]
    public async Task Status_has_five_truthful_states_explicit_nulls_and_never_changes_core_generation_or_similarity_support()
    {
        var scope = "embedding-api-" + Guid.NewGuid().ToString("N");
        var snapshots = new SnapshotStore(db.DataSource);
        var snapshot = await snapshots.PublishAsync((await snapshots.CreateAsync(scope, "facts", [await SnapshotEmbeddingTests.Import(db)])).Id, 0);
        using var factory = new DatabaseApiFactory(db.ConnectionString, scope);
        using var client = factory.CreateClient();
        var path = $"/api/embeddings/status?cap={scope}&snapshot={snapshot.Id}";
        var absent = await Read(client, path);
        Assert.Equal("not-configured", absent.GetProperty("state").GetString());
        foreach (var field in new[] { "configuredModel", "coverageModel", "snapshotArtifactsSha256", "counts", "lastSuccessfulCompletion" })
            Assert.Equal(JsonValueKind.Null, absent.GetProperty(field).ValueKind);
        Assert.False(absent.GetProperty("countsExact").GetBoolean());
        Assert.False(absent.GetProperty("similarityAvailable").GetBoolean());
        Assert.Equal("selected-view", absent.GetProperty("coverageScope").GetString());
        Assert.Equal(new string('a', 40), absent.GetProperty("selection").GetProperty("web-ts").GetString());
        var settings = Path.Combine(Path.GetDirectoryName(factory.CapabilitiesPath)!, "no-model-settings.json");
        new ModelSettings("http://localhost:11434/v1", null, "fake", null).Save(settings);
        var pending = await Read(client, path);
        Assert.Equal("pending", pending.GetProperty("state").GetString());
        var configuration = EmbeddingConfiguration.Resolve(new InterpretationOptions(), settings)!;
        var model = new SnapshotEmbeddingModel(configuration.Identity.Id, configuration.Identity.Fingerprint, configuration.Identity.RecipeVersion);
        var embeddings = new SnapshotEmbeddingStore(db.DataSource, db.ConnectionString);
        await embeddings.EmbedAsync(snapshot.Id, model, SnapshotEmbeddingTests.Fake);
        var ready = await Read(client, path);
        Assert.Equal("ready", ready.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, ready.GetProperty("reasonCode").ValueKind);
        Assert.Equal(model.Fingerprint, ready.GetProperty("coverageModel").GetProperty("fingerprint").GetString());
        Assert.True(ready.GetProperty("countsExact").GetBoolean());
        new ModelSettings("http://localhost:11435/v1", null, "fake", null).Save(settings);
        Assert.Equal("stale", (await Read(client, path)).GetProperty("state").GetString());
        configuration = EmbeddingConfiguration.Resolve(new InterpretationOptions(), settings)!;
        model = new(configuration.Identity.Id, configuration.Identity.Fingerprint, configuration.Identity.RecipeVersion);
        await Assert.ThrowsAsync<EmbeddingProjectionFailedException>(() => embeddings.EmbedAsync(snapshot.Id, model, (_, _) =>
            throw new InvalidOperationException("synthetic provider failure")));
        Assert.Equal("partial-failure", (await Read(client, path)).GetProperty("state").GetString());
        await embeddings.EmbedAsync(snapshot.Id, model, SnapshotEmbeddingTests.Fake);
        Assert.Equal("ready", (await Read(client, path)).GetProperty("state").GetString());
        Assert.Equal(snapshot.Generation, (await snapshots.GetPublishedAsync(scope))!.Generation);
        using var similar = await client.GetAsync(new Uri($"/api/similar?cap={scope}&snapshot={snapshot.Id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotImplemented, similar.StatusCode);
        using var unavailable = await client.GetAsync(new Uri($"/api/embeddings/status?cap={scope}&snapshot={Guid.NewGuid()}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Conflict, unavailable.StatusCode);
    }

    [Fact]
    public async Task Invalid_configuration_is_an_explicit_sanitized_error_not_missing_configuration()
    {
        var scope = "embedding-api-" + Guid.NewGuid().ToString("N");
        var snapshots = new SnapshotStore(db.DataSource);
        var snapshot = await snapshots.PublishAsync((await snapshots.CreateAsync(scope, "facts", [])).Id, 0);
        using var factory = new DatabaseApiFactory(db.ConnectionString, scope);
        using var client = factory.CreateClient();
        new ModelSettings("https://secret-user:secret-password@model.invalid/v1?secret-token=bad", null, "fake", null)
            .Save(Path.Combine(Path.GetDirectoryName(factory.CapabilitiesPath)!, "no-model-settings.json"));
        using var response = await client.GetAsync(new Uri($"/api/embeddings/status?cap={scope}&snapshot={snapshot.Id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("invalid-model-configuration", body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Optional_model_configuration_is_lazy_at_startup_and_core_queries_remain_available(bool invalid)
    {
        var scope = "embedding-startup-" + Guid.NewGuid().ToString("N");
        var snapshots = new SnapshotStore(db.DataSource);
        var snapshot = await snapshots.PublishAsync((await snapshots.CreateAsync(scope, "facts", [await SnapshotEmbeddingTests.Import(db)])).Id, 0);
        using var owner = new DatabaseApiFactory(db.ConnectionString, scope);
        using var factory = owner.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("interpretation:provider", "openai");
            builder.UseSetting("interpretation:endpoint", invalid ? "https://userinfo-canary:private-canary@model.invalid/v1?query-canary=token" : "https://model.invalid/v1");
            builder.UseSetting("interpretation:embeddingModel", "fake");
        });
        using var client = factory.CreateClient();
        var intelligence = factory.Services.GetRequiredService<CapabilityExplorer.Explorer.Intelligence>();
        Assert.Null(intelligence.Embeddings);
        Assert.Null(intelligence.Layer.Chat);
        Assert.Null(intelligence.Layer.Explanations);
        Assert.Equal(1, (await Read(client, $"/api/meta?cap={scope}")).GetProperty("generation").GetInt64());
        await Read(client, $"/api/search?cap={scope}&q=week");
        using var status = await client.GetAsync(new Uri($"/api/embeddings/status?cap={scope}&snapshot={snapshot.Id}", UriKind.Relative));
        Assert.Equal(invalid ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, status.StatusCode);
        var body = await status.Content.ReadAsStringAsync();
        Assert.DoesNotContain("canary", body, StringComparison.Ordinal);
        if (!invalid) Assert.Contains("\"state\":\"pending\"", body, StringComparison.Ordinal);
        Assert.Null(intelligence.Embeddings);
    }

    private static async Task<JsonElement> Read(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.Clone();
    }
}
