using CapabilityExplorer.Binding;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Explorer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class HttpBindingRecommendationTests
{
    private const string Hint = "@unbound:pricing-node/@fixture/pricing:env=FORECAST_API_URL";
    private const string Target = "forecast-cs/Forecast.Api";

    [Fact]
    public void Given_only_a_generic_root_route_when_reconciling_then_no_dependency_is_suggested()
    {
        var original = Load("pricing-node", "v1-base", "@fixture/pricing");
        var call = original.Artifact.Edges.Single(edge => edge.To?.Contains(Hint, StringComparison.Ordinal) == true);
        var source = ChangedPricing(text => text.Replace(call.To!, $"http|{Hint}|GET /", StringComparison.Ordinal));
        var destination = Load("forecast-cs", "v1-base", "Forecast.Api");
        var endpoint = destination.Artifact.Nodes.Single(node => node.NodeKind == "Endpoint" && node.SymbolKey.Split('|')[2] == call.To!.Split('|')[2]);
        var report = ArtifactValidator.Validate(Fixtures.Golden("forecast-cs/v1-base/Forecast.Api.jsonl")
            .Replace(endpoint.SymbolKey, $"http|{Target}|GET /", StringComparison.Ordinal));
        Assert.True(report.IsValid, string.Join("; ", report.Problems));
        Assert.DoesNotContain(IntegrationMatching.Build([
            new("pricing", "source-run", source.From, source.Artifact),
            new("forecast", "target-run", destination.From, report.Artifact!),
        ], BindingFile.Empty), match => match.EdgeId == call.Id);
    }

    [Theory]
    [InlineData("PUBLISHES", "CONSUMES", "chan|servicebus|orders|topic/sales", "message", "matched")]
    [InlineData("PUBLISHES", "CONSUMES", "chan|servicebus|@unbound:pricing-node/@fixture/pricing:env=BUS|topic/sales", "message", "suggested")]
    [InlineData("WRITES", "READS", "sql|orders|dbo|sales", "data", "matched")]
    public void Given_resource_participants_when_reconciling_then_identity_controls_connection_strength(
        string sourceRelation, string targetRelation, string resource, string kind, string status)
    {
        var source = Load("pricing-node", "v1-base", "@fixture/pricing");
        var target = Load("forecast-cs", "v1-base", "Forecast.Api");
        var sourceArtifact = Participant("pricing-node/v1-base/_fixture_pricing.jsonl", sourceRelation, resource);
        var targetArtifact = Participant("forecast-cs/v1-base/Forecast.Api.jsonl", targetRelation, resource);
        var match = Assert.Single(IntegrationMatching.Build([
            new("pricing", "source-run", source.From, sourceArtifact),
            new("forecast", "target-run", target.From, targetArtifact),
        ], BindingFile.Empty), item => item.SourceCapabilityId == "pricing");
        Assert.Equal(kind, match.Kind);
        Assert.Equal(status, match.Status);
        Assert.Equal(targetArtifact.Edges[0].From, match.TargetKey);
        Assert.NotEmpty(match.SourceEvidence);
        Assert.NotEmpty(match.TargetEvidence);
    }

    private static Artifact Participant(string fixture, string relation, string resource)
    {
        var rows = Fixtures.Golden(fixture).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToList();
        if (resource.Contains("@unbound:", StringComparison.Ordinal))
            resource = resource.Replace("@unbound:pricing-node/@fixture/pricing:",
                $"@unbound:{rows[0]["repositoryId"]!.GetValue<string>()}/{rows[0]["unit"]!["path"]!.GetValue<string>()}:", StringComparison.Ordinal);
        var edge = rows.First(row => row["kind"]?.GetValue<string>() == "edge" && row["relation"]?.GetValue<string>() == "REQUESTS");
        rows.RemoveAll(row => row["kind"]?.GetValue<string>() == "edge" && !ReferenceEquals(row, edge));
        edge["relation"] = relation;
        edge["to"] = resource;
        edge["resolution"] = "resolved";
        edge.AsObject().Remove("unresolvedReason");
        var complete = rows.Single(row => row["kind"]?.GetValue<string>() == "complete");
        complete["counts"]!["edge"] = 1;
        complete["coverage"]!["unresolvedEdges"] = 0;
        var report = ArtifactValidator.Validate(string.Join('\n', rows.Select(row => row.ToJsonString())));
        Assert.True(report.IsValid, string.Join("; ", report.Problems));
        return report.Artifact!;
    }

    [Fact]
    public void Given_route_only_identity_when_reconciling_then_complete_route_coverage_remains_suggested()
    {
        var source = Load("pricing-node", "v1-base", "@fixture/pricing");
        var target = Load("forecast-cs", "v1-base", "Forecast.Api");
        var matches = IntegrationMatching.Build([
            new("pricing", "source-run", source.From, source.Artifact),
            new("forecast", "target-run", target.From, target.Artifact),
        ], BindingFile.Empty);
        Assert.Equal("suggested", Assert.Single(matches, item => item.TargetCapabilityId == "forecast").Status);
        Assert.Equal(matches.Select(match => match.Id), IntegrationMatching.Build([
            new("forecast", "target-run", target.From, target.Artifact),
            new("pricing", "source-run", source.From, source.Artifact),
        ], BindingFile.Empty).Select(match => match.Id));
    }

    [Fact]
    public void Given_published_capabilities_when_binding_proves_http_identity_then_automatic_match_retains_both_revisions()
    {
        var source = Load("pricing-node", "v1-base", "@fixture/pricing");
        var target = Load("forecast-cs", "v1-base", "Forecast.Api");
        var binding = BindingFile.Parse(JsonSerializer.Serialize(new { bindings = new[] { new { hint = Hint, target = Target } } }));
        var matches = IntegrationMatching.Build([
            new("pricing", "source-run", source.From, source.Artifact),
            new("forecast", "target-run", target.From, target.Artifact),
        ], binding);
        var match = Assert.Single(matches, item => item.SourceCapabilityId == "pricing" && item.TargetCapabilityId == "forecast");
        Assert.Equal("matched", match.Status);
        Assert.Equal("http", match.Kind);
        Assert.Equal("source-run", match.SourceRunId);
        Assert.Equal("target-run", match.TargetRunId);
        Assert.Equal(source.Artifact.ContentSha256, match.Source.ArtifactHash);
        Assert.Equal(target.Artifact.ContentSha256, match.Target.ArtifactHash);
        Assert.NotEmpty(match.SourceEvidence);
        Assert.NotEmpty(match.TargetEvidence);
    }

    private static (LoadedFrom From, Artifact Artifact) Load(string repo, string revision, string unit)
    {
        var artifact = ArtifactValidator.Validate(Fixtures.Golden($"{repo}/{revision}/{unit.Replace('/', '_').Replace('@', '_')}.jsonl")).Artifact!;
        return (new(repo, artifact.Header.CommitSha, artifact.Header.Unit.Path), artifact);
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("revision")]
    [InlineData("unit")]
    public void Given_mismatched_source_identity_when_matching_then_the_input_is_rejected(string field)
    {
        var source = Load("pricing-node", "v1-base", "@fixture/pricing");
        var from = field switch
        {
            "repository" => source.From with { RepositoryId = "other" },
            "revision" => source.From with { CommitSha = new string('f', 40) },
            _ => source.From with { UnitPath = "other-unit" },
        };
        Assert.Throws<ArgumentException>(() => IntegrationMatching.Build(
            [new("pricing", "source-run", from, source.Artifact)], BindingFile.Empty));
    }

    [Fact]
    public void Given_multiple_provider_revisions_when_matching_then_selection_is_rejected()
    {
        var first = Load("forecast-cs", "v1-base", "Forecast.Api");
        var second = Load("forecast-cs", "v2-contract", "Forecast.Api");
        Assert.Throws<ArgumentException>(() => IntegrationMatching.Build([
            new("forecast", "first-run", first.From, first.Artifact),
            new("forecast", "second-run", second.From, second.Artifact),
        ], BindingFile.Empty));
    }

    [Fact]
    public void Given_conflicting_artifact_content_at_one_revision_when_matching_then_selection_is_rejected()
    {
        var target = Load("forecast-cs", "v1-base", "Forecast.Api");
        var changed = ArtifactValidator.Validate(Fixtures.Golden("forecast-cs/v1-base/Forecast.Api.jsonl")
            .Replace(target.Artifact.Header.ConfigHash, new string('f', 64), StringComparison.Ordinal));
        Assert.True(changed.IsValid, string.Join("; ", changed.Problems));
        Assert.Throws<ArgumentException>(() => IntegrationMatching.Build([
            new("forecast", "first-run", target.From, target.Artifact),
            new("forecast", "second-run", target.From, changed.Artifact!),
        ], BindingFile.Empty));
    }

    [Fact]
    public void Given_two_provider_units_in_one_capability_when_binding_then_identity_remains_ambiguous()
    {
        var source = Load("pricing-node", "v1-base", "@fixture/pricing");
        var target = Load("forecast-cs", "v1-base", "Forecast.Api");
        var rows = Fixtures.Golden("forecast-cs/v1-base/Forecast.Api.jsonl")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToList();
        rows[0]["unit"]!["path"] = "Forecast.Other";
        var changed = ArtifactValidator.Validate(string.Join('\n', rows.Select(row => row.ToJsonString())));
        Assert.True(changed.IsValid, string.Join("; ", changed.Problems));
        var other = changed.Artifact!;
        var bindings = BindingFile.Parse(JsonSerializer.Serialize(new { bindings = new[] { new { hint = Hint, target = Target } } }));
        IntegrationMatchInput[] inputs = [
            new("pricing", "source-run", source.From, source.Artifact),
            new("forecast", "target-run", target.From, target.Artifact),
            new("forecast", "target-run", new(target.From.RepositoryId, target.From.CommitSha, other.Header.Unit.Path), other),
        ];
        var matches = IntegrationMatching.Build(inputs, bindings).Where(match => match.Kind == "http").ToList();
        Assert.Equal(2, matches.Count);
        Assert.All(matches, match => Assert.Equal("suggested", match.Status));
        Assert.All(matches, match => { Assert.NotEmpty(match.SourceEvidence); Assert.NotEmpty(match.TargetEvidence); });
        Assert.Equal(matches.Select(match => match.Id),
            IntegrationMatching.Build(inputs.Reverse().ToArray(), bindings).Where(match => match.Kind == "http").Select(match => match.Id));
    }

    private static (LoadedFrom From, Artifact Artifact) ChangedPricing(Func<string, string> change)
    {
        var report = ArtifactValidator.Validate(change(Fixtures.Golden("pricing-node/v1-base/_fixture_pricing.jsonl")));
        Assert.True(report.IsValid, string.Join("; ", report.Problems));
        var artifact = report.Artifact!;
        return (new(artifact.Header.RepositoryId, artifact.Header.CommitSha, artifact.Header.Unit.Path), artifact);
    }

    [Fact]
    public void Locked_fixture_exact_route_baseline_matches_four_reviewed_calls_and_abstains_on_dynamic_call()
    {
        var inputs = Fixtures.GoldenFiles.Where(file => file.Contains("/v1-base/", StringComparison.Ordinal))
            .Select(file => ArtifactValidator.Validate(Fixtures.Golden(file)).Artifact!).ToList();
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["e:13f76b548e800f60"] = "api-cs/Sales.Api",
            ["e:50996e799fe26c19"] = Target,
            ["e:2a67e51c60549753"] = "api-cs/Sales.Api",
            ["e:0c7e09536422f6c5"] = "api-cs/Sales.Api",
        };
        var endpoints = inputs.SelectMany(input => input.Nodes).Where(node => node.NodeKind == "Endpoint").ToList();
        var requests = inputs.SelectMany(input => input.Edges).Where(edge => edge.Relation == "REQUESTS").ToList();
        Assert.Equal(5, requests.Count);
        Assert.Single(requests, request => request.To is null);
        foreach (var request in requests.Where(request => request.To is not null))
        {
            var matches = endpoints.Where(endpoint => endpoint.SymbolKey.Split('|')[2] == request.To!.Split('|')[2]).ToList();
            Assert.Equal(labels[request.Id], Assert.Single(matches).SymbolKey.Split('|')[1]);
        }
        var changed = Load("forecast-cs", "v2-contract", "Forecast.Api");
        var source = Load("pricing-node", "v1-base", "@fixture/pricing");
        var forecastCall = source.Artifact.Edges.Single(edge => edge.To?.Contains(Hint, StringComparison.Ordinal) == true);
        Assert.DoesNotContain(changed.Artifact.Nodes, node => node.NodeKind == "Endpoint" && node.SymbolKey.Split('|')[2] == forecastCall.To!.Split('|')[2]);
    }

    [Fact]
    public void Exact_match_returns_service_and_bidirectional_evidence_without_mutating_facts()
    {
        var source = Load("pricing-node", "v1-base", "@fixture/pricing");
        var target = Load("forecast-cs", "v1-base", "Forecast.Api");
        var original = source.Artifact.Edges.ToArray();
        var groups = HttpBindingRecommendations.Build([source], [target], BindingFile.Empty);
        var group = Assert.Single(groups, item => item.Hint == Hint);
        var candidate = Assert.Single(group.Candidates);
        Assert.Equal(Target, candidate.Target);
        Assert.Equal(100, candidate.Score);
        Assert.True(candidate.PreviewEligible);
        var match = Assert.Single(candidate.Matches);
        Assert.NotEmpty(match.Request.Evidence);
        Assert.NotEmpty(match.Endpoint.Evidence);
        Assert.Equal(source.Artifact.ContentSha256, match.Request.Source.ArtifactHash);
        Assert.Equal(target.Artifact.ContentSha256, match.Endpoint.Source.ArtifactHash);
        Assert.Equal(original, source.Artifact.Edges);
        Assert.Equal("candidate", candidate.State);
    }

    [Fact]
    public void Reordered_targets_and_repeated_call_sites_preserve_scores_and_ambiguity()
    {
        var source = ChangedPricing(text =>
        {
            var rows = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToList();
            var originalRequest = rows.Single(row => row["id"]?.GetValue<string>() == "e:50996e799fe26c19");
            for (var index = 0; index < 45; index++)
            {
                var request = originalRequest.DeepClone();
                request["id"] = $"e:{index + 100000:x16}";
                rows.Insert(rows.FindLastIndex(row => row["kind"]?.GetValue<string>() == "edge") + 1, request);
            }
            var counts = rows.Single(row => row["kind"]?.GetValue<string>() == "complete")["counts"]!;
            counts["edge"] = counts["edge"]!.GetValue<int>() + 45;
            var coverage = rows.Single(row => row["kind"]?.GetValue<string>() == "complete")["coverage"]!;
            coverage["unresolvedEdges"] = coverage["unresolvedEdges"]!.GetValue<int>() + 45;
            return string.Join('\n', rows.Select(row => row.ToJsonString()));
        });
        var target = Load("forecast-cs", "v1-base", "Forecast.Api");
        var rivalReport = ArtifactValidator.Validate(Fixtures.Golden("forecast-cs/v1-base/Forecast.Api.jsonl").Replace("forecast-cs", "rival-cs", StringComparison.Ordinal));
        Assert.True(rivalReport.IsValid, string.Join("; ", rivalReport.Problems));
        var rival = (new LoadedFrom("rival-cs", target.From.CommitSha, target.From.UnitPath), rivalReport.Artifact!);
        var first = HttpBindingRecommendations.Build([source], [target, rival], BindingFile.Empty);
        var second = HttpBindingRecommendations.Build([source], [rival, target], BindingFile.Empty);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        var group = Assert.Single(first, item => item.Hint == Hint);
        Assert.Equal("ambiguous", group.State);
        Assert.Equal(2, group.Candidates.Count);
        Assert.All(group.Candidates, candidate =>
        {
            Assert.Equal(100, candidate.Score);
            Assert.Equal(1, candidate.TotalRoutes);
            Assert.Equal(46, candidate.Matches.Count);
        });
    }

    [Fact]
    public void Whole_hint_coverage_reports_unmatched_calls_and_blocks_export_eligibility()
    {
        var source = ChangedPricing(text => text.Replace("env=SALES_API_URL", "env=FORECAST_API_URL", StringComparison.Ordinal));
        var target = Load("forecast-cs", "v1-base", "Forecast.Api");
        var group = Assert.Single(HttpBindingRecommendations.Build([source], [target], BindingFile.Empty), item => item.Hint == Hint);
        var candidate = Assert.Single(group.Candidates);
        Assert.Equal(50, candidate.Score);
        Assert.Equal(2, candidate.TotalRoutes);
        Assert.Single(candidate.Unmatched);
        Assert.False(candidate.PreviewEligible);
        Assert.Contains("Host ownership", candidate.MissingEvidence);
        Assert.Contains("Query compatibility", candidate.MissingEvidence);
        Assert.Contains("Response schema compatibility", candidate.MissingEvidence);
    }

    [Theory]
    [InlineData("POST /api/forecasts/{}")]
    [InlineData("GET /gateway/api/forecasts/{}")]
    [InlineData("GET /api/v2/forecasts/{}")]
    [InlineData("GET /api/forecasts/%2F")]
    public void Conflicting_method_prefix_version_or_encoded_path_does_not_match(string route)
    {
        var source = ChangedPricing(text => text.Replace("GET /api/forecasts/{}", route, StringComparison.Ordinal));
        var target = Load("forecast-cs", "v1-base", "Forecast.Api");
        var group = Assert.Single(HttpBindingRecommendations.Build([source], [target], BindingFile.Empty), item => item.Hint == Hint);
        Assert.Equal("no-candidate", group.State);
        Assert.Empty(group.Candidates);
    }

    [Fact]
    public void Dynamic_targets_abstain_and_existing_bindings_are_not_overridden()
    {
        var source = Load("pricing-node", "v1-base", "@fixture/pricing");
        var target = Load("forecast-cs", "v1-base", "Forecast.Api");
        var existing = BindingFile.Parse($$"""{"bindings":[{"hint":"{{Hint}}","target":"{{Target}}"}]}""");
        var groups = HttpBindingRecommendations.Build([source], [target], existing);
        Assert.DoesNotContain(groups, group => group.Hint == Hint);
        var dynamic = Assert.Single(groups, group => group.State == "insufficient-evidence");
        Assert.Null(Assert.Single(dynamic.Requests).Target);
        Assert.Empty(dynamic.Candidates);
        var changed = Load("forecast-cs", "v2-contract", "Forecast.Api");
        var broken = Assert.Single(HttpBindingRecommendations.Build([source], [changed], existing), group => group.Hint == Hint);
        Assert.Equal("broken-binding", broken.State);
        Assert.Empty(broken.Candidates);
    }

    [Fact]
    public void Source_and_destination_with_same_identity_but_different_bytes_are_rejected()
    {
        var source = Load("pricing-node", "v1-base", "@fixture/pricing");
        var changed = ChangedPricing(text => text.Replace(source.Artifact.Header.ConfigHash, new string('f', 64), StringComparison.Ordinal));
        var target = Load("forecast-cs", "v1-base", "Forecast.Api");
        Assert.Throws<ArgumentException>(() => HttpBindingRecommendations.Build([source], [changed, target], BindingFile.Empty));
    }

    [Fact]
    public async Task Cross_capability_recommendation_requires_explicit_review_and_rejects_stale_destination()
    {
        var directory = Directory.CreateTempSubdirectory("ce-http-recommendations-");
        try
        {
            var capabilities = Path.Combine(directory.FullName, "capabilities.json");
            await File.WriteAllTextAsync(capabilities, """
                {"capabilities":[
                  {"id":"consumer","name":"Consumer","repositories":[{"id":"pricing-node","kind":"local","location":"unused","ref":"HEAD"}]},
                  {"id":"provider","name":"Provider","repositories":[{"id":"forecast-cs","kind":"local","location":"unused","ref":"HEAD"}]}]}
                """);
            var before = await File.ReadAllTextAsync(capabilities);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(new ExplorerState(new ExplorerOptions([Fixtures.ExpectedDirectory], null, null, capabilities, directory.FullName,
                RequestsDirectory: Path.Combine(directory.FullName, "requests"))));
            await using var app = builder.Build();
            app.MapBindingPreviewApi();
            await app.StartAsync();
            using var client = app.GetTestClient();
            var source = Load("pricing-node", "v1-base", "@fixture/pricing");
            var target = Load("forecast-cs", "v1-base", "Forecast.Api");
            var changed = Load("forecast-cs", "v2-contract", "Forecast.Api");
            var scope = $"?cap=consumer&view=pricing-node:{source.From.CommitSha}&targetView=forecast-cs:{target.From.CommitSha}";
            var response = await client.GetAsync("/api/binding-preview/recommendations" + scope);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var recommendations = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var groups = recommendations.RootElement.GetProperty("groups").EnumerateArray().ToList();
            Assert.Contains(groups, group => group.GetProperty("hint").GetString() == Hint && group.GetProperty("candidates").GetArrayLength() == 1);
            Assert.Contains(recommendations.RootElement.GetProperty("owners").EnumerateArray(), owner => owner.GetProperty("capabilityId").GetString() == "provider");
            var catalog = (await client.GetFromJsonAsync<BindingPreviewCatalog>("/api/binding-preview" + scope))!;
            Assert.Equal(catalog.Fingerprint, recommendations.RootElement.GetProperty("fingerprint").GetString());
            Assert.All(catalog.Hints, hint => Assert.StartsWith("@unbound:pricing-node/", hint.Hint, StringComparison.Ordinal));
            client.DefaultRequestHeaders.Add("X-CE-Binding-Preview", "1");
            var request = new BindingPreviewRequest(catalog.Fingerprint, [new(Hint, Target, null, false)]);
            var preview = await client.PostAsJsonAsync("/api/binding-preview" + scope, request);
            Assert.False((await preview.Content.ReadFromJsonAsync<BindingPreviewResult>())!.CanExport);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/binding-preview/export" + scope, request)).StatusCode);
            var reviewed = request with { Entries = [request.Entries[0] with { Reviewed = true }] };
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/binding-preview/export" + scope, reviewed)).StatusCode);
            var staleScope = scope.Replace(target.From.CommitSha, changed.From.CommitSha, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/binding-preview/export" + staleScope, reviewed)).StatusCode);
            var injected = request with { Entries = [new("@unbound:forecast-cs/Forecast.Api:config=SalesApi:BaseUrl", "api-cs/Sales.Api", null, true)] };
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/binding-preview" + scope, injected)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/binding-preview/recommendations" + scope + ",forecast-cs:unknown")).StatusCode);
            Assert.Equal(before, await File.ReadAllTextAsync(capabilities));
            Assert.False(File.Exists(Path.Combine(directory.FullName, "bindings.json")));
            var changedMembership = before.Replace("\"id\":\"provider\"", "\"id\":\"provider-renamed\"", StringComparison.Ordinal);
            await File.WriteAllTextAsync(capabilities, changedMembership);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/binding-preview/export" + scope, reviewed)).StatusCode);
            Assert.Equal(changedMembership, await File.ReadAllTextAsync(capabilities));
        }
        finally { directory.Delete(true); }
    }
}