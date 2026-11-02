using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class AnalysisApiTests
{
    private const string Url = "/api/capabilities/demand-pricing/analyze";

    [Fact]
    public async Task Concurrent_admission_accepts_one_run_and_preserves_its_identity()
    {
        using var factory = new CapabilityExplorerFactory();
        using var client = factory.CreateClient();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<HttpResponseMessage> Admit()
        {
            await start.Task;
            return await client.PostAsync(new Uri(Url, UriKind.Relative), null);
        }
        var first = Admit();
        var second = Admit();
        start.SetResult();
        var responses = await Task.WhenAll(first, second);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Accepted);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        var accepted = await responses.Single(r => r.StatusCode == HttpStatusCode.Accepted).Content.ReadFromJsonAsync<JsonElement>();
        var latest = await client.GetFromJsonAsync<JsonElement>(Url);
        Assert.Equal(accepted.GetProperty("runId").GetString(), latest.GetProperty("runId").GetString());
        Assert.NotNull(responses.Single(r => r.StatusCode == HttpStatusCode.Accepted).Headers.Location);
    }

    [Fact]
    public async Task Stop_for_old_run_cannot_cancel_new_run_and_history_is_readable()
    {
        using var factory = new CapabilityExplorerFactory();
        using var client = factory.CreateClient();
        var first = await (await client.PostAsync(new Uri(Url, UriKind.Relative), null)).Content.ReadFromJsonAsync<JsonElement>();
        var oldUrl = new Uri($"{Url}?runId={first.GetProperty("runId").GetString()}", UriKind.Relative);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync(oldUrl)).StatusCode);
        var second = await (await client.PostAsync(new Uri(Url, UriKind.Relative), null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync(oldUrl)).StatusCode);
        var latest = await client.GetFromJsonAsync<JsonElement>(Url);
        Assert.Equal(second.GetProperty("runId").GetString(), latest.GetProperty("runId").GetString());
        Assert.Equal("queued", latest.GetProperty("state").GetString());
        Assert.Equal("stopped", (await client.GetFromJsonAsync<JsonElement>(oldUrl)).GetProperty("state").GetString());
    }

    [Theory]
    [InlineData("..%2Fother")]
    [InlineData("invalid")]
    public async Task Malformed_run_ids_fail_without_disclosing_host_paths(string runId)
    {
        using var factory = new CapabilityExplorerFactory();
        using var client = factory.CreateClient();
        var url = new Uri($"{Url}?runId={runId}", UriKind.Relative);
        foreach (var response in new[] { await client.GetAsync(url), await client.DeleteAsync(url) })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain(factory.RequestsDirectory, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Legacy_running_evidence_is_globally_blocked_and_never_reported_stopped()
    {
        using var factory = new CapabilityExplorerFactory();
        using var client = factory.CreateClient();
        Directory.CreateDirectory(factory.RequestsDirectory);
        await File.WriteAllTextAsync(Path.Combine(factory.RequestsDirectory, "another-capability.running"), "");
        var status = await client.GetFromJsonAsync<JsonElement>(Url);
        Assert.Equal("unknown", status.GetProperty("state").GetString());
        Assert.True(status.GetProperty("blocked").GetBoolean());
        Assert.True(File.Exists(Path.Combine(factory.RequestsDirectory, "another-capability.running")));
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync(new Uri(Url, UriKind.Relative))).StatusCode);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Incomplete_or_malformed_run_evidence_returns_blocked_unknown(bool malformed)
    {
        using var factory = new CapabilityExplorerFactory();
        using var client = factory.CreateClient();
        var store = new CapabilityExplorer.Capabilities.Analysis.FileRunStore(factory.RequestsDirectory);
        var orphan = store.RunDirectory(Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphan);
        if (malformed) await File.WriteAllTextAsync(Path.Combine(orphan, "run.json"), "{invalid");
        var response = await client.GetAsync(new Uri(Url, UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unknown", status.GetProperty("state").GetString());
        Assert.True(status.GetProperty("blocked").GetBoolean());
        Assert.DoesNotContain(factory.RequestsDirectory, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True(Directory.Exists(orphan));
    }

    [Fact]
    public async Task Admission_freezes_the_capability_include_tests_setting()
    {
        using var factory = new CapabilityExplorerFactory();
        var configuration = Path.Combine(Path.GetDirectoryName(factory.RequestsDirectory)!, "capabilities.json");
        var original = await File.ReadAllTextAsync(configuration);
        await File.WriteAllTextAsync(configuration, original.Replace("\"name\": \"Demand pricing\"", "\"name\": \"Demand pricing\", \"includeTests\": true", StringComparison.Ordinal));
        using var client = factory.CreateClient();
        var response = await client.PostAsync(new Uri(Url, UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>();
        var runId = accepted.GetProperty("runId").GetString()!;
        var store = new CapabilityExplorer.Capabilities.Analysis.FileRunStore(factory.RequestsDirectory);
        Assert.True(store.ReadRun(runId).Settings.IncludeTests);
        await File.WriteAllTextAsync(configuration, original);
        Assert.True(store.ReadRun(runId).Settings.IncludeTests);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Admission_freezes_nonsecret_worker_defaults_and_include_tests_precedence(bool? includeTestsOverride, bool expectedIncludeTests)
    {
        using var factory = new CapabilityExplorerFactory();
        var configuration = Path.Combine(Path.GetDirectoryName(factory.RequestsDirectory)!, "capabilities.json");
        var original = await File.ReadAllTextAsync(configuration);
        await File.WriteAllTextAsync(configuration, original.Replace("\"name\": \"Demand pricing\"", "\"name\": \"Demand pricing\", \"includeTests\": true", StringComparison.Ordinal));
        using var client = factory.CreateClient();
        var store = new CapabilityExplorer.Capabilities.Analysis.FileRunStore(factory.RequestsDirectory);
        using var owner = store.AcquireWorker();
        var defaults = new CapabilityExplorer.Capabilities.Analysis.AnalysisSettings(Packages: false, Locked: true,
            GitHubAuth: "none", NugetPassEnv: "PRIVATE_FEED_TOKEN", NugetAllowedOrigins: "https://packages.example.invalid");
        store.PublishWorkerDefaults(owner, defaults, includeTestsOverride);
        var response = await client.PostAsync(new Uri(Url, UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>();
        var runId = accepted.GetProperty("runId").GetString()!;
        var frozen = store.ReadRun(runId);
        Assert.Equal(defaults with { IncludeTests = expectedIncludeTests }, frozen.Settings);
        store.PublishWorkerDefaults(owner, new CapabilityExplorer.Capabilities.Analysis.AnalysisSettings(), !expectedIncludeTests);
        Assert.Equal(frozen, store.ReadRun(runId));
    }

}
