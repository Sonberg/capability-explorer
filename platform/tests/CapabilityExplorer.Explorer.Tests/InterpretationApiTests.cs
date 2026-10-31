using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CapabilityExplorer.Interpretation;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace CapabilityExplorer.Tests;

/// <summary>The explorer with the optional layer switched on, replaying recorded model responses.</summary>
public sealed class RecordedExplorerFactory : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
{
    public string Recordings { get; } = Directory.CreateTempSubdirectory("ce-recordings").FullName;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("artifacts", Fixtures.ExpectedDirectory);
        builder.UseSetting("repos", Path.Combine(Fixtures.RepositoryRoot, "fixtures", ".repos"));
        builder.UseSetting("interpretation:provider", "recorded");
        builder.UseSetting("interpretation:recordings", Recordings);
    }
}

public sealed class InterpretationDisabledTests(ExplorerFactory factory) : IClassFixture<ExplorerFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Llm_is_off_by_default_and_every_other_feature_still_works()
    {
        var meta = await _client.GetFromJsonAsync<JsonElement>("/api/meta");
        Assert.False(meta.GetProperty("interpretation").GetProperty("enabled").GetBoolean());
        Assert.Equal("none", meta.GetProperty("interpretation").GetProperty("provider").GetString());

        var compare = await _client.GetFromJsonAsync<JsonElement>($"/api/compare?a={Uri.EscapeDataString(GoldenCalculations.ApiCalendar)}&b={Uri.EscapeDataString(GoldenCalculations.WorkerActive)}");
        Assert.Contains(compare.GetProperty("differences").EnumerateArray(), d => d.GetProperty("attribute").GetString() == "denominatorSource");

        var explain = await _client.PostAsJsonAsync("/api/explanations", new { a = GoldenCalculations.ApiCalendar, b = GoldenCalculations.WorkerActive });
        Assert.Equal(HttpStatusCode.Conflict, explain.StatusCode);
        Assert.Contains("disabled", await explain.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}

public sealed class InterpretationEnabledTests(RecordedExplorerFactory factory) : IClassFixture<RecordedExplorerFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Recorded_provider_produces_a_validated_proposal()
    {
        // Record exactly what the explorer will ask for: same comparison, same model id.
        var comparison = GoldenCalculations.CalendarVsActive();
        var request = new ExplanationService(new RecordedModel(factory.Recordings)).BuildRequest(comparison);
        await File.WriteAllTextAsync(Path.Combine(factory.Recordings, $"{request.InputHash}.json"), JsonSerializer.Serialize(new
        {
            summary = "The API divides by calendar weeks in the period, the worker by weeks that had sales.",
            points = comparison.Differences.Select(d => new { differenceId = d.Id, text = $"{d.Attribute}: {d.Left?.Value} vs {d.Right?.Value}" }),
        }));

        var meta = await _client.GetFromJsonAsync<JsonElement>("/api/meta");
        Assert.True(meta.GetProperty("interpretation").GetProperty("enabled").GetBoolean());

        var response = await _client.PostAsJsonAsync("/api/explanations", new { a = GoldenCalculations.ApiCalendar, b = GoldenCalculations.WorkerActive });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var proposal = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("proposed", proposal.GetProperty("status").GetString());
        Assert.Equal("llm", proposal.GetProperty("derivation").GetString());
        Assert.Equal("unreviewed", proposal.GetProperty("reviewStatus").GetString());
        Assert.Equal(request.InputHash, proposal.GetProperty("inputHash").GetString());
    }

    [Fact]
    public async Task Missing_recording_is_a_503_not_a_crash()
    {
        var response = await _client.PostAsJsonAsync("/api/explanations", new { a = GoldenCalculations.WorkerActive, b = GoldenCalculations.ApiCalendar });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("no recorded response", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Identical_calculations_have_nothing_to_explain()
    {
        var response = await _client.PostAsJsonAsync("/api/explanations", new { a = GoldenCalculations.WorkerActive, b = GoldenCalculations.WorkerActive });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }
}

/// <summary>The explorer with an isolated settings file, so saving from the API never touches a real one.</summary>
public sealed class SettingsExplorerFactory : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
{
    public string SettingsPath { get; } = Path.Combine(Directory.CreateTempSubdirectory("ce-model").FullName, "model.json");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("artifacts", Fixtures.ExpectedDirectory);
        builder.UseSetting("modelSettings", SettingsPath);
    }
}

public sealed class ModelSettingsApiTests(SettingsExplorerFactory factory) : IClassFixture<SettingsExplorerFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Saving_applies_at_once_and_the_api_key_is_never_sent_back()
    {
        var saved = await _client.PutAsJsonAsync("/api/settings/model", new { endpoint = "https://res.services.ai.azure.com", chatModel = "chat", embeddingModel = "emb", apiKey = "s3cret-key" });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var text = await saved.Content.ReadAsStringAsync();
        Assert.DoesNotContain("s3cret-key", text, StringComparison.Ordinal);
        var status = JsonDocument.Parse(text).RootElement;
        Assert.Equal(("saved", true, "saved"), (status.GetProperty("source").GetString(), status.GetProperty("hasApiKey").GetBoolean(), status.GetProperty("apiKeySource").GetString()));

        var meta = await _client.GetFromJsonAsync<JsonElement>("/api/meta");
        Assert.True(meta.GetProperty("interpretation").GetProperty("enabled").GetBoolean());
        Assert.Equal("foundry:emb", meta.GetProperty("similarity").GetProperty("embeddings").GetString());
        Assert.False(meta.GetProperty("similarity").GetProperty("database").GetBoolean());

        // A blank key keeps the saved one.
        await _client.PutAsJsonAsync("/api/settings/model", new { endpoint = "https://res.services.ai.azure.com", embeddingModel = "emb2" });
        Assert.Equal("s3cret-key", ModelSettings.Load(factory.SettingsPath)!.ApiKey);

        // Without a database, similarity says why instead of failing.
        var similar = await _client.GetAsync("/api/similar?key=x");
        Assert.Equal(HttpStatusCode.Conflict, similar.StatusCode);
        Assert.Contains("CE_DATABASE", await similar.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://res.services.ai.azure.com", "emb", "https URL")]
    [InlineData("http://203.0.113.7:11434/v1", "emb", "https URL")]
    [InlineData("https://res.services.ai.azure.com", "bad name;", "may contain")]
    [InlineData("https://res.services.ai.azure.com", "", "chat model, an embedding model")]
    public async Task Invalid_settings_are_rejected_and_not_saved(string endpoint, string embeddingModel, string message)
    {
        var before = File.Exists(factory.SettingsPath) ? await File.ReadAllTextAsync(factory.SettingsPath) : null;
        var response = await _client.PutAsJsonAsync("/api/settings/model", new { endpoint, embeddingModel });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(message, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(before, File.Exists(factory.SettingsPath) ? await File.ReadAllTextAsync(factory.SettingsPath) : null);
    }
}

public sealed class LocalModelSettingsApiTests(SettingsExplorerFactory factory) : IClassFixture<SettingsExplorerFactory>
{
    [Fact]
    public async Task A_local_ollama_over_plain_http_is_accepted_without_a_key()
    {
        var client = factory.CreateClient();
        var response = await client.PutAsJsonAsync("/api/settings/model", new { endpoint = "http://host.docker.internal:11434/v1", chatModel = "qwen2.5:7b", embeddingModel = "nomic-embed-text" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("openai", false), (status.GetProperty("provider").GetString(), status.GetProperty("hasApiKey").GetBoolean()));

        var meta = await client.GetFromJsonAsync<JsonElement>("/api/meta");
        Assert.Equal("openai:nomic-embed-text", meta.GetProperty("similarity").GetProperty("embeddings").GetString());
        Assert.Equal("openai:qwen2.5:7b", meta.GetProperty("interpretation").GetProperty("model").GetString());
    }
}
