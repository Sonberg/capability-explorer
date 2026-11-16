using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CapabilityExplorer.Binding;
using CapabilityExplorer.Capabilities.Analysis;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class LandscapeAnalysisApiTests
{
    private const string Url = "/api/landscape-analysis";

    [Fact]
    public async Task Given_published_revisions_and_their_mapping_identity_when_landscape_analysis_runs_then_the_snapshot_records_all_input_identities()
    {
        using var factory = new CapabilityExplorerFactory();
        using var client = Client(factory);

        using var response = await Analyze(client, Request());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ce.landscape-analysis.v1", result.GetProperty("schema").GetString());
        Assert.Equal("complete", result.GetProperty("status").GetString());
        Assert.Equal("landscape-analysis-v1", result.GetProperty("ruleVersion").GetString());
        Assert.Equal("integration-cluster-v2", result.GetProperty("integrationRuleVersion").GetString());
        Assert.Equal(BindingFile.Empty.Sha256, result.GetProperty("bindingsSha256").GetString());
        Assert.Empty(result.GetProperty("bindings").EnumerateArray());
        Assert.Matches("^[0-9a-f]{64}$", result.GetProperty("contentHash").GetString());
        var inputs = result.GetProperty("inputs").EnumerateArray().ToArray();
        Assert.NotEmpty(inputs);
        Assert.All(inputs, input =>
        {
            var repository = input.GetProperty("repositoryId").GetString()!;
            var expectedCommit = repository switch
            {
                "forecast-cs" => Fixtures.Lock["forecast-cs"]["v1-base"],
                "pricing-node" => Fixtures.Lock["pricing-node"]["v2-move"],
                _ => throw new Xunit.Sdk.XunitException($"Unexpected input repository {repository}."),
            };
            Assert.Equal(expectedCommit, input.GetProperty("commitSha").GetString());
            Assert.Matches("^[0-9a-f]{64}$", input.GetProperty("artifactSha256").GetString());
            Assert.False(string.IsNullOrEmpty(input.GetProperty("unitPath").GetString()));
            Assert.False(string.IsNullOrEmpty(input.GetProperty("configHash").GetString()));
            Assert.False(string.IsNullOrEmpty(input.GetProperty("inputHash").GetString()));
            Assert.False(string.IsNullOrEmpty(input.GetProperty("extractor").GetProperty("name").GetString()));
            Assert.Equal("full", input.GetProperty("status").GetString());
            Assert.True(input.TryGetProperty("coverage", out _));
            Assert.True(input.TryGetProperty("diagnostics", out _));
        });
        Assert.Equal(inputs.Length, inputs.Select(input =>
            (input.GetProperty("repositoryId").GetString(), input.GetProperty("unitPath").GetString())).Distinct().Count());
    }

    [Fact]
    public async Task Given_identical_published_revisions_and_mappings_when_analysis_runs_twice_then_the_content_hash_is_identical()
    {
        using var firstFactory = new CapabilityExplorerFactory();
        using var secondFactory = new CapabilityExplorerFactory();
        using var firstClient = Client(firstFactory);
        using var secondClient = Client(secondFactory);

        using var firstResponse = await Analyze(firstClient, Request());
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        var first = await firstResponse.Content.ReadFromJsonAsync<JsonElement>();
        using var secondResponse = await Analyze(secondClient, Request());
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var second = await secondResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(first.GetProperty("contentHash").GetString(), second.GetProperty("contentHash").GetString());
        Assert.Equal(first.GetProperty("inputs").GetRawText(), second.GetProperty("inputs").GetRawText());
        Assert.Equal(first.GetProperty("landscape").GetRawText(), second.GetProperty("landscape").GetRawText());
    }

    [Fact]
    public async Task Given_an_unavailable_selected_revision_after_a_successful_analysis_when_analysis_runs_then_it_reports_incomplete_input_and_preserves_the_last_snapshot()
    {
        using var factory = new CapabilityExplorerFactory();
        using var client = Client(factory);

        using var accepted = await Analyze(client, Request());
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var before = await client.GetFromJsonAsync<JsonElement>(Url);
        var previousHash = before.GetProperty("contentHash").GetString();

        using var incomplete = await Analyze(client, new
        {
            revisions = new[]
            {
                new { repositoryId = "forecast-cs", commitSha = Fixtures.Lock["forecast-cs"]["v1-base"] },
                new { repositoryId = "pricing-node", commitSha = new string('f', 40) },
            },
            bindingsSha256 = BindingFile.Empty.Sha256,
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, incomplete.StatusCode);
        var error = await incomplete.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("incomplete-inputs", error.GetProperty("code").GetString());
        Assert.Contains(error.GetProperty("missing").EnumerateArray(),
            item => item.GetProperty("repositoryId").GetString() == "pricing-node");
        var after = await client.GetFromJsonAsync<JsonElement>(Url);
        Assert.Equal(previousHash, after.GetProperty("contentHash").GetString());

        using var partial = await Analyze(client, new
        {
            revisions = new[]
            {
                new { repositoryId = "worker-cs", commitSha = Fixtures.Lock["worker-cs"]["v2-broken"] },
            },
            bindingsSha256 = BindingFile.Empty.Sha256,
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, partial.StatusCode);
        var partialError = await partial.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("incomplete-inputs", partialError.GetProperty("code").GetString());
        Assert.Contains(partialError.GetProperty("partial").EnumerateArray(),
            item => item.GetProperty("status").GetString() == "partial");
        after = await client.GetFromJsonAsync<JsonElement>(Url);
        Assert.Equal(previousHash, after.GetProperty("contentHash").GetString());
    }

    [Fact]
    public async Task Given_a_valid_selection_when_landscape_analysis_runs_then_it_does_not_start_source_acquisition_or_extraction()
    {
        using var factory = new CapabilityExplorerFactory();
        using var client = Client(factory);
        var runs = new FileRunStore(factory.RequestsDirectory);
        var checksBefore = runs.ReadLatestCheck("demand-pricing")!.RequestId;
        var publishedBefore = runs.ReadPublishedRuns().Select(run => run.RunId).ToArray();

        using var response = await Analyze(client, Request());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(checksBefore, runs.ReadLatestCheck("demand-pricing")!.RequestId);
        Assert.Equal(publishedBefore, runs.ReadPublishedRuns().Select(run => run.RunId).ToArray());
    }

    [Fact]
    public async Task Given_a_request_without_the_landscape_action_header_when_analysis_is_requested_then_no_snapshot_is_published()
    {
        using var factory = new CapabilityExplorerFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(Url, Request());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Url)).StatusCode);
    }

    private static HttpClient Client(CapabilityExplorerFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-CE-Landscape-Analysis", "1");
        return client;
    }

    private static object Request() => new
    {
        revisions = new[]
        {
            new { repositoryId = "forecast-cs", commitSha = Fixtures.Lock["forecast-cs"]["v1-base"] },
            new { repositoryId = "pricing-node", commitSha = Fixtures.Lock["pricing-node"]["v2-move"] },
        },
        bindingsSha256 = BindingFile.Empty.Sha256,
    };

    private static Task<HttpResponseMessage> Analyze(HttpClient client, object request) =>
        client.PostAsJsonAsync(Url, request);
}
