using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class AnalysisApiTests
{
    private const string Url = "/api/capabilities/demand-pricing/analyze";

    [Fact]
    public async Task Given_host_github_accounts_when_the_account_list_is_requested_then_only_login_metadata_is_returned()
    {
        using var factory = new CapabilityExplorerFactory();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/github-accounts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("available").ValueKind is JsonValueKind.True or JsonValueKind.False);
        foreach (var account in result.GetProperty("accounts").EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(account.GetProperty("login").GetString()));
            Assert.True(account.GetProperty("active").ValueKind is JsonValueKind.True or JsonValueKind.False);
            Assert.False(account.TryGetProperty("token", out _));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_cancelled_prepublication_execution_when_operator_authorizes_retry_then_old_evidence_is_preserved_and_a_new_run_is_queued(bool acknowledge)
    {
        using var factory = new CapabilityExplorerFactory();
        using var client = factory.CreateClient();
        var store = new CapabilityExplorer.Capabilities.Analysis.FileRunStore(factory.RequestsDirectory);
        var admitted = store.Admit("demand-pricing",factory.ConfigurationPath,null,new()).Run;
        CapabilityExplorer.Capabilities.Analysis.AnalysisRun prior;
        using (var owner = store.AcquireWorker())
        {
            prior = store.ClaimNext(owner,admitted.RunId)!;
            store.Cancel(prior.RunId);
            store.FinishExecution(owner,new(prior.RunId,prior.AttemptId!),1,false);
        }
        var record = Path.Combine(store.RunDirectory(prior.RunId),"run.json");
        var bytes = await File.ReadAllBytesAsync(record);

        var response = await client.PostAsJsonAsync("/api/capabilities/demand-pricing/retry",new
        {
            runId = prior.RunId, attemptId = prior.AttemptId, acknowledgeUnverifiedExecution = acknowledge,
        });

        Assert.Equal(acknowledge ? HttpStatusCode.Accepted : HttpStatusCode.BadRequest,response.StatusCode);
        if (!acknowledge)
        {
            Assert.Equal(bytes,await File.ReadAllBytesAsync(record));
            Assert.Equal("unknown",store.ReadRun(prior.RunId).State);
            return;
        }
        var job = await response.Content.ReadFromJsonAsync<JsonElement>();
        var nextId = job.GetProperty("runId").GetString()!;
        Assert.NotEqual(prior.RunId,nextId);
        Assert.Equal("queued",job.GetProperty("state").GetString());
        Assert.False(job.GetProperty("blocked").GetBoolean());
        Assert.Equal(nextId,store.ReadLatest("demand-pricing")!.RunId);
        Assert.Null(store.ReadBlockReason());
        var archive = Path.Combine(factory.RequestsDirectory,"reconciled",prior.RunId);
        Assert.Equal(bytes,await File.ReadAllBytesAsync(Path.Combine(archive,"evidence","run.json")));
        var decision = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(archive,"decision.json")));
        using (decision)
        {
            Assert.Equal("operator-released-for-retry",decision.RootElement.GetProperty("outcome").GetString());
            Assert.False(decision.RootElement.GetProperty("terminationVerified").GetBoolean());
            Assert.Equal(prior.AttemptId,decision.RootElement.GetProperty("attemptId").GetString());
        }
        Assert.DoesNotContain(factory.RequestsDirectory,await response.Content.ReadAsStringAsync(),StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("publication")]
    [InlineData("stale-attempt")]
    [InlineData("not-cancelled")]
    public async Task Given_unsafe_or_stale_recovery_when_retry_is_requested_then_no_evidence_is_retired(string condition)
    {
        using var factory = new CapabilityExplorerFactory();
        using var client = factory.CreateClient();
        var store = new CapabilityExplorer.Capabilities.Analysis.FileRunStore(factory.RequestsDirectory);
        store.Admit("demand-pricing",factory.ConfigurationPath,null,new());
        CapabilityExplorer.Capabilities.Analysis.AnalysisRun prior;
        using (var owner = store.AcquireWorker())
        {
            prior = store.ClaimNext(owner)!;
            if (condition == "publication")
            {
                var manifest = store.BindResolved(owner,new(prior.RunId,prior.AttemptId!),new(prior.AcceptedInputsHash,
                    new Dictionary<string,string> { ["source"] = new('a',64) }));
                store.FinishExecution(owner,new(prior.RunId,prior.AttemptId!),0);
                using (var permit = store.BeginPublication(owner,new(prior.RunId,prior.AttemptId!),manifest))
                    permit.Intent("local-artifacts","preserve");
                store.Cancel(prior.RunId);
            }
            else
            {
                if (condition != "not-cancelled") store.Cancel(prior.RunId);
                store.FinishExecution(owner,new(prior.RunId,prior.AttemptId!),1,false);
            }
        }
        var record = Path.Combine(store.RunDirectory(prior.RunId),"run.json");
        var bytes = await File.ReadAllBytesAsync(record);

        var response = await client.PostAsJsonAsync("/api/capabilities/demand-pricing/retry",new
        {
            runId = prior.RunId, attemptId = condition == "stale-attempt" ? Guid.NewGuid().ToString("N") : prior.AttemptId,
            acknowledgeUnverifiedExecution = true,
        });

        Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);
        Assert.Equal(bytes,await File.ReadAllBytesAsync(record));
        Assert.False(Directory.Exists(Path.Combine(factory.RequestsDirectory,"reconciled",prior.RunId)));
        Assert.Equal(prior.RunId,store.ReadLatest("demand-pricing")!.RunId);
        Assert.NotNull(store.ReadBlockReason());
    }

    [Fact]
    public async Task Given_a_recorded_operator_decision_when_retirement_was_interrupted_before_move_then_retry_can_resume_without_deleting_evidence()
    {
        using var factory = new CapabilityExplorerFactory();
        using var client = factory.CreateClient();
        var store = new CapabilityExplorer.Capabilities.Analysis.FileRunStore(factory.RequestsDirectory);
        store.Admit("demand-pricing",factory.ConfigurationPath,null,new());
        CapabilityExplorer.Capabilities.Analysis.AnalysisRun prior;
        using (var owner = store.AcquireWorker())
        {
            prior = store.ClaimNext(owner)!;
            store.Cancel(prior.RunId);
            store.FinishExecution(owner,new(prior.RunId,prior.AttemptId!),1,false);
        }
        var archive = Path.Combine(factory.RequestsDirectory,"reconciled",prior.RunId);
        Directory.CreateDirectory(archive);
        var decision = JsonSerializer.SerializeToUtf8Bytes(new
        {
            prior.RunId, prior.AttemptId, prior.CapabilityId, prior.AcceptedInputsHash,
            outcome = "operator-released-for-retry", terminationVerified = false,
        },JsonSerializerOptions.Web);
        await File.WriteAllBytesAsync(Path.Combine(archive,"decision.json"),decision);
        var original = await File.ReadAllBytesAsync(Path.Combine(store.RunDirectory(prior.RunId),"run.json"));

        var response = await client.PostAsJsonAsync("/api/capabilities/demand-pricing/retry",new
        {
            prior.RunId, prior.AttemptId, acknowledgeUnverifiedExecution = true,
        });

        Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);
        Assert.Equal(decision,await File.ReadAllBytesAsync(Path.Combine(archive,"decision.json")));
        Assert.Equal(original,await File.ReadAllBytesAsync(Path.Combine(archive,"evidence","run.json")));
        Assert.Equal("queued",store.ReadLatest("demand-pricing")!.State);
    }

    [Theory]
    [InlineData("healthy")]
    [InlineData("legacy")]
    [InlineData("malformed")]
    public async Task Given_multiple_capabilities_when_status_is_batched_then_identities_and_global_safety_blocks_are_preserved(string evidence)
    {
        using var factory = new CapabilityExplorerFactory();
        using var client = factory.CreateClient();
        var created = await client.PostAsJsonAsync("/api/capabilities", new { name = "Other", repositories = "https://github.com/acme/other" });
        Assert.Equal(HttpStatusCode.Accepted,created.StatusCode);
        var admitted = await client.PostAsync(new Uri(Url,UriKind.Relative),null);
        Assert.Equal(HttpStatusCode.Accepted,admitted.StatusCode);
        var accepted = await admitted.Content.ReadFromJsonAsync<JsonElement>();
        if (evidence == "legacy") await File.WriteAllTextAsync(Path.Combine(factory.RequestsDirectory,"old.running"),"");
        if (evidence == "malformed")
        {
            var orphan = Path.Combine(factory.RequestsDirectory,"runs",Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(orphan);
            await File.WriteAllTextAsync(Path.Combine(orphan,"run.json"),"{invalid");
        }

        var response = await client.GetAsync(new Uri("/api/analysis-status",UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        Assert.Equal("application/json",response.Content.Headers.ContentType?.MediaType);
        var statuses = await response.Content.ReadFromJsonAsync<Dictionary<string,JsonElement>>();
        Assert.Equal(new[] { "demand-pricing","other" },statuses!.Keys.Order(StringComparer.Ordinal));
        Assert.All(statuses.Values,status => Assert.Equal(evidence != "healthy",status.GetProperty("blocked").GetBoolean()));
        if (evidence != "malformed") Assert.Equal(accepted.GetProperty("runId").GetString(),statuses["demand-pricing"].GetProperty("runId").GetString());
        if (evidence == "malformed") Assert.All(statuses.Values,status => Assert.Equal("unknown",status.GetProperty("state").GetString()));
        Assert.DoesNotContain(factory.RequestsDirectory,await response.Content.ReadAsStringAsync(),StringComparison.Ordinal);
        Assert.DoesNotContain("configurationPath",await response.Content.ReadAsStringAsync(),StringComparison.OrdinalIgnoreCase);
    }

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

    [Fact]
    public async Task Given_a_selected_host_github_account_when_analysis_is_queued_then_the_run_uses_that_account()
    {
        using var factory = new CapabilityExplorerFactory();
        using var client = factory.CreateClient();
        var response = await client.PostAsync(Url + "?githubAccount=fixture-account", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = await response.Content.ReadFromJsonAsync<JsonElement>();
        var store = new CapabilityExplorer.Capabilities.Analysis.FileRunStore(factory.RequestsDirectory);
        Assert.Equal("account:fixture-account", store.ReadRun(job.GetProperty("runId").GetString()!).Settings.GitHubAuth);
    }

}
