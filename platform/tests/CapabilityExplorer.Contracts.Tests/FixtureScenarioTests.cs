using System.Text.Json;
using CapabilityExplorer.Contracts;
using Xunit;

namespace CapabilityExplorer.Tests;

/// <summary>
/// The fixtures exist to plant specific situations for later stages. These tests pin the golden facts that
/// encode them, so a fixture edit cannot silently remove a scenario.
/// </summary>
public sealed class FixtureScenarioTests
{
    private const string CalendarV1 = "cs|api-cs|Sales.Api|M:Sales.Api.Calculations.WeeklySalesCalculator.AveragePerCalendarWeek(System.Collections.Generic.IReadOnlyList{Sales.Domain.SaleRow},System.DateOnly,System.DateOnly)";
    private const string CalendarV2 = "cs|api-cs|Sales.Api|M:Sales.Api.Calculations.WeeklySalesCalculator.CalendarWeekAverage(System.Collections.Generic.IReadOnlyList{Sales.Domain.SaleRow},System.DateOnly,System.DateOnly)";

    private static Artifact A(string file) => GoldenArtifactTests.Valid(file);

    private static JsonElement Calc(Artifact a, string symbolKey) => a.Calculations.Single(c => c.SymbolKey == symbolKey).Raw;

    private static string Attr(JsonElement calc, string name) =>
        calc.GetProperty("attributes").GetProperty(name) is var a && a.TryGetProperty("value", out var v) ? v.ToString() : $"<{a.GetProperty("status").GetString()}>";

    [Fact]
    public void Rename_changes_the_symbol_key_but_not_the_calculation()
    {
        var v1 = A("api-cs/v1-base/Sales.Api.jsonl");
        var v2 = A("api-cs/v2-rename/Sales.Api.jsonl");

        Assert.Contains(v1.Nodes, n => n.SymbolKey == CalendarV1);
        Assert.DoesNotContain(v2.Nodes, n => n.SymbolKey == CalendarV1);
        Assert.Contains(v2.Nodes, n => n.SymbolKey == CalendarV2);

        // Identical normalised IR: a lineage matcher must report a rename/move, not a definition change.
        Assert.Equal(Calc(v1, CalendarV1).GetProperty("irHash").GetString(), Calc(v2, CalendarV2).GetProperty("irHash").GetString());

        var paths = (Artifact a, string key) => a.Evidence
            .Where(e => a.Nodes.Single(n => n.SymbolKey == key).Evidence.Contains(e.Id))
            .Select(e => e.Path).Single();
        Assert.Equal("src/Sales.Api/Calculations/WeeklySalesCalculator.cs", paths(v1, CalendarV1));
        Assert.Equal("src/Sales.Api/Calculations/Weekly/WeeklySalesCalculator.cs", paths(v2, CalendarV2));
    }

    [Fact]
    public void Filter_change_changes_the_calculation_and_its_filter_attribute()
    {
        var before = Calc(A("api-cs/v2-rename/Sales.Api.jsonl"), CalendarV2);
        var after = Calc(A("api-cs/v3-filter/Sales.Api.jsonl"), CalendarV2);

        Assert.NotEqual(before.GetProperty("irHash").GetString(), after.GetProperty("irHash").GetString());
        Assert.Equal("soldOn in [from,to]", Attr(before, "filters"));
        Assert.Equal("soldOn in [from,to] and not isReturn", Attr(after, "filters"));
        foreach (var unchanged in new[] { "rounding", "denominatorSource", "nullHandling", "timeBucket" })
        {
            Assert.Equal(Attr(before, unchanged), Attr(after, unchanged));
        }
    }

    [Fact]
    public void Possible_consumers_of_the_changed_calculation_are_reachable_through_static_edges()
    {
        var api = A("api-cs/v3-filter/Sales.Api.jsonl");
        var iface = api.Edges.Single(e => e.Relation == "IMPLEMENTS" && e.From == CalendarV2).To;
        var callers = api.Edges.Where(e => e.Relation == "CALLS" && e.To == iface).ToList();
        var caller = Assert.Single(callers);
        Assert.Equal("static-target", caller.Resolution); // interface dispatch: proven static target, not runtime target
        Assert.Contains(api.Edges, e => e.Relation == "HANDLES" && e.From == caller.From);
    }

    [Fact]
    public void Unchanged_units_keep_their_input_hash_across_revisions()
    {
        foreach (var unit in new[] { "Sales.Domain", "SalesDb" })
        {
            var hashes = new[] { "v1-base", "v2-rename", "v3-filter" }.Select(tag => A($"api-cs/{tag}/{unit}.jsonl").Header.InputHash).Distinct();
            Assert.Single(hashes);
        }

        Assert.Equal(3, new[] { "v1-base", "v2-rename", "v3-filter" }.Select(tag => A($"api-cs/{tag}/Sales.Api.jsonl").Header.InputHash).Distinct().Count());
    }

    [Fact]
    public void Broken_project_is_partial_and_loses_no_symbols()
    {
        var baseline = A("worker-cs/v1-base/Sales.Worker.jsonl");
        var broken = A("worker-cs/v2-broken/Sales.Worker.jsonl");

        Assert.Equal("full", baseline.Complete.Status);
        Assert.Equal("partial", broken.Complete.Status);
        Assert.Equal(["src/Sales.Worker/Calculations/CurrencyNormalizer.cs"], broken.Complete.Coverage.IncompletePaths);
        Assert.Contains(broken.Diagnostics, d => d.Code == "MISSING_PROJECT_REFERENCE" && d.AffectsCoverage == true);
        Assert.Empty(baseline.Nodes.Select(n => n.SymbolKey).Except(broken.Nodes.Select(n => n.SymbolKey)));
        Assert.Contains(broken.Edges, e => e.Resolution == "unresolved" && e.UnresolvedReason == "missing-reference" && e.To is null);
    }

    public static TheoryData<string, string, string, string, string, string> Variants => new()
    {
        // file, symbol, denominatorSource, timeBucket, rounding, nullHandling
        { "api-cs/v1-base/Sales.Api.jsonl", CalendarV1, "period", "isoWeek", "halfEven", "nullAsZero" },
        { "api-cs/v1-base/SalesDb.jsonl", "sql|@unbound:api-cs/SalesDb:dbproject=SalesDb|dbo.fnWeeklySalesAverage", "period", "sevenDayFromPeriodStart", "halfAwayFromZero", "nullAsZero" },
        { "worker-cs/v1-base/Sales.Worker.jsonl", "cs|worker-cs|Sales.Worker|M:Sales.Worker.Calculations.ArticleStatsCalculator.AveragePerActiveWeek(System.Collections.Generic.IReadOnlyCollection{Sales.Worker.Calculations.SaleRecord})", "data", "isoWeek", "halfAwayFromZero", "excluded" },
        { "web-ts/v1-base/_fixture_web.jsonl", "ts|web-ts|@fixture/web|src/sales/weeklyStats.ts|averagePerActiveWeek#value", "data", "isoWeek", "halfTowardPositiveInfinity", "excluded" },
    };

    [Theory]
    [MemberData(nameof(Variants))]
    public void Weekly_average_variants_differ_as_planted(string file, string symbol, string denominator, string bucket, string rounding, string nulls)
    {
        var calc = Calc(A(file), symbol);
        Assert.Equal(denominator, Attr(calc, "denominatorSource"));
        Assert.Equal(bucket, Attr(calc, "timeBucket"));
        Assert.Equal(rounding, Attr(calc, "rounding"));
        Assert.Equal(nulls, Attr(calc, "nullHandling"));
        Assert.Equal("<unknown>", Attr(calc, "currency")); // nothing in the code says; must not be guessed
    }

    [Fact]
    public void Cross_repo_links_are_left_unbound_for_the_snapshot_resolver()
    {
        var web = A("web-ts/v1-base/_fixture_web.jsonl");
        var request = web.Edges.Single(e => e.Relation == "REQUESTS");
        Assert.Equal("unresolved", request.Resolution);
        Assert.Equal("configuration", request.UnresolvedReason);
        Assert.Equal("http|@unbound:web-ts/@fixture/web:base=salesApiBaseUrl|GET /api/articles/{}/weekly-sales", request.To);

        var api = A("api-cs/v1-base/Sales.Api.jsonl");
        Assert.Contains(api.Nodes, n => n.SymbolKey == "http|api-cs/Sales.Api|GET /api/articles/{}/weekly-sales");

        // Same table name, different binding hints: identical names are not proof of the same resource.
        var efTable = api.Edges.Single(e => e.Relation == "MAPS_TO").To;
        var declared = A("api-cs/v1-base/SalesDb.jsonl").Nodes.Single(n => n.NodeKind == "Table" && n.Name == "dbo.Sales").SymbolKey;
        Assert.NotEqual(efTable, declared);
    }
}
