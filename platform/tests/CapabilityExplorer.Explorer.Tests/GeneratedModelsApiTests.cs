using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class GeneratedModelsApiTests : IDisposable
{
    private readonly CapabilityExplorerFactory _factory = new();
    private readonly HttpClient _client;

    public GeneratedModelsApiTests() => _client = _factory.CreateClient();

    [Fact]
    public async Task Given_default_deny_policy_when_generated_model_authorization_is_read_then_all_data_grants_are_denied()
    {
        var authorization = await _client.GetFromJsonAsync<JsonElement>("/api/generated-models/settings");

        Assert.False(authorization.GetProperty("modelEvidenceAccessGranted").GetBoolean());
        Assert.False(authorization.GetProperty("sourceExcerptAccessGranted").GetBoolean());
        Assert.False(authorization.GetProperty("providerAuthorized").GetBoolean());
        Assert.False(authorization.GetProperty("sourceSnippetsAuthorized").GetBoolean());
    }

    [Fact]
    public async Task Given_no_previous_run_when_generated_model_status_is_read_then_status_is_empty()
    {
        using var response = await _client.GetAsync("/api/generated-models/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Given_no_successful_result_when_generated_model_result_is_requested_then_not_found_is_returned()
    {
        using var response = await _client.GetAsync("/api/generated-models");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
