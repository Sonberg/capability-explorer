using System.Text.Json;
using CapabilityExplorer.Binding;
using CapabilityExplorer.Explorer;
using Xunit;

namespace CapabilityExplorer.Tests;

/// <summary>Real frozen artifacts and the repository service map; no generated facts or runtime traffic.</summary>
public sealed class GlossaryFlowFixtureTests
{
    [Theory]
    [InlineData("v1-base", "resolved", "bound")]
    [InlineData("v2-contract", "unresolved", "broken")]
    public void Demand_pricing_calculation_navigation_preserves_binding_provenance_and_revision_boundaries(
        string forecastRevision, string expectedResolution, string expectedCertainty)
    {
        using var capabilities = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures.RepositoryRoot, "capabilities.json")));
        var configured = capabilities.RootElement.GetProperty("capabilities").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == "demand-pricing");
        var repositories = configured.GetProperty("repositories").EnumerateArray()
            .Select(r => r.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(["api-cs", "forecast-cs", "pricing-node"], repositories.Order(StringComparer.Ordinal));

        // HEAD fixtures are api v3, pricing v2 and forecast v2. The explicit forecast v1 selection
        // demonstrates a compatible historical binding; it is not a deployment manifest.
        var revisions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["api-cs"] = Fixtures.Lock["api-cs"]["v3-filter"],
            ["pricing-node"] = Fixtures.Lock["pricing-node"]["v2-move"],
            ["forecast-cs"] = Fixtures.Lock["forecast-cs"][forecastRevision],
        };
        var bindings = BindingFile.Load(Path.Combine(Fixtures.RepositoryRoot, "bindings.json"));
        var index = ExplorerIndex.Load([Fixtures.ExpectedDirectory], null, bindings);
        Assert.Empty(index.Problems);
        var view = index.View(null, new ViewScope("demand-pricing", repositories, revisions));
        Assert.Equal(revisions.OrderBy(p => p.Key, StringComparer.Ordinal), view.Selection.OrderBy(p => p.Key, StringComparer.Ordinal));
        var seed = Assert.Single(Glossary.Build(view).SelectMany(c => c.Occurrences), o =>
            o.RepositoryId == "pricing-node" && o.Role == "calculation" && o.Name == "priceForDemand");
        Assert.Equal(revisions["pricing-node"], seed.CommitSha);
        Assert.Contains("src/pricing/demand.ts", seed.Key, StringComparison.Ordinal);

        const string forecastHint = "@unbound:pricing-node/@fixture/pricing:env=FORECAST_API_URL";
        var original = Assert.Single(view.Edges, e => e.Artifact.RepositoryId == "pricing-node" &&
            e.Record.Relation == "REQUESTS" && e.Record.To?.Contains(forecastHint, StringComparison.Ordinal) == true);
        var before = original.Record;
        var serviceMap = Assert.Single(bindings.Entries, b => b.Hint == forecastHint);
        var derived = Assert.IsType<BoundEdge>(view.BoundFor(original));
        var flow = GlossaryFlow.Build(view, [seed.Id], new("both", 8, 500, 1000));
        var seedNode = Assert.Single(flow.Seeds);
        Assert.Equal(seed.Id, seedNode.OccurrenceId);
        Assert.Contains(flow.Nodes, n => n.Id == seedNode.NodeId && n.OccurrenceIds.Contains(seed.Id));
        Assert.All(flow.Selection, item => Assert.Equal(revisions[item.RepositoryId], item.CommitSha));
        Assert.Equal(repositories.Order(StringComparer.Ordinal), flow.Selection.Select(s => s.RepositoryId).Distinct().Order(StringComparer.Ordinal));
        Assert.All(flow.Nodes.Where(n => n.RepositoryId is not null), n => Assert.Equal(revisions[n.RepositoryId!], n.CommitSha));

        var bound = Assert.Single(flow.Edges, e => e.Category == "service-binding" && e.OriginalRecordId == original.Record.Id);
        Assert.Equal(expectedResolution, bound.Resolution);
        Assert.Equal(derived.Edge.Id, bound.RecordId);
        Assert.Equal("human", bound.Derivation);
        Assert.Equal("service-map 1", bound.Rule);
        Assert.Equal(serviceMap.Hint, bound.BindingHint);
        Assert.Equal(serviceMap.Target, bound.BindingTarget);
        Assert.Equal(serviceMap.Note, bound.BindingNote);
        Assert.Equal(original.Artifact.Artifact.Header.Unit.Path, bound.UnitPath);
        Assert.Equal(Map.Evidence(original.Artifact, original.Record.Evidence).OrderBy(e => e.Id, StringComparer.Ordinal),
            bound.Evidence.OrderBy(e => e.Id, StringComparer.Ordinal));
        Assert.All(bound.Evidence, e =>
        {
            Assert.Equal("pricing-node", e.RepositoryId);
            Assert.Equal(revisions["pricing-node"], e.CommitSha);
        });
        var canvasEdge = Assert.Single(flow.Graph.Edges, e => e.Id == bound.Id);
        Assert.Equal(expectedCertainty, canvasEdge.Certainty);
        Assert.Contains("context", canvasEdge.Label, StringComparison.Ordinal);
        Assert.Equal("pricing-node", flow.Nodes.Single(n => n.Id == bound.From).RepositoryId);

        // Reach the HTTP caller through real CALLS context around the exact calculation:
        // getPrice calls both priceForDemand and getForecast. This establishes navigation only.
        var calculation = flow.Nodes.Single(n => n.Id == seedNode.NodeId);
        var caller = Assert.Single(flow.Edges, e => e.Category == "source-navigation" && e.Relation == "CALLS" &&
            e.To == calculation.Id && flow.Nodes.Single(n => n.Id == e.From).SymbolKey!.Contains("getPrice#value", StringComparison.Ordinal));
        Assert.Contains(flow.Edges, e => e.Category == "source-navigation" && e.Relation == "CALLS" &&
            e.From == caller.From && e.To == bound.From);
        var preserved = Assert.Single(flow.Edges, e => e.Category == "source-navigation" && e.RecordId == original.Record.Id);
        Assert.Equal("unresolved", preserved.Resolution);
        Assert.Equal("configuration", preserved.UnresolvedReason);
        Assert.Equal(before, original.Record);
        Assert.False(flow.Coverage.ValueFlowComplete);
        Assert.Contains(flow.Coverage.Reasons, r => r.Code == "unsupported-field-mapping");
        Assert.DoesNotContain(flow.Edges, e => e.Category == "human-assertion");

        var destination = flow.Nodes.Single(n => n.Id == bound.To);
        Assert.Equal("http|forecast-cs/Forecast.Api|GET /api/forecasts/{}", destination.SymbolKey);
        if (forecastRevision == "v1-base")
        {
            Assert.Equal(BindingStatus.Bound, derived.Status);
            Assert.Equal("forecast-cs", destination.RepositoryId);
            Assert.Equal(revisions["forecast-cs"], destination.CommitSha);
            Assert.NotEmpty(destination.Evidence);
            Assert.DoesNotContain(flow.Graph.Nodes.Single(n => n.Id == destination.Id).Badges, b => b == "boundary");
        }
        else
        {
            Assert.Equal(BindingStatus.Broken, derived.Status);
            Assert.Equal("missing-reference", bound.UnresolvedReason);
            Assert.Contains(flow.Coverage.Reasons, r => r.Code == "binding-boundary" && r.EdgeId == bound.Id);
            Assert.Contains("boundary", flow.Graph.Nodes.Single(n => n.Id == destination.Id).Badges);
            Assert.DoesNotContain(flow.Nodes, n => n.RepositoryId == "forecast-cs" && n.SymbolKey == destination.SymbolKey);
        }
    }
}
