using System.Net;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Text.Json;
using CapabilityExplorer.Explorer;
using CapabilityExplorer.Contracts.Collection;
using CapabilityExplorer.Store;
using Json.Schema;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CapabilityExplorer.Tests;

/// <summary>Split of <see cref="TaskQueryApiTests"/> (same authorized file, kept under the 500-line ceiling):
/// regressions for the Opus-review correction segment's findings #2 (shared bounded envelope), #5 (HTTP raw-input
/// parsing bypassing the envelope/deadline) and #6 (per-repository missing state, truncation-driven
/// completeness).</summary>
public sealed partial class TaskQueryApiTests
{
    /// <summary>Regression for finding #5: a malformed numeric query-string value used to be rejected by ASP.NET's
    /// own model binding with a generic framework 400 (no <c>ce.task-query</c> body at all), bypassing the shared
    /// envelope and deadline. It must now surface as a typed <c>invalid</c> envelope like every other rejection.</summary>
    [Fact]
    public async Task A_malformed_numeric_query_parameter_is_rejected_as_a_typed_invalid_envelope()
    {
        await using var host = await TaskQueryTestHost.CreateAsync();

        using var badLimit = await host.Client.GetAsync(new Uri("/api/tasks/v1/models?limit=not-a-number", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, badLimit.StatusCode);
        var limitBody = await badLimit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid", limitBody.GetProperty("status").GetString());
        Assert.Equal("ce.task-query.v1", limitBody.GetProperty("schema").GetString());
        Assert.Equal("invalid-limit", limitBody.GetProperty("reason").GetString());

        using var badGeneration = await host.Client.GetAsync(new Uri("/api/tasks/v1/models?generation=not-a-number", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, badGeneration.StatusCode);
        var generationBody = await badGeneration.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid", generationBody.GetProperty("status").GetString());
        Assert.Equal("invalid-generation", generationBody.GetProperty("reason").GetString());

        using var badStart = await host.Client.GetAsync(new Uri(
            $"/api/tasks/v1/source?repo=forecast-cs&commit={TaskQueryTestHost.ForecastCommit}&path=x&start=not-a-number&end=1", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, badStart.StatusCode);
        Assert.Equal("invalid-start", (await badStart.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").GetString());
    }

    /// <summary>Regression for finding #2: both adapters must share the exact same one-mebibyte envelope bound.
    /// This exercises the shared helper directly (an HTTP-level reproduction would need a multi-megabyte golden
    /// fixture to force a real envelope past the bound) and proves the bound is enforced with a well-formed
    /// typed <c>limit</c> envelope, never a truncated/partial body.</summary>
    [Fact]
    public void An_oversized_envelope_is_replaced_by_a_well_formed_limit_envelope_not_truncated()
    {
        var huge = string.Join(",", Enumerable.Repeat("x", 2_000_000));
        var oversized = new TaskQueryEnvelope("models_list", "ok", "files", null,
            new TaskQueryCoverage([], "within-selection-complete"), null, huge, []);

        var bytes = TaskQueryJson.SerializeBounded(oversized);

        Assert.True(bytes.Length <= TaskQueryJson.MaxEnvelopeBytes);
        var parsed = JsonSerializer.Deserialize<JsonElement>(bytes);
        Assert.Equal("limit", parsed.GetProperty("status").GetString());
        Assert.Equal("envelope-too-large", parsed.GetProperty("reason").GetString());
        Assert.True(parsed.GetProperty("data").ValueKind is JsonValueKind.Null);

        var small = new TaskQueryEnvelope("models_list", "ok", "files", null, new TaskQueryCoverage([], "within-selection-complete"), null, "fine", []);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(small, TaskQueryJson.Options), TaskQueryJson.SerializeBounded(small));
    }

    /// <summary>Regression for finding #6: a caller-requested repository that is not part of the resolved
    /// snapshot's own selection at all must surface as a distinct <c>missing</c> status, never conflated with an
    /// operator-<c>denied</c> repository, and completeness must correctly read <c>not-established</c>. HTTP does
    /// not expose a <c>Selection</c> query parameter (only <c>cap</c>-based resolution), so this exercises
    /// <see cref="TaskQueryService"/> directly over the same real fixture-backed host/service the HTTP tests use.</summary>
    [Fact]
    public async Task A_requested_repository_outside_the_resolved_snapshot_is_reported_as_missing_not_denied()
    {
        await using var host = await TaskQueryTestHost.CreateAsync();

        var selection = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["forecast-cs"] = TaskQueryTestHost.ForecastCommit,
            ["not-a-real-repo"] = new string('a', 40),
        };
        var envelope = await host.Service.SnapshotResolveAsync(new TaskQueryRequest(Selection: selection), CancellationToken.None);

        Assert.Equal("partial", envelope.Status);
        var repos = envelope.Coverage.Repositories.ToDictionary(r => r.RepositoryId, r => r.Status);
        Assert.Equal("partial", repos["forecast-cs"]);
        Assert.Equal("missing", repos["not-a-real-repo"]);
        Assert.Equal("not-established", envelope.Coverage.Completeness);
        Assert.True(host.EnvelopeSchema.Evaluate(JsonSerializer.SerializeToElement(envelope, TaskQueryJson.Options)).IsValid);
    }

    /// <summary>Regression for finding #3: a cursor issued while the caller's own selection was narrowed to one
    /// repository must not resolve against a later call under a different selection narrowing, even though the
    /// operator grant (and therefore the previous, selection-blind digest) is identical both times - the cursor's
    /// scope key must be bound to the effective selection it was issued under.</summary>
    [Fact]
    public async Task A_cursor_issued_under_one_selection_is_rejected_when_replayed_under_a_different_selection()
    {
        await using var host = await TaskQueryTestHost.CreateAsync();

        var narrowToForecast = new Dictionary<string, string>(StringComparer.Ordinal) { ["forecast-cs"] = TaskQueryTestHost.ForecastCommit };
        var narrowToPricing = new Dictionary<string, string>(StringComparer.Ordinal) { ["pricing-node"] = TaskQueryTestHost.PricingCommit };

        var first = await host.Service.ModelsListAsync(new TaskQueryRequest(SnapshotId: host.Snapshot!.Id, Generation: host.Snapshot.Generation, Selection: narrowToForecast, Limit: 1), CancellationToken.None);
        Assert.Equal("ok", first.Status);
        var cursor = first.Page!.NextCursor;
        Assert.False(string.IsNullOrEmpty(cursor));

        // Same cursor, same operator grant, but a DIFFERENT caller-narrowed selection: must not silently resolve
        // the stale position as if it still belonged to the new selection's own universe of rows.
        var replayed = await host.Service.ModelsListAsync(new TaskQueryRequest(SnapshotId: host.Snapshot!.Id, Generation: host.Snapshot.Generation, Selection: narrowToPricing, Limit: 1, Cursor: cursor), CancellationToken.None);
        Assert.NotEqual("ok", replayed.Status);
        Assert.Contains(replayed.Status, new[] { "stale", "invalid" });
    }

    [Theory]
    [InlineData("/models", "snapshot-required")]
    [InlineData("/models?snapshot=synthetic", "generation-required")]
    [InlineData("/models/decision-references", "invalid-model-id")]
    [InlineData("/contracts/consumers", "invalid-key")]
    [InlineData("/evidence", "invalid-key")]
    [InlineData("/source", "invalid-excerpt-request")]
    public async Task Missing_pins_and_targets_always_return_the_typed_invalid_envelope(string route, string reason)
    {
        await using var host = await TaskQueryTestHost.CreateAsync();
        var needsPin = route is not "/models" and not "/models?snapshot=synthetic";
        using var response = await host.Client.GetAsync($"/api/tasks/v1{route}{(needsPin ? "?" + host.Pin : "")}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(reason, body.GetProperty("reason").GetString());
        Assert.True(host.EnvelopeSchema.Evaluate(body).IsValid);
    }

    [Fact]
    public async Task Selection_revision_mismatch_is_stale_before_content_is_read()
    {
        await using var host = await TaskQueryTestHost.CreateAsync();
        var envelope = await host.Service.ModelsListAsync(new TaskQueryRequest(
            SnapshotId: host.Snapshot!.Id, Generation: host.Snapshot.Generation,
            Selection: new Dictionary<string, string> { ["forecast-cs"] = new string('a', 40) }), CancellationToken.None);
        Assert.Equal("stale", envelope.Status);
        Assert.Equal("selection-revision-mismatch", envelope.Reason);
        Assert.Equal("not-established", envelope.Coverage.Completeness);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Storage_failures_during_resolution_or_content_fetch_never_claim_complete_coverage(bool duringResolution)
    {
        var service = ServiceFor(new SyntheticIntegrationSource(failResolve: duringResolution, failModels: !duringResolution));
        var envelope = await service.ModelsListAsync(new TaskQueryRequest(SnapshotId: "synthetic", Generation: 1), CancellationToken.None);
        Assert.Equal("unavailable", envelope.Status);
        Assert.Equal(503, TaskQueryStatusCodes.HttpStatus(envelope.Status));
        Assert.Equal("not-established", envelope.Coverage.Completeness);
        Assert.DoesNotContain(envelope.Coverage.Repositories, repo => repo.Status == "full");
        var serialized = JsonSerializer.SerializeToElement(envelope, TaskQueryJson.Options);
        Assert.DoesNotContain("private-storage-path", serialized.GetRawText(), StringComparison.Ordinal);
        Assert.True(TaskQueryTestHost.LoadEnvelopeSchema().Evaluate(serialized).IsValid);
    }

    [Fact]
    public async Task Oversized_cited_git_content_returns_a_typed_limit_instead_of_missing()
    {
        var root = Directory.CreateDirectory(Path.Combine(Fixtures.RepositoryRoot, ".sandbox-cache", "task-query-oversized-git",
            Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var repo = Path.Combine(root, "svc-a");
            Directory.CreateDirectory(repo);
            RunGit(repo, "init");
            RunGit(repo, "config", "user.name", "Synthetic test");
            RunGit(repo, "config", "user.email", "test@example.invalid");
            const string path = "source.txt";
            await File.WriteAllTextAsync(Path.Combine(repo, path), new string('x', 16 * 1024 * 1024 + 1));
            RunGit(repo, "add", path);
            RunGit(repo, "commit", "-m", "oversized source");
            var commit = RunGit(repo, "rev-parse", "HEAD");
            var source = new OversizedGitTaskEvidenceSource(new GitSource(id => id == "svc-a" ? repo : null), commit);
            var service = new TaskQueryService(source, new TaskQueryAccessPolicy(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskQueries:Operations:0"] = TaskQueryOperations.SourceExcerpt,
                    ["TaskQueries:Repositories:0"] = "svc-a",
                }).Build()), new TaskQueryCursorCodec(), NullLogger<TaskQueryService>.Instance);

            var envelope = await service.SourceExcerptAsync(new TaskQueryRequest(
                SnapshotId: "synthetic", Generation: 1, Repo: "svc-a", Commit: commit, Path: path, Start: 1, End: 1), CancellationToken.None);

            Assert.Equal("limit", envelope.Status);
            Assert.Equal("source-content-too-large", envelope.Reason);
            Assert.Equal(413, TaskQueryStatusCodes.HttpStatus(envelope.Status));
            Assert.Equal("not-established", envelope.Coverage.Completeness);
            Assert.True(TaskQueryTestHost.LoadEnvelopeSchema().Evaluate(JsonSerializer.SerializeToElement(envelope, TaskQueryJson.Options)).IsValid);
        }
        finally
        {
            Directory.Delete(root, true);
        }

        static string RunGit(string repository, params string[] args)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = repository, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git for the synthetic repository.");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, error);
            return output.Trim();
        }
    }

    [Fact]
    public async Task A_capped_evidence_scan_has_unknown_total_and_a_model_beyond_the_head_cap_is_not_falsely_missing()
    {
        var source = new SyntheticIntegrationSource(truncated: true);
        var service = ServiceFor(source);
        var evidence = await service.EvidenceListAsync(new TaskQueryRequest(SnapshotId: "synthetic", Generation: 1, Key: "key"), CancellationToken.None);
        Assert.Equal("partial", evidence.Status);
        Assert.Equal("unknown", evidence.Page!.Total);
        Assert.Equal("not-established", evidence.Coverage.Completeness);
        var reference = await service.DecisionReferencesAsync(new TaskQueryRequest(
            SnapshotId: "synthetic", Generation: 1, ModelId: "model:" + new string('a', 64)), CancellationToken.None);
        Assert.Equal("partial", reference.Status);
        Assert.Equal("model-coverage-limited", reference.Reason);
        Assert.Equal(0, source.ContentReads);
        Assert.True(TaskQueryTestHost.LoadEnvelopeSchema().Evaluate(JsonSerializer.SerializeToElement(reference, TaskQueryJson.Options)).IsValid);
    }

    private static TaskQueryService ServiceFor(SyntheticIntegrationSource source) => new(source,
        new TaskQueryAccessPolicy(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskQueries:Operations:0"] = TaskQueryOperations.ModelsList,
            ["TaskQueries:Operations:1"] = TaskQueryOperations.EvidenceList,
            ["TaskQueries:Operations:2"] = TaskQueryOperations.DecisionReferences,
            ["TaskQueries:Repositories:0"] = "svc-a",
        }).Build()), new TaskQueryCursorCodec(), NullLogger<TaskQueryService>.Instance);

    [Fact]
    public async Task Invalid_target_and_missing_pin_are_rejected_without_reading_snapshot_metadata()
    {
        var source = new SyntheticIntegrationSource(failResolve: true);
        var service = ServiceFor(source);
        var invalidTarget = await service.DecisionReferencesAsync(new TaskQueryRequest(
            SnapshotId: "synthetic", Generation: 1, ModelId: "model:" + new string('g', 64)), CancellationToken.None);
        Assert.Equal("invalid-model-id", invalidTarget.Reason);
        var missingPin = await service.ModelsListAsync(new TaskQueryRequest(), CancellationToken.None);
        Assert.Equal("snapshot-required", missingPin.Reason);
        Assert.Equal(0, source.ResolveReads);
    }

    [Fact]
    public async Task A_resolved_file_scope_keeps_its_models_when_the_live_state_reloads()
    {
        await using var host = await TaskQueryTestHost.CreateAsync();
        var source = new FileTaskEvidenceSource(host.State);
        var scope = await source.ResolveAsync("*", new Dictionary<string, string>(), null, null, CancellationToken.None);
        Assert.NotNull(scope);
        var before = await source.ModelsAsync(scope, null, CancellationToken.None);
        Assert.Contains(before.Heads, head => head.Id == host.BpmnModelId);
        var plan = JsonSerializer.Deserialize<CollectionPlan>(await File.ReadAllTextAsync(Path.Combine(host.Root, "plan.json")), JsonSerializerOptions.Web)!;
        var replacementPlan = Path.Combine(host.Root, "replacement-plan.json");
        await File.WriteAllTextAsync(replacementPlan, JsonSerializer.Serialize(
            plan with { Inputs = plan.Inputs.Where(input => input.UnitPath.EndsWith("pricing.dmn", StringComparison.Ordinal)).ToList() },
            JsonSerializerOptions.Web));
        var replacement = Path.Combine(host.Root, "replacement-bundle");
        Assert.True((await CollectionExporter.ExportAsync(replacementPlan, replacement)).IsValid);
        Directory.Move(Path.Combine(host.Root, "bundle"), Path.Combine(host.Root, "retired-bundle"));
        Directory.Move(replacement, Path.Combine(host.Root, "bundle"));
        host.State.Refresh();
        Assert.NotEqual(scope.Generation, host.State.Current.Generation);
        Assert.DoesNotContain(host.State.Current.Models.Products, model => model.Id == host.BpmnModelId);
        var after = await source.ModelsAsync(source.Narrow(scope, scope.Selection), null, CancellationToken.None);
        Assert.Equal(before.Heads, after.Heads);
        Assert.NotNull(await source.ModelAsync(scope, host.BpmnModelId, CancellationToken.None));
    }

    private sealed class OversizedGitTaskEvidenceSource(GitSource git, string commit) : ITaskEvidenceSource
    {
        private readonly IReadOnlyDictionary<string, string> _selection = new Dictionary<string, string> { ["svc-a"] = commit };

        public string Mode => "files";
        public Task<TaskSnapshotScope?> ResolveAsync(string cap, IReadOnlyDictionary<string, string> specifiedView,
            string? snapshotId, long? generation, CancellationToken ct) =>
            Task.FromResult<TaskSnapshotScope?>(new TaskSnapshotScope(snapshotId ?? "synthetic", generation ?? 1,
                _selection, true, snapshotId ?? "synthetic", new object()));
        public TaskSnapshotScope Narrow(TaskSnapshotScope scope, IReadOnlyDictionary<string, string> allowed) =>
            scope with { Selection = allowed };
        public Task<TaskModelPage> ModelsAsync(TaskSnapshotScope scope, string? kind, CancellationToken ct) =>
            Task.FromResult(new TaskModelPage([], false));
        public Task<ModelProduct?> ModelAsync(TaskSnapshotScope scope, string modelId, CancellationToken ct) =>
            Task.FromResult<ModelProduct?>(null);
        public Task<Integrations> IntegrationsAsync(TaskSnapshotScope scope, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<TaskEvidencePage> EvidenceAsync(TaskSnapshotScope scope, string key, CancellationToken ct) =>
            Task.FromResult(new TaskEvidencePage([], false));
        public Task<bool> IsCitedAsync(TaskSnapshotScope scope, string repositoryId, string commitSha, string path, CancellationToken ct) =>
            Task.FromResult(repositoryId == "svc-a" && commitSha == commit);
        public Task<string?> ReadCitedFileAsync(TaskSnapshotScope scope, string repositoryId, string commitSha, string path, CancellationToken ct) =>
            git.ReadTaskQueryFileAsync(repositoryId, commitSha, path, ct);
    }
}
