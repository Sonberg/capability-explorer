using System.Text.Json.Nodes;
using CapabilityExplorer.Binding;
using CapabilityExplorer.Contracts;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class IntegrationClusterTests
{
    [Fact]
    public void Given_only_one_overlapping_operation_when_a_cluster_has_other_calls_then_no_service_connection_is_inferred()
    {
        var source = Requests(["GET /gateway/api/forecasts/{}", "POST /gateway/api/orders/query"]);
        var target = Endpoints(["GET /api/forecasts/{}", "PATCH /api/forecasts/{}"]);
        var matches = IntegrationMatching.Build([Input("pricing", source), Input("forecast", target)], BindingFile.Empty);
        Assert.DoesNotContain(matches, match => match.Status == "inferred");
    }

    [Fact]
    public void Given_equally_supported_services_when_clustering_then_ties_remain_suggestions()
    {
        var source = Requests(["GET /gateway/api/forecasts/{}", "POST /gateway/api/forecasts/query"]);
        var target = Endpoints(["GET /api/forecasts/{}", "POST /api/forecasts/query"]);
        var rival = Endpoints(["GET /api/forecasts/{}", "POST /api/forecasts/query"], "rival-cs");
        var matches = IntegrationMatching.Build([Input("pricing", source), Input("forecast", target), Input("rival", rival)], BindingFile.Empty);
        Assert.DoesNotContain(matches, match => match.Status == "inferred");
        Assert.Contains(matches, match => match.TargetCapabilityId == "forecast" && match.Status == "suggested");
        Assert.Contains(matches, match => match.TargetCapabilityId == "rival" && match.Status == "suggested");
    }

    [Fact]
    public void Given_different_payload_schemas_when_paths_match_then_the_schema_difference_is_not_hidden()
    {
        const string left = """{"type":"object","properties":{"amount":{"type":"integer"}}}""";
        const string right = """{"type":"object","properties":{"amount":{"type":"string"}}}""";
        var source = Requests(["POST /gateway/api/forecasts/query", "PUT /gateway/api/forecasts/{}"], left);
        var target = Endpoints(["POST /api/forecasts/query", "PUT /api/forecasts/{}"], requestSchema: right);
        var matches = IntegrationMatching.Build([Input("pricing", source), Input("forecast", target)], BindingFile.Empty)
            .Where(match => match.ClusterId is not null).ToList();
        Assert.NotEmpty(matches);
        Assert.All(matches, match => Assert.Equal("incompatible", match.Contract?.Status));
        Assert.Contains(matches.SelectMany(match => match.Contract!.Differences), difference => difference.Contains("requestSchema", StringComparison.Ordinal));
    }

    [Fact]
    public void Given_conflicting_concrete_api_versions_when_paths_align_then_versions_are_reported_incompatible()
    {
        var source = Requests(["GET /gateway/api/v1/forecasts/{}", "POST /gateway/api/v1/forecasts/query"]);
        var target = Endpoints(["GET /api/v2/forecasts/{}", "POST /api/v2/forecasts/query"]);
        var matches = IntegrationMatching.Build([Input("pricing", source), Input("forecast", target)], BindingFile.Empty)
            .Where(match => match.ClusterId is not null).ToList();
        Assert.NotEmpty(matches);
        Assert.All(matches, match => Assert.Equal("incompatible", match.Contract?.Status));
        Assert.Contains(matches.SelectMany(match => match.Contract!.Differences), difference => difference.Contains("version", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Given_related_gateway_calls_when_contract_paths_align_then_one_inferred_cluster_connects_the_service()
    {
        var source = Requests(["GET /gateway/v2/api/v2/forecasts/{}", "POST /gateway/v2/api/v2/forecasts/query"]);
        var target = Endpoints(["GET /api/v{}/forecasts/{}", "POST /api/v{}/forecasts/query"]);
        var matches = IntegrationMatching.Build([Input("pricing", source), Input("forecast", target)], BindingFile.Empty)
            .Where(match => match.SourceCapabilityId == "pricing" && match.TargetCapabilityId == "forecast").ToList();
        Assert.Equal(2, matches.Count);
        Assert.All(matches, match => Assert.Equal("inferred", match.Status));
        Assert.Single(matches.Select(match => match.ClusterId).Distinct());
        Assert.All(matches, match => Assert.False(string.IsNullOrEmpty(match.ClusterId)));
        Assert.All(matches, match =>
        {
            Assert.NotNull(match.Contract);
            Assert.Equal(2, match.Contract.CompatibleOperations);
            Assert.Equal("unverified", match.Contract.Status);
            Assert.NotEmpty(match.Contract.Unknowns);
            Assert.NotEmpty(match.SourceEvidence);
            Assert.NotEmpty(match.TargetEvidence);
        });
    }

    [Fact]
    public void Given_different_methods_and_identifier_locations_when_clustering_then_contract_differences_remain_visible()
    {
        var source = Requests(["GET /gateway/api/forecasts/{}/{}/{}", "PUT /gateway/api/forecasts/{}/{}/{}"]);
        var target = Endpoints(["DELETE /forecasts", "GET /forecasts", "PATCH /forecasts"]);
        var matches = IntegrationMatching.Build([Input("pricing", source), Input("forecast", target)], BindingFile.Empty)
            .Where(match => match.SourceCapabilityId == "pricing" && match.TargetCapabilityId == "forecast").ToList();
        Assert.Equal(2, matches.Count);
        Assert.All(matches, match => Assert.Equal("inferred", match.Status));
        Assert.All(matches, match => Assert.Equal("incompatible", match.Contract?.Status));
        Assert.Contains(matches.SelectMany(match => match.Contract!.Differences), difference => difference.Contains("method", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(matches.SelectMany(match => match.Contract!.Differences), difference => difference.Contains("PUT versus PATCH", StringComparison.Ordinal));
        Assert.Contains(matches.SelectMany(match => match.Contract!.Differences), difference => difference.Contains("identifier", StringComparison.OrdinalIgnoreCase));
    }

    private static IntegrationMatchInput Input(string capability, Artifact artifact) =>
        new(capability, capability + "-run", new(artifact.Header.RepositoryId, artifact.Header.CommitSha, artifact.Header.Unit.Path), artifact);

    private static Artifact Requests(IReadOnlyList<string> signatures, string? requestSchema = null)
    {
        var rows = Rows("pricing-node/v1-base/_fixture_pricing.jsonl");
        var template = rows.Single(row => row["kind"]?.GetValue<string>() == "edge" && row["to"]?.GetValue<string>()?.Contains("FORECAST_API_URL", StringComparison.Ordinal) == true);
        var target = template["to"]!.GetValue<string>().Split('|')[1];
        rows.RemoveAll(row => row["kind"]?.GetValue<string>() == "edge" && row["relation"]?.GetValue<string>() == "REQUESTS");
        foreach (var (signature, index) in signatures.Select((signature, index) => (signature, index)))
        {
            var edge = template.DeepClone();
            edge["id"] = $"e:{index + 200000:x16}";
            edge["to"] = $"http|{target}|{signature}";
            edge["props"] = new JsonObject { ["method"] = signature.Split(' ')[0], ["normalizedRoute"] = signature.Split(' ')[1] };
            if (requestSchema is not null) edge["props"]!["requestSchema"] = requestSchema;
            rows.Insert(rows.FindLastIndex(row => row["kind"]?.GetValue<string>() == "edge") + 1, edge);
        }
        return Validate(rows);
    }

    private static Artifact Endpoints(IReadOnlyList<string> signatures, string? repository = null, string? requestSchema = null)
    {
        var rows = Rows("forecast-cs/v1-base/Forecast.Api.jsonl");
        var available = rows.Where(row => row["kind"]?.GetValue<string>() == "node" && row["nodeKind"]?.GetValue<string>() == "Endpoint"
            && row["symbolKey"]!.GetValue<string>().Contains("forecasts", StringComparison.Ordinal)).ToList();
        var template = available.FirstOrDefault(row => row["symbolKey"]!.GetValue<string>().EndsWith("|" + signatures[0], StringComparison.Ordinal)) ?? available[0];
        var old = template["symbolKey"]!.GetValue<string>();
        var service = old.Split('|')[1];
        foreach (var row in rows)
            foreach (var property in new[] { "symbolKey", "from", "to" })
                if (row[property]?.GetValue<string>() == old) row[property] = $"http|{service}|{signatures[0]}";
        for (var index = 1; index < signatures.Count; index++)
        {
            var node = template.DeepClone();
            node["id"] = $"n:{index + 300000:x16}";
            node["symbolKey"] = $"http|{service}|{signatures[index]}";
            rows.Insert(rows.FindLastIndex(row => row["kind"]?.GetValue<string>() == "node") + 1, node);
        }
        if (requestSchema is not null)
            foreach (var node in rows.Where(row => row["kind"]?.GetValue<string>() == "node" && row["nodeKind"]?.GetValue<string>() == "Endpoint"))
            {
                node["props"] ??= new JsonObject();
                node["props"]!["requestSchema"] = requestSchema;
            }
        if (repository is not null)
            rows = rows.Select(row => JsonNode.Parse(row.ToJsonString().Replace("forecast-cs", repository, StringComparison.Ordinal))!).ToList();
        return Validate(rows);
    }

    private static List<JsonNode> Rows(string fixture) => Fixtures.Golden(fixture).Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => JsonNode.Parse(line)!).ToList();

    private static Artifact Validate(List<JsonNode> rows)
    {
        var complete = rows.Single(row => row["kind"]?.GetValue<string>() == "complete");
        complete["counts"]!["edge"] = rows.Count(row => row["kind"]?.GetValue<string>() == "edge");
        complete["counts"]!["node"] = rows.Count(row => row["kind"]?.GetValue<string>() == "node");
        complete["coverage"]!["unresolvedEdges"] = rows.Count(row => row["kind"]?.GetValue<string>() == "edge" && row["resolution"]?.GetValue<string>() == "unresolved");
        var result = ArtifactValidator.Validate(string.Join('\n', rows.Select(row => row.ToJsonString())));
        Assert.True(result.IsValid, string.Join("; ", result.Problems));
        return result.Artifact!;
    }
}