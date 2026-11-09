using System.Net;
using System.Diagnostics;
using System.Text.Json;
using CapabilityExplorer.Explorer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CapabilityExplorer.Tests;

/// <summary>Runs the explorer in-process over the golden artifacts and the fixture repos.</summary>
public sealed class ExplorerFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("artifacts", Fixtures.ExpectedDirectory);
        builder.UseSetting("repos", Path.Combine(Fixtures.RepositoryRoot, "fixtures", ".repos"));
    }
}

/// <summary>Each test checks one thing the UI promises to show, using the planted fixture scenarios.</summary>
public sealed class ExplorerApiTests(ExplorerFactory factory) : IClassFixture<ExplorerFactory>
{
    private const string ApiCalcV1 = "cs|api-cs|Sales.Api|M:Sales.Api.Calculations.WeeklySalesCalculator.AveragePerCalendarWeek(System.Collections.Generic.IReadOnlyList{Sales.Domain.SaleRow},System.DateOnly,System.DateOnly)";
    private const string ApiCalcV2 = "cs|api-cs|Sales.Api|M:Sales.Api.Calculations.WeeklySalesCalculator.CalendarWeekAverage(System.Collections.Generic.IReadOnlyList{Sales.Domain.SaleRow},System.DateOnly,System.DateOnly)";
    private const string WorkerCalc = "cs|worker-cs|Sales.Worker|M:Sales.Worker.Calculations.ArticleStatsCalculator.AveragePerActiveWeek(System.Collections.Generic.IReadOnlyCollection{Sales.Worker.Calculations.SaleRecord})";

    private readonly HttpClient _client = factory.CreateClient();

    private async Task<JsonElement> Get(string path)
    {
        var response = await _client.GetAsync(new Uri(path, UriKind.Relative));
        Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static string Q(string s) => Uri.EscapeDataString(s);
    private static JsonElement Integrations(string? view = null) => JsonSerializer.SerializeToElement(
        Queries.Integrations(ExplorerIndex.Load([Fixtures.ExpectedDirectory], new GitSource(Fixtures.RepoDirectory)).View(view)),
        JsonSerializerOptions.Web);

    [Fact]
    public async Task Legacy_file_mode_reports_database_required_instead_of_falling_back_for_integrations_and_marks()
    {
        foreach (var endpoint in new[] { "integrations", "deprecations" })
        {
            using var response = await _client.GetAsync(new Uri($"/api/{endpoint}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("database", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Meta_lists_every_revision_with_its_tag_and_defaults_to_the_newest()
    {
        var meta = await Get("/api/meta");
        Assert.Equal(Fixtures.Lock.Sum(r => r.Value.Count), meta.GetProperty("revisions").GetArrayLength());
        Assert.Contains(meta.GetProperty("revisions").EnumerateArray(), r => r.GetProperty("label").GetString() == "v2-rename");
        Assert.Equal(Fixtures.Lock["api-cs"]["v3-filter"], meta.GetProperty("defaultView").GetProperty("api-cs").GetString());
        Assert.Empty(meta.GetProperty("problems").EnumerateArray());
    }

    [Fact]
    public async Task Live_reload_exposes_snapshot_identity_even_when_restart_generation_is_unchanged()
    {
        var meta = await Get("/api/meta");
        var poll = await Get("/api/generation");
        Assert.Equal(meta.GetProperty("generation").GetInt64(), poll.GetProperty("generation").GetInt64());
        Assert.Equal(meta.GetProperty("snapshotId").GetGuid(), poll.GetProperty("snapshotId").GetGuid());
    }

    [Fact]
    public async Task Search_finds_implementations_across_repositories()
    {
        var hits = await Get("/api/search?q=averagePerActiveWeek");
        var repos = hits.EnumerateArray().Where(h => h.GetProperty("hasCalculation").GetBoolean()).Select(h => h.GetProperty("repositoryId").GetString()).ToHashSet();
        Assert.Equal(["web-ts", "worker-cs"], repos.Order());
    }

    [Fact]
    public async Task Search_supports_cursor_pagination_with_stable_ordering()
    {
        var first = await Get("/api/search?q=a&limit=1");
        var firstItems = first.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(firstItems);
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));
        var total = first.GetProperty("total").GetInt32();
        Assert.True(total > 1);

        var snapshot = first.GetProperty("snapshotId").GetString()!;
        var second = await Get($"/api/search?q=a&limit=1&snapshot={snapshot}&cursor={Q(cursor!)}");
        var secondItems = second.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(secondItems);
        Assert.NotEqual(firstItems[0].GetProperty("key").GetString(), secondItems[0].GetProperty("key").GetString());
        Assert.Equal(total, second.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Paged_search_traverses_every_result_once_in_stable_duplicate_name_order()
    {
        var meta = await Get("/api/meta");
        var snapshot = meta.GetProperty("snapshotId").GetString();
        var current = factory.Services.GetRequiredService<ExplorerState>().Current;
        var expected = current.Index.View(null, current.Capabilities.Scope(null)).Nodes.Values
            .Select(nodes => nodes[0].Record)
            .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.SymbolKey, StringComparer.Ordinal).ToList();
        Assert.True(expected.Count > 100);
        Assert.Contains(expected.GroupBy(x => x.Name), group => group.Count() > 1);
        var keys = new List<string>();
        string? cursor = null;
        var pageCount = 0;
        do
        {
            var page = await Get($"/api/search?limit=17&snapshot={snapshot}" + (cursor is null ? "" : $"&cursor={Q(cursor)}"));
            Assert.Equal(snapshot, page.GetProperty("snapshotId").GetString());
            Assert.Equal(expected.Count, page.GetProperty("total").GetInt32());
            var items = page.GetProperty("items").EnumerateArray().ToArray();
            Assert.InRange(items.Length, 1, 17);
            keys.AddRange(items.Select(x => x.GetProperty("key").GetString()!));
            cursor = page.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
            Assert.True(++pageCount <= expected.Count);
        } while (cursor is not null);
        Assert.Equal(expected.Select(x => x.SymbolKey), keys);
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("q=changed&limit=1")]
    [InlineData("q=a&limit=2")]
    [InlineData("q=a&kind=Method&limit=1")]
    [InlineData("q=a&repo=api-cs&limit=1")]
    [InlineData("q=a&view=api-cs:v1-base&limit=1")]
    public async Task Search_rejects_cursor_reuse_under_changed_filters(string query)
    {
        var first = await Get("/api/search?q=a&limit=1");
        var cursor = Q(first.GetProperty("nextCursor").GetString()!);
        var snapshot = first.GetProperty("snapshotId").GetString();
        using var response = await _client.GetAsync(new Uri($"/api/search?{query}&snapshot={snapshot}&cursor={cursor}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("invalid-cursor", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Search_rejects_tampered_unpinned_and_stale_continuations()
    {
        var first = await Get("/api/search?q=a&limit=1");
        var cursor = first.GetProperty("nextCursor").GetString()!;
        var snapshot = first.GetProperty("snapshotId").GetString();
        foreach (var suffix in new[] { $"snapshot={snapshot}&cursor={Q("x" + cursor)}", $"cursor={Q(cursor)}" })
        {
            using var invalid = await _client.GetAsync(new Uri($"/api/search?q=a&limit=1&{suffix}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using var stale = await _client.GetAsync(new Uri($"/api/search?q=a&limit=1&snapshot={Guid.NewGuid()}&cursor={Q(cursor)}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var problem = JsonDocument.Parse(await stale.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("stale-snapshot", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Symbol_detail_has_edges_calculation_evidence_and_history()
    {
        var d = await Get($"/api/symbol?key={Q(WorkerCalc)}");
        Assert.Equal("AveragePerActiveWeek", d.GetProperty("node").GetProperty("name").GetString());
        Assert.Contains(d.GetProperty("incoming").EnumerateArray(), e => e.GetProperty("from").GetProperty("name").GetString() == "ExecuteAsync");
        Assert.Contains(d.GetProperty("outgoing").EnumerateArray(), e => e.GetProperty("to").GetProperty("key").GetString()!.Contains("MidpointRounding", StringComparison.Ordinal));
        Assert.Equal("data", d.GetProperty("calculation").GetProperty("attributes").GetProperty("denominatorSource").GetProperty("value").GetString());
        Assert.All(d.GetProperty("history").EnumerateArray(), h => Assert.Equal("present", h.GetProperty("state").GetString()));
    }

    [Fact]
    public async Task Renamed_symbol_is_absent_in_later_revisions_and_reachable_through_the_view()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(new Uri($"/api/symbol?key={Q(ApiCalcV1)}", UriKind.Relative))).StatusCode);

        var v1 = await Get($"/api/symbol?key={Q(ApiCalcV1)}&view={Q("api-cs:v1-base")}");
        var states = v1.GetProperty("history").EnumerateArray().ToDictionary(h => h.GetProperty("label").GetString()!, h => h.GetProperty("state").GetString());
        Assert.Equal("present", states["v1-base"]);
        Assert.Equal("absent", states["v2-rename"]);
    }

    [Fact]
    public async Task Source_is_served_for_cited_files_only()
    {
        var sha = Fixtures.Lock["api-cs"]["v1-base"];
        var src = await Get($"/api/source?repo=api-cs&commit={sha}&path={Q("src/Sales.Api/Calculations/WeeklySalesCalculator.cs")}&start=18&end=18");
        Assert.Contains(src.GetProperty("lines").EnumerateArray(), l => l.GetProperty("text").GetString()!.Contains("Math.Round(total / weeks, 2)", StringComparison.Ordinal));
        Assert.Equal(18, src.GetProperty("highlightStart").GetInt32());

        var uncited = await _client.GetAsync(new Uri($"/api/source?repo=api-cs&commit={sha}&path={Q(".gitignore")}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, uncited.StatusCode);
        var traversal = await _client.GetAsync(new Uri($"/api/source?repo={Q("../api-cs")}&commit={sha}&path={Q("README.md")}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, traversal.StatusCode);
    }

    [Fact]
    public async Task Oversized_legacy_source_read_remains_unavailable_instead_of_throwing()
    {
        var repository = Path.Combine(AppContext.BaseDirectory, $"oversized-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repository);
        try
        {
            await RunGit(repository, "init", "-q");
            await RunGit(repository, "config", "user.name", "Explorer test");
            await RunGit(repository, "config", "user.email", "explorer-test@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(repository, "large.txt"), new string('x', 2 * 1024 * 1024 + 1));
            await RunGit(repository, "add", "large.txt");
            await RunGit(repository, "commit", "-qm", "oversized source fixture");
            var commit = (await RunGit(repository, "rev-parse", "HEAD")).Trim();
            var source = new GitSource(id => id == "legacy" ? repository : null);

            Assert.Null(await source.ReadFileAsync("legacy", commit, "large.txt"));
            await Assert.ThrowsAsync<GitSourceContentLimitException>(
                () => source.ReadTaskQueryFileAsync("legacy", commit, "large.txt"));
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }

        static async Task<string> RunGit(string directory, params string[] arguments)
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var stderr = await error;
            Assert.True(process.ExitCode == 0, stderr);
            return await output;
        }
    }

    [Fact]
    public void Integration_projection_links_follow_the_selected_revisions()
    {
        static string? Match(JsonElement integrations, string client) => integrations.GetProperty("http").EnumerateArray()
            .Single(l => l.GetProperty("request").GetProperty("from").GetProperty("name").GetString() == client).GetProperty("match").GetString();

        // forecast-cs v2-contract moved its routes: pricing-node's call has no endpoint.
        Assert.Equal("none", Match(Integrations(), "getForecast"));
        // With forecast-cs v1-base selected, the same call has a route candidate.
        Assert.Equal("route", Match(Integrations("forecast-cs:v1-base"), "getForecast"));
        Assert.Equal("route", Match(Integrations(), "fetchWeeklySales"));
        var integrations = Integrations();
        var weeklySales = integrations.GetProperty("http").EnumerateArray().Single(l => l.GetProperty("request").GetProperty("from").GetProperty("name").GetString() == "fetchWeeklySales");
        Assert.Equal("src/api/salesClient.ts", weeklySales.GetProperty("sourcePath").GetString());
    }

    [Fact]
    public void Integration_projection_groups_same_named_channels_but_keeps_them_apart()
    {
        var channel = Integrations().GetProperty("channels").EnumerateArray().Single(g => g.GetProperty("name").GetString() == "article-stats-updated");
        var keys = channel.GetProperty("keys").EnumerateArray().ToList();
        Assert.Equal(3, keys.Count);
        Assert.Single(keys, k => k.GetProperty("bound").GetBoolean());
        Assert.False(string.IsNullOrEmpty(channel.GetProperty("note").GetString()));
    }

    [Fact]
    public async Task Calculations_list_every_variant_in_the_view()
    {
        var calcs = (await Get("/api/calculations")).EnumerateArray().ToList();
        Assert.Equal(10, calcs.Count);
        var rounding = calcs.Select(c => c.GetProperty("attributes").GetProperty("rounding").GetProperty("value").GetString()).ToHashSet();
        Assert.Superset(new HashSet<string?> { "halfEven", "halfAwayFromZero", "halfTowardPositiveInfinity", "truncate" }, rounding);
    }

    [Fact]
    public async Task Rename_shows_as_continuity_with_unchanged_logic()
    {
        var d = await Get("/api/changes?repo=api-cs&from=v1-base&to=v2-rename");
        var api = d.GetProperty("units").EnumerateArray().Single(u => u.GetProperty("unit").GetString() == "Sales.Api");
        var candidate = api.GetProperty("continuity").EnumerateArray().Single(c => c.GetProperty("from").GetProperty("key").GetString() == ApiCalcV1);
        Assert.Equal(ApiCalcV2, candidate.GetProperty("to").GetProperty("key").GetString());
        Assert.Contains("identical normalised calculation", candidate.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
        Assert.Empty(api.GetProperty("calculations").EnumerateArray());
        Assert.DoesNotContain(api.GetProperty("removed").EnumerateArray(), n => n.GetProperty("key").GetString() == ApiCalcV1);
        Assert.True(d.GetProperty("units").EnumerateArray().Single(u => u.GetProperty("unit").GetString() == "Sales.Domain").GetProperty("inputUnchanged").GetBoolean());
    }

    [Fact]
    public async Task Filter_change_shows_logic_change_and_possible_consumers()
    {
        var d = await Get("/api/changes?repo=api-cs&from=v2-rename&to=v3-filter");
        var change = d.GetProperty("units").EnumerateArray().SelectMany(u => u.GetProperty("calculations").EnumerateArray()).Single();
        Assert.True(change.GetProperty("logicChanged").GetBoolean());
        Assert.Equal("filters", change.GetProperty("attributes").EnumerateArray().Single().GetProperty("name").GetString());
        var consumers = change.GetProperty("possibleConsumers").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Contains("GetWeeklySales", consumers);
        Assert.Contains(consumers, c => c!.Contains("weekly-sales", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Partial_analysis_never_reports_removals()
    {
        var d = await Get("/api/changes?repo=worker-cs&from=v1-base&to=v2-broken");
        var unit = d.GetProperty("units").EnumerateArray().Single();
        Assert.Equal("partial", unit.GetProperty("toStatus").GetString());
        Assert.Empty(unit.GetProperty("removed").EnumerateArray());
        Assert.Equal(2, unit.GetProperty("added").GetArrayLength());
    }

    [Fact]
    public async Task Move_behind_re_export_is_a_same_name_continuity_candidate()
    {
        var d = await Get("/api/changes?repo=pricing-node&from=v1-base&to=v2-move");
        var c = d.GetProperty("units").EnumerateArray().SelectMany(u => u.GetProperty("continuity").EnumerateArray()).Single();
        Assert.EndsWith("demand.ts|priceForDemand#value", c.GetProperty("to").GetProperty("key").GetString(), StringComparison.Ordinal);
        var reasons = c.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.Contains("identical normalised calculation", reasons);
        Assert.Contains("same name (moved)", reasons);
    }

    [Fact]
    public async Task Artifacts_and_ui_are_served()
    {
        Assert.Equal(18, (await Get("/api/artifacts")).GetArrayLength());
        using var indexResponse = await _client.GetAsync(new Uri("/", UriKind.Relative));
        Assert.True(indexResponse.Headers.CacheControl?.NoCache);
        var index = await indexResponse.Content.ReadAsStringAsync();
        Assert.Contains("<title>Capability Explorer</title>", index, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(new Uri("/api/evaluation", UriKind.Relative))).StatusCode);

        // Stable asset URLs must revalidate so a refreshed page does not mix UI protocol versions.
        var assets = System.Text.RegularExpressions.Regex.Matches(index, "(?:src|href)=\"(/assets/[^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.Contains(assets, a => a.EndsWith(".js", StringComparison.Ordinal));
        Assert.Contains(assets, a => a.EndsWith(".css", StringComparison.Ordinal));
        foreach (var asset in assets)
        {
            using var response = await _client.GetAsync(new Uri(asset, UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoCache);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.InRange(bytes.Length, 1, 600_000);
        }
    }
}
