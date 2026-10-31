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
