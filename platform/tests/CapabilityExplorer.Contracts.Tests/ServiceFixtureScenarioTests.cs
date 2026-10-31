using System.Text.Json;
using CapabilityExplorer.Contracts;
using Xunit;

namespace CapabilityExplorer.Tests;

/// <summary>Scenarios planted by the forecast-cs and pricing-node fixtures.</summary>
public sealed class ServiceFixtureScenarioTests
{
    private const string Forecast = "cs|forecast-cs|Forecast.Api|";
    private const string Pricing = "ts|pricing-node|@fixture/pricing|";
    private const string Smoothing = Forecast + "M:Forecast.Api.Forecasting.ExponentialSmoothingStrategy.Forecast(System.Collections.Generic.IReadOnlyList{System.Int32})";
    private const string Resolve = Forecast + "M:Forecast.Api.Forecasting.StrategyLoader.Resolve(System.IServiceProvider,System.String)";
    private const string GetPrice = Pricing + "src/handlers/getPrice.ts|getPrice#value";

    private static Artifact A(string file) => GoldenArtifactTests.Valid(file);

    private static JsonElement Calc(Artifact a, string symbolKey) => a.Calculations.Single(c => c.SymbolKey == symbolKey).Raw;

    private static JsonElement Attribute(JsonElement calc, string name) => calc.GetProperty("attributes").GetProperty(name);

    private static string? Value(JsonElement calc, string name) =>
        Attribute(calc, name).TryGetProperty("value", out var v) ? v.ToString() : null;

    private static string Prop(EdgeRecord e, string name) => e.Props!.Value.GetProperty(name).ToString();

    [Fact]
    public void Same_config_key_name_in_two_repositories_yields_different_channels()
    {
        var worker = A("worker-cs/v1-base/Sales.Worker.jsonl").Edges.Single(e => e.Relation == "PUBLISHES").To;
        var forecast = A("forecast-cs/v1-base/Forecast.Api.jsonl").Edges.Single(e => e.Relation == "CONSUMES").To;
        var pricing = A("pricing-node/v1-base/_fixture_pricing.jsonl").Edges.Single(e => e.Relation == "CONSUMES");

        // Both read "ServiceBus:ConnectionString" and use topic "article-stats-updated", but nothing proves one namespace.
        Assert.EndsWith("|topic/article-stats-updated", worker, StringComparison.Ordinal);
        Assert.EndsWith("|topic/article-stats-updated", forecast, StringComparison.Ordinal);
        Assert.NotEqual(worker, forecast);

        // pricing-node names its namespace literally; a queue with the same short name is yet another resource.
        Assert.Equal("chan|servicebus|sb-pricing.servicebus.windows.net|queue/article-stats-updated", pricing.To);
        Assert.Equal("resolved", pricing.Resolution);
    }

    [Fact]
    public void Route_version_change_breaks_the_match_with_an_unchanged_consumer()
    {
        static string Route(string key) => key[(key.LastIndexOf('|') + 1)..];
        var request = A("pricing-node/v1-base/_fixture_pricing.jsonl").Edges
            .Single(e => e.Relation == "REQUESTS" && e.From == Pricing + "src/clients/forecastClient.ts|getForecast#value");
        var v1 = A("forecast-cs/v1-base/Forecast.Api.jsonl").Nodes.Where(n => n.NodeKind == "Endpoint").Select(n => Route(n.SymbolKey)).ToList();
        var v2 = A("forecast-cs/v2-contract/Forecast.Api.jsonl").Nodes.Where(n => n.NodeKind == "Endpoint").Select(n => Route(n.SymbolKey)).ToList();

        Assert.Equal("GET /api/forecasts/{}", Route(request.To!));
        Assert.Contains(Route(request.To!), v1);
        Assert.DoesNotContain(Route(request.To!), v2);
        Assert.Contains("GET /api/v2/forecasts/{}", v2);
    }

    [Fact]
    public void Constant_change_changes_the_calculation_but_not_its_shape()
    {
        var before = Calc(A("forecast-cs/v1-base/Forecast.Api.jsonl"), Smoothing);
        var after = Calc(A("forecast-cs/v2-contract/Forecast.Api.jsonl"), Smoothing);

        Assert.NotEqual(before.GetProperty("irHash").GetString(), after.GetProperty("irHash").GetString());
        Assert.Equal(0.3, Attribute(before, "aggregation").GetProperty("detail").GetProperty("alpha").GetDouble());
        Assert.Equal(0.5, Attribute(after, "aggregation").GetProperty("detail").GetProperty("alpha").GetDouble());
        foreach (var name in new[] { "rounding", "numericType", "zeroDenominator", "denominatorSource" })
        {
            Assert.Equal(Value(before, name), Value(after, name));
        }
    }

    [Fact]
    public void Moving_a_function_behind_a_re_export_changes_its_key_but_not_its_logic_or_callers()
    {
        const string oldKey = Pricing + "src/pricing/priceForDemand.ts|priceForDemand#value";
        const string newKey = Pricing + "src/pricing/demand.ts|priceForDemand#value";
        var v1 = A("pricing-node/v1-base/_fixture_pricing.jsonl");
        var v2 = A("pricing-node/v2-move/_fixture_pricing.jsonl");

        Assert.Equal(Calc(v1, oldKey).GetProperty("irHash").GetString(), Calc(v2, newKey).GetProperty("irHash").GetString());
        Assert.DoesNotContain(v2.Nodes, n => n.SymbolKey == oldKey);

        var call1 = v1.Edges.Single(e => e.From == GetPrice && e.To == oldKey);
        var call2 = v2.Edges.Single(e => e.From == GetPrice && e.To == newKey);
        var site = (Artifact a, EdgeRecord e) => a.Evidence.Single(ev => ev.Id == e.Evidence[0]);
        Assert.Equal((site(v1, call1).Path, site(v1, call1).Span), (site(v2, call2).Path, site(v2, call2).Span));
    }

    [Fact]
    public void Generated_code_is_marked()
    {
        var pricing = A("pricing-node/v1-base/_fixture_pricing.jsonl");
        Assert.All(pricing.Nodes.Where(n => n.SymbolKey.Contains("src/generated/", StringComparison.Ordinal)), n => Assert.True(n.Generated));
        Assert.True(pricing.Edges.Single(e => e.From.EndsWith("SalesApi.getWeeklySales#value", StringComparison.Ordinal) && e.Relation == "REQUESTS").Props!.Value.GetProperty("generated").GetBoolean());
        Assert.True(A("forecast-cs/v1-base/Forecast.Api.jsonl").Nodes.Single(n => n.Name == "ForecastResponse").Generated);
        Assert.DoesNotContain(pricing.Nodes, n => n.Generated == true && !n.SymbolKey.Contains("src/generated/", StringComparison.Ordinal));
    }

    [Fact]
    public void Keyed_di_gives_candidates_and_reflection_stays_unresolved()
    {
        var edges = A("forecast-cs/v1-base/Forecast.Api.jsonl").Edges.Where(e => e.From == Resolve && e.Relation == "REFERENCES").ToList();
        Assert.Equal(2, edges.Count(e => e.Resolution == "candidate"));
        var reflection = Assert.Single(edges, e => e.Resolution == "unresolved");
        Assert.Equal("reflection", reflection.UnresolvedReason);
        Assert.Null(reflection.To);
    }

    [Fact]
    public void Dynamic_dispatch_has_an_unresolved_edge_and_a_rule_candidate()
    {
        var edges = A("pricing-node/v1-base/_fixture_pricing.jsonl").Edges
            .Where(e => e.From == Pricing + "src/messaging/statsListener.ts|startStatsListener#value" && e.Relation == "CALLS").ToList();
        Assert.Contains(edges, e => e.Resolution == "unresolved" && e.UnresolvedReason == "dynamic-dispatch");
        Assert.Contains(edges, e => e.Resolution == "candidate" && e.Derivation == "rule" && e.To!.EndsWith("onStatsUpdated#value", StringComparison.Ordinal));
    }

    [Fact]
    public void Data_access_through_a_generic_repository_names_the_type_argument()
    {
        var forecast = A("forecast-cs/v1-base/Forecast.Api.jsonl");
        var reads = forecast.Edges.Single(e => e.Relation == "READS");
        Assert.Equal(Forecast + "T:Forecast.Api.Data.WeeklyHistory", Prop(reads, "typeArgument"));
        Assert.EndsWith("|forecast.weekly_history", reads.To, StringComparison.Ordinal);

        var mappings = forecast.Edges.Where(e => e.Relation == "MAPS_TO").ToDictionary(e => Prop(e, "mapping"), e => e.To!);
        Assert.EndsWith("|forecast.weekly_history", mappings["attribute"], StringComparison.Ordinal);
        Assert.EndsWith("|public.Forecasts", mappings["convention"], StringComparison.Ordinal);
    }

    [Fact]
    public void Dynamic_sql_is_marked_partial()
    {
        var reads = A("pricing-node/v1-base/_fixture_pricing.jsonl").Edges.Where(e => e.Relation == "READS").ToDictionary(e => e.From, e => Prop(e, "sqlAnalysis"));
        Assert.Equal("partial", reads[Pricing + "src/db/priceRepository.ts|listPrices#value"]);
        Assert.Equal("full", reads[Pricing + "src/db/priceRepository.ts|findPrice#value"]);
    }

    [Fact]
    public void Partial_classes_and_overloads_are_one_symbol_with_several_declarations()
    {
        var series = A("forecast-cs/v1-base/Forecast.Api.jsonl");
        var node = series.Nodes.Single(n => n.Name == "WeeklySeries");
        Assert.Equal(2, series.Evidence.Where(e => node.Evidence.Contains(e.Id)).Select(e => e.Path).Distinct().Count());

        var pricing = A("pricing-node/v1-base/_fixture_pricing.jsonl");
        var ratio = Assert.Single(pricing.Nodes, n => n.Name == "demandRatio");
        Assert.Equal(3, ratio.Evidence.Count);
    }

    public static TheoryData<string, string, string, string> SpecialCases => new()
    {
        // file, symbol, attribute, planted value
        { "forecast-cs/v1-base/Forecast.Api.jsonl", Smoothing, "zeroDenominator", "returnsNaN" },
        { "forecast-cs/v1-base/Forecast.Api.jsonl", Forecast + "M:Forecast.Api.Forecasting.MovingAverageStrategy.Forecast(System.Collections.Generic.IReadOnlyList{System.Int32})", "zeroDenominator", "throws" },
        { "forecast-cs/v1-base/Forecast.Api.jsonl", Forecast + "M:Forecast.Api.Forecasting.WeeklySeries.TotalUnits", "overflow", "wraps" },
        { "pricing-node/v1-base/pricing-db.jsonl", "sql|@unbound:pricing-node/pricing-db:dbproject=pricing-db|pricing.weekly_revenue", "overflow", "throws" },
        { "pricing-node/v1-base/pricing-db.jsonl", "sql|@unbound:pricing-node/pricing-db:dbproject=pricing-db|pricing.weekly_revenue", "currency", "mixedCurrenciesSummed" },
        { "pricing-node/v1-base/_fixture_pricing.jsonl", Pricing + "src/legacy/revenue.js|weeklyRevenueSek#value", "currency", "convertedToSek" },
        { "pricing-node/v1-base/_fixture_pricing.jsonl", Pricing + "src/legacy/revenue.js|weeklyRevenueSek#value", "denominatorSource", "parameter" },
        { "pricing-node/v1-base/_fixture_pricing.jsonl", Pricing + "src/pricing/priceForDemand.ts|priceForDemand#value", "rounding", "truncate" },
    };

    [Theory]
    [MemberData(nameof(SpecialCases))]
    public void Special_cases_are_planted(string file, string symbol, string attribute, string expected)
    {
        Assert.Equal(expected, Value(Calc(A(file), symbol), attribute));
    }
}
