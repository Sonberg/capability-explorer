using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CapabilityExplorer.Capabilities.Analysis;
using CapabilityExplorer.Explorer;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class SourceCheckApiTests
{
    [Fact]
    public async Task Check_is_accepted_with_status_location_duplicate_identity_and_explicit_cancellation()
    {
        using var factory = new CapabilityExplorerFactory();
        using var client = factory.CreateClient();
        var first = await client.PostAsync(new Uri("/api/capabilities/demand-pricing/check", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var check = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("queued", check.GetProperty("state").GetString());
        Assert.False(check.GetProperty("duplicate").GetBoolean());
        Assert.Equal(64, check.GetProperty("configurationHash").GetString()!.Length);
        Assert.False(check.TryGetProperty("configurationPath", out _));
        Assert.False(check.TryGetProperty("gitHubAuth", out _));
        var duplicate = await client.PostAsync(new Uri("/api/capabilities/demand-pricing/check", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.Accepted, duplicate.StatusCode);
        var again = await duplicate.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(again.GetProperty("duplicate").GetBoolean());
        Assert.Equal(check.GetProperty("requestId").GetString(), again.GetProperty("requestId").GetString());
        Assert.Equal(check.GetProperty("statusUrl").GetString(), first.Headers.Location?.OriginalString);

        var status = await client.GetFromJsonAsync<JsonElement>(first.Headers.Location);
        Assert.Equal("queued", status.GetProperty("state").GetString());
        var cancelled = await client.PostAsync(new Uri(first.Headers.Location!.OriginalString + "/cancel", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Equal("cancelled", (await cancelled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("check").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Worker_completion_is_visible_without_restarting_and_late_cancel_is_explicit()
    {
        using var factory = new CapabilityExplorerFactory();
        using var client = factory.CreateClient();
        var accepted = await client.PostAsync(new Uri("/api/capabilities/demand-pricing/check", UriKind.Relative), null);
        var before = await client.GetFromJsonAsync<JsonElement>("/api/capabilities/demand-pricing");
        Assert.Equal("queued", before.GetProperty("check").GetProperty("state").GetString());
        Assert.False(before.TryGetProperty("checkedAt", out _));
        var state = factory.Services.GetRequiredService<ExplorerState>();
        state.Refresh();
        var generation = state.Current.Generation;
        factory.CompleteFixtureCheck();
        state.Refresh();
        Assert.True(state.Current.Generation > generation);
        var after = await client.GetFromJsonAsync<JsonElement>("/api/capabilities/demand-pricing");
        Assert.Equal("done", after.GetProperty("check").GetProperty("state").GetString());
        Assert.Equal(Fixtures.Lock["forecast-cs"]["v1-base"], after.GetProperty("repositories")[0].GetProperty("lockedSha").GetString());
        var cancelled = await client.PostAsync(new Uri(accepted.Headers.Location!.OriginalString + "/cancel", UriKind.Relative), null);
        Assert.Equal("too-late", (await cancelled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Definition_change_marks_result_stale_and_never_reuses_old_locks_or_scope()
    {
        using var factory = new CapabilityExplorerFactory();
        using var client = factory.CreateClient();
        var before = await client.GetFromJsonAsync<JsonElement>("/api/capabilities/demand-pricing");
        var requestId = before.GetProperty("check").GetProperty("requestId").GetString();
        var definition = await File.ReadAllTextAsync(factory.ConfigurationPath);
        await File.WriteAllTextAsync(factory.ConfigurationPath, definition.Replace("v1-base", "v2-contract", StringComparison.Ordinal));
        factory.Services.GetRequiredService<ExplorerState>().Refresh();
        var stale = await client.GetFromJsonAsync<JsonElement>("/api/capabilities/demand-pricing");
        Assert.True(stale.GetProperty("checkStale").GetBoolean());
        Assert.False(stale.TryGetProperty("checkedAt", out _));
        Assert.All(stale.GetProperty("repositories").EnumerateArray(), r => Assert.False(r.TryGetProperty("lockedSha", out _)));
        var historic = await client.GetFromJsonAsync<JsonElement>($"/api/source-checks/{requestId}");
        Assert.Equal("done", historic.GetProperty("state").GetString());
        Assert.True(historic.GetProperty("stale").GetBoolean());
        var view = await client.GetFromJsonAsync<Dictionary<string, string>>("/api/view?cap=demand-pricing");
        Assert.Equal(Fixtures.Lock["forecast-cs"]["v2-contract"], view!["forecast-cs"]);
    }

    [Theory]
    [InlineData(true, "failed")]
    [InlineData(false, "unknown")]
    public async Task Worker_failure_is_not_projected_as_a_success(bool terminationConfirmed, string expected)
    {
        using var factory = new CapabilityExplorerFactory();
        using var client = factory.CreateClient();
        var accepted = await client.PostAsync(new Uri("/api/capabilities/demand-pricing/check", UriKind.Relative), null);
        var journal = new FileRunStore(factory.RequestsDirectory);
        using var owner = journal.AcquireWorker();
        var check = journal.ClaimCheck(owner)!;
        journal.FailCheck(owner, new AttemptIdentity(check.RequestId, check.AttemptId!), terminationConfirmed);
        var status = await client.GetFromJsonAsync<JsonElement>(accepted.Headers.Location);
        Assert.Equal(expected, status.GetProperty("state").GetString());
        var capability = await client.GetFromJsonAsync<JsonElement>("/api/capabilities/demand-pricing");
        Assert.False(capability.TryGetProperty("checkedAt", out _));
    }

    [Fact]
    public async Task Admission_failure_reports_persisted_capability_instead_of_claiming_creation_failed()
    {
        using var factory = new CapabilityExplorerFactory();
        using var client = factory.CreateClient();
        await client.PostAsync(new Uri("/api/capabilities/demand-pricing/check", UriKind.Relative), null);
        var journal = new FileRunStore(factory.RequestsDirectory);
        using var owner = journal.AcquireWorker();
        var check = journal.ClaimCheck(owner)!;
        journal.FailCheck(owner, new AttemptIdentity(check.RequestId, check.AttemptId!));
        var response = await client.PostAsJsonAsync("/api/capabilities", new { name = "Persisted only", repositories = Fixtures.RepoDirectory("web-ts") });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("created").GetBoolean());
        Assert.False(body.GetProperty("queued").GetBoolean());
        Assert.Equal("persisted-only", body.GetProperty("capability").GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/api/capabilities/persisted-only", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Unknown_identifiers_are_bounded_errors()
    {
        using var factory = new CapabilityExplorerFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(new Uri("/api/source-checks/not-an-id", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(new Uri("/api/capabilities/nope/check", UriKind.Relative), null)).StatusCode);
    }
}
