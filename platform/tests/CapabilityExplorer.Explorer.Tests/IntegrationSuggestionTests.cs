using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using CapabilityExplorer.Interpretation;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class IntegrationSuggestionTests
{
    [Fact]
    public async Task Given_cancelled_assessment_when_suggesting_then_no_model_request_is_sent()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var model = new SuggestionModel("not json");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new IntegrationSuggestionService(model).SuggestAsync(Context(), cancellation.Token));
        Assert.Null(model.Request);
    }

    [Fact]
    public async Task Given_an_untrusted_request_when_assessing_then_no_model_call_or_connection_write_occurs()
    {
        using var factory = new ExplorerFactory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/architecture/integration-suggestions", new
        {
            sourceCapabilityId = "weekly-sales", targetCapabilityId = "demand-pricing",
        });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Given_unproven_target_identity_when_model_claims_compatibility_then_the_result_remains_unverified()
    {
        var model = new SuggestionModel("""{"dependency":"observed","compatibility":"compatible","transport":"http","explanation":"Similar DTO names appear compatible.","sourceKey":"source-node","targetKey":"target-node","citations":[1,2]}""");
        var result = await new IntegrationSuggestionService(model).SuggestAsync(Context());
        Assert.Equal("suggested", result.Status);
        Assert.Equal("unverified", result.Compatibility);
    }

    [Fact]
    public async Task Given_no_model_when_requesting_integration_suggestions_then_the_api_reports_model_required()
    {
        using var factory = new ExplorerFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-CE-Integration-Suggestions", "1");
        using var response = await client.PostAsJsonAsync("/api/architecture/integration-suggestions", new
        {
            sourceCapabilityId = "weekly-sales", targetCapabilityId = "demand-pricing", sourceQuery = "SalesPlanRepository",
        });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData("\"citations\":[1,2]", "\"citations\":[1,999]")]
    [InlineData("\"citations\":[1,2]", "\"citations\":[1]")]
    [InlineData("\"targetKey\":\"target-node\"", "\"targetKey\":\"invented-node\"")]
    [InlineData("\"dependency\":\"observed\"", "\"dependency\":\"confirmed\"")]
    public async Task Given_invalid_model_claims_when_validating_then_no_connection_is_adopted(string original, string changed)
    {
        const string reply = """{"dependency":"observed","compatibility":"unverified","transport":"http","explanation":"Proposed dependency.","sourceKey":"source-node","targetKey":"target-node","citations":[1,2]}""";
        var result = await new IntegrationSuggestionService(new SuggestionModel(reply.Replace(original, changed, StringComparison.Ordinal))).SuggestAsync(Context());
        Assert.Equal("insufficient-evidence", result.Status);
        Assert.Null(result.TargetKey);
        Assert.Empty(result.Citations);
    }

    [Fact]
    public async Task Given_different_contracts_when_the_model_reports_incompatibility_then_the_mismatch_is_not_hidden()
    {
        var model = new SuggestionModel("""{"dependency":"observed","compatibility":"incompatible","transport":"http","explanation":"Caller uses PUT with path identifiers; target declares PATCH with query identifiers.","sourceKey":"source-node","targetKey":"target-node","citations":[1,2]}""");
        var result = await new IntegrationSuggestionService(model).SuggestAsync(Context());
        Assert.Equal("incompatible", result.Status);
        Assert.Equal("observed", result.Dependency);
        Assert.Equal("incompatible", result.Compatibility);
    }

    [Fact]
    public async Task Given_missing_target_evidence_when_suggesting_then_the_model_is_not_called()
    {
        var model = new SuggestionModel("not json");
        var context = Context();
        var result = await new IntegrationSuggestionService(model).SuggestAsync(context with { Evidence = [context.Evidence[0]] });
        Assert.Equal("insufficient-evidence", result.Status);
        Assert.Null(model.Request);
    }

    [Fact]
    public async Task Given_changed_source_evidence_when_suggesting_then_the_input_identity_changes()
    {
        var model = new SuggestionModel("not json");
        var service = new IntegrationSuggestionService(model);
        var context = Context();
        var first = await service.SuggestAsync(context);
        var second = await service.SuggestAsync(context with { Evidence = [context.Evidence[0] with { CommitSha = new string('c', 40) }, context.Evidence[1]] });
        Assert.NotEqual(first.InputHash, second.InputHash);
        Assert.Equal("insufficient-evidence", first.Status);
    }

    [Fact]
    public async Task Given_cited_source_and_target_when_model_proposes_a_dependency_then_it_remains_a_suggestion()
    {
        var model = new SuggestionModel("""{"dependency":"observed","compatibility":"unverified","transport":"http","explanation":"Caller uses a configured gateway; target operation exists but gateway ownership is unverified.","sourceKey":"source-node","targetKey":"target-node","citations":[1,2]}""");
        var result = await new IntegrationSuggestionService(model).SuggestAsync(Context());
        Assert.Equal("suggested", result.Status);
        Assert.Equal("observed", result.Dependency);
        Assert.Equal("unverified", result.Compatibility);
        Assert.Equal(new[] { 1, 2 }, result.Citations);
        Assert.Equal("target-node", result.TargetKey);
        Assert.Equal(64, result.InputHash.Length);
        Assert.NotNull(model.Request);
        Assert.Contains("not instructions", model.Request.System, StringComparison.Ordinal);
    }

    private static IntegrationSuggestionContext Context() => new("source-capability", "target-capability", ["source-node"], ["target-node"],
        [new(1, "source", "source-repo", new string('a', 40), "Caller.cs", 1, 1, "await client.PostAsync(configuredUrl, body);"),
         new(2, "target", "target-repo", new string('b', 40), "Endpoint.cs", 1, 1, "[HttpPost(\"query\")] public Task Query()")]);

    private sealed class SuggestionModel(string response) : IInterpretationModel
    {
        public string Id => "test-integration-model";
        public ModelRequest? Request { get; private set; }
        public Task<ModelResponse> CompleteJsonAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(new ModelResponse(response));
        }
    }
}