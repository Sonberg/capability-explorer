using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CapabilityExplorer.Explorer;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed partial class ProcessLandscapeApiTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("""{"xml":"hidden"}""")]
    [InlineData("""{"note":"<bpmn:task/>"}""")]
    [InlineData("""{"extension":{"sourceBytesBase64":"hidden"}}""")]
    public async Task DictionaryLabelsDoNotMasqueradeAsCarriersButNestedRecordContentIsStillScanned(string? embedded)
    {
        await using var fixture = await Host.CreateAsync();
        var navigator = NavigatorEnvelope();
        var categories = JsonNode.Parse("""
            {
              "elements":{"example.bpmn20.xml":{"XmlTask":{"t":"task","n":""}}},
              "links":{"example.bpmn20.xml":{"XmlTask":{"phase":""}}},
              "events":{"XmlReady":{"throw":[["example.bpmn20.xml","XmlTask"]]},"Base64EnvelopeQueued":{}},
              "lifecycle":[{"states":["XmlState"],"set":{"XmlState":[["example.bpmn20.xml","XmlTask","synthetic"]]}}],
              "procFile":{"XmlProcess":"example.bpmn20.xml"},
              "dmn":{"XmlDecision":"Synthetic label"}
            }
            """)!.AsObject();
        foreach (var category in categories) navigator[category.Key] = category.Value?.DeepClone();
        if (embedded is not null) navigator["events"]!["XmlReady"]!["detail"] = JsonNode.Parse(embedded);
        var snapshot = Snapshots.Build([], navigator: navigator);
        await fixture.WriteAsync(snapshot);

        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(embedded is null ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!;
        if (embedded is null)
            Assert.True(JsonElement.DeepEquals(snapshot.Navigator!.Value, body.Snapshot!.Navigator!.Value));
        else
            Assert.Equal("structural-navigator-embedded-content", body.Reason);
    }
}
