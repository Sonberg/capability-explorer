extern alias analysiscli;
using System.Net;
using System.Text;
using System.Text.Json;
using analysiscli::CapabilityExplorer.Cli;
using CapabilityExplorer.Explorer;
using CapabilityExplorer.Interpretation;
using CapabilityExplorer.Store;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CapabilityExplorer.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class EmbeddingConfigurationConsistencyTests(PostgresFixture db)
{
    [Theory]
    [InlineData("interpretation:useDefaultAzureCredential", "invalid-bool-canary")]
    [InlineData("model-config", "nonexistent-canary-model-config.json")]
    public async Task Malformed_shared_configuration_stays_lazy_and_cannot_block_core_startup(string key, string value)
    {
        var scope = "embedding-lazy-" + Guid.NewGuid().ToString("N");
        var snapshots = new SnapshotStore(db.DataSource);
        var snapshot = await snapshots.PublishAsync((await snapshots.CreateAsync(scope, "facts", [await SnapshotEmbeddingTests.Import(db)])).Id, 0);
        using var owner = new DatabaseApiFactory(db.ConnectionString, scope);
        using var factory = owner.WithWebHostBuilder(builder => builder.UseSetting(key, value));
        using var client = factory.CreateClient();
        using var meta = await client.GetAsync(new Uri($"/api/meta?cap={scope}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, meta.StatusCode);
        using var search = await client.GetAsync(new Uri($"/api/search?cap={scope}&q=week", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        using var status = await client.GetAsync(new Uri($"/api/embeddings/status?cap={scope}&snapshot={snapshot.Id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status.StatusCode);
        var body = await status.Content.ReadAsStringAsync();
        Assert.Contains("invalid-model-configuration", body, StringComparison.Ordinal);
        Assert.DoesNotContain("canary", body, StringComparison.Ordinal);
        Assert.Null(factory.Services.GetRequiredService<Intelligence>().Embeddings);
    }

    [Theory]
    [InlineData("shared-options")]
    [InlineData("shared-settings")]
    [InlineData("host-options")]
    [InlineData("host-settings")]
    public async Task API_automatic_enrichment_and_standalone_retry_agree_on_custom_configuration(string source)
    {
        var scope = "embedding-consistency-" + Guid.NewGuid().ToString("N");
        var snapshots = new SnapshotStore(db.DataSource);
        var snapshot = await snapshots.PublishAsync((await snapshots.CreateAsync(scope, "facts", [await SnapshotEmbeddingTests.Import(db)])).Id, 0);
        using var owner = new DatabaseApiFactory(db.ConnectionString, scope);
        var directory = Path.GetDirectoryName(owner.CapabilitiesPath)!;
        var settings = Path.Combine(directory, "custom-settings.json");
        new ModelSettings("https://synthetic.invalid/v1", null, "custom-saved", null).Save(settings);
        var configured = source.EndsWith("settings", StringComparison.Ordinal)
            ? new Dictionary<string, string> { ["modelSettings"] = settings }
            : new Dictionary<string, string>
            {
                ["interpretation:provider"] = "openai", ["interpretation:endpoint"] = "https://synthetic.invalid/v1",
                ["interpretation:embeddingModel"] = "custom-configured", ["interpretation:embeddingRevision"] = "custom-revision",
                ["interpretation:useDefaultAzureCredential"] = "false",
            };
        var shared = Path.Combine(directory, "shared-options.json");
        File.WriteAllText(shared, JsonSerializer.Serialize(new
        {
            modelSettings = source.EndsWith("settings", StringComparison.Ordinal) ? settings : null,
            interpretation = source.EndsWith("options", StringComparison.Ordinal) ? new
            {
                provider = "openai", endpoint = "https://synthetic.invalid/v1", embeddingModel = "custom-configured",
                embeddingRevision = "custom-revision", useDefaultAzureCredential = false,
            } : null,
        }));
        var savedEnvironment = new Dictionary<string, string?>();
        void Set(string key, string? value)
        {
            savedEnvironment.TryAdd(key, Environment.GetEnvironmentVariable(key));
            Environment.SetEnvironmentVariable(key, value);
        }
        try
        {
            Set("CE_DATABASE", db.ConnectionString);
            Set("CE_MODEL_ENDPOINT", null);
            Set("CE_EMBEDDING_MODEL", null);
            Set("CE_EMBEDDING_REVISION", null);
            Set("CE_MODEL_SETTINGS", null);
            Set("CE_MODEL_CONFIG", source.StartsWith("shared-", StringComparison.Ordinal) ? shared : null);
            var options = source.StartsWith("host-", StringComparison.Ordinal) ? configured : null;
            var expected = EmbeddingHostConfiguration.Resolve(options);
            Assert.NotNull(expected);
            using var factory = owner.WithWebHostBuilder(builder =>
            {
                if (source == "shared-settings") builder.UseSetting("modelSettings", settings);
                if (!source.StartsWith("host-", StringComparison.Ordinal)) return;
                foreach (var pair in configured) builder.UseSetting(pair.Key, pair.Value);
            });
            using var client = factory.CreateClient();
            var path = new Uri($"/api/embeddings/status?cap={scope}&snapshot={snapshot.Id}", UriKind.Relative);
            using var initial = await client.GetAsync(path);
            initial.EnsureSuccessStatusCode();
            var initialBody = await initial.Content.ReadAsStringAsync();
            using var json = JsonDocument.Parse(initialBody);
            Assert.True(json.RootElement.GetProperty("configuredModel").ValueKind == JsonValueKind.Object, initialBody);
            var fingerprint = json.RootElement.GetProperty("configuredModel").GetProperty("fingerprint").GetString();
            Assert.Equal("pending", json.RootElement.GetProperty("state").GetString());
            Assert.Equal(fingerprint, expected.Identity.Fingerprint);
            var intelligence = factory.Services.GetRequiredService<Intelligence>();
            Assert.Null(intelligence.Embeddings);
            using var handler = new FakeProvider { Fail = true };
            using var modelHttp = new HttpClient(handler);
            var failed = await AnalysisEnrichment.Run(snapshot.Id, options: options, embeddingHttp: modelHttp);
            Assert.Equal("failed", failed.Outcome);
            Assert.Equal(1, handler.Requests);
            using var partialResponse = await client.GetAsync(path);
            using var partial = JsonDocument.Parse(await partialResponse.Content.ReadAsStringAsync());
            Assert.Equal("partial-failure", partial.RootElement.GetProperty("state").GetString());
            var retry = options is null ? new Dictionary<string, string>() : new Dictionary<string, string>(options);
            retry["snapshot"] = snapshot.Id.ToString();
            handler.Fail = false;
            Assert.Equal(0, await EmbeddingCommand.Run(retry, [], embeddingHttp: modelHttp));
            using var readyResponse = await client.GetAsync(path);
            using var ready = JsonDocument.Parse(await readyResponse.Content.ReadAsStringAsync());
            Assert.Equal("ready", ready.RootElement.GetProperty("state").GetString());
            Assert.Equal(fingerprint, ready.RootElement.GetProperty("coverageModel").GetProperty("fingerprint").GetString());
            var requests = handler.Requests;
            Assert.Equal("done", (await AnalysisEnrichment.Run(snapshot.Id, options: options, embeddingHttp: modelHttp)).Outcome);
            Assert.Equal(0, await EmbeddingCommand.Run(retry, [], embeddingHttp: modelHttp));
            Assert.Equal(requests, handler.Requests);
            Assert.Equal(1, (await snapshots.GetPublishedAsync(scope))!.Generation);
            Assert.Null(intelligence.Embeddings);
        }
        finally
        {
            foreach (var pair in savedEnvironment) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    private sealed class FakeProvider : HttpMessageHandler
    {
        public bool Fail { get; set; }
        public int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (Fail) return new(HttpStatusCode.BadRequest) { Content = new StringContent("synthetic failure") };
            using var input = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var count = input.RootElement.GetProperty("input").GetArrayLength();
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { data = Enumerable.Range(0, count).Select(i => new { index = i, embedding = new[] { 1f, 2f, 3f } }) }),
                    Encoding.UTF8, "application/json"),
            };
        }
    }
}
