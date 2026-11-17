using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CapabilityExplorer.Explorer;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed partial class ProcessLandscapeApiTests
{
    private const string DocumentPath = "flows/collaboration.bpmn";
    private const string DocumentBpmn = "http://www.omg.org/spec/BPMN/20100524/MODEL";
    private const string DocumentDi = "http://www.omg.org/spec/BPMN/20100524/DI";

    [Theory]
    [InlineData("activities")]
    [InlineData("participants")]
    [InlineData("blackbox")]
    [InlineData("unresolved-process-ref")]
    [InlineData("duplicate-process-ref-explicit")]
    [InlineData("mixed")]
    public async Task OptionalDocumentsPreserveAuthoredCollaborationAndSourceJoins(string endpoints)
    {
        await using var fixture = await Host.CreateAsync();
        var snapshot = DocumentSnapshot();
        if (endpoints != "activities")
            snapshot = SetDocumentEndpoints(snapshot, endpoints == "mixed" ? "Task_A" : "Pool_A", "Pool_B");
        if (endpoints == "blackbox")
        {
            snapshot = ChangeDocumentElement(snapshot, "Pool_A", element => element with { Attributes = new Dictionary<string, string>() });
            snapshot = ChangeDocumentElement(snapshot, "Pool_B", element => element with { Attributes = new Dictionary<string, string>() });
        }
        if (endpoints is "unresolved-process-ref" or "duplicate-process-ref-explicit")
            snapshot = ChangeDocumentElement(snapshot, "Pool_A", element => element with
            {
                Attributes = new Dictionary<string, string>
                {
                    ["processRef"] = endpoints == "unresolved-process-ref" ? "Missing" : "Process_B",
                },
            });
        await fixture.WriteAsync(snapshot);

        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!;
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(snapshot.Documents, Json),
            JsonSerializer.SerializeToElement(body.Snapshot!.Documents, Json)));
        Assert.Equal(ProcessLandscapeIdentityStatus.Reconstructed, body.IdentityVerification!.Status);
        Assert.Single(body.Snapshot.Sources);
    }

    [Fact]
    public async Task DocumentsRemainOptionalForOldArtifacts()
    {
        await using var fixture = await Host.CreateAsync();
        await fixture.WriteAsync(Snapshots.Build([]));
        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("snapshot").TryGetProperty("documents", out _));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[{\"path\":\"flows/collaboration.bpmn\",\"elements\":null,\"relationships\":[]}]")]
    [InlineData("[{\"path\":\"flows/collaboration.bpmn\",\"elements\":[null],\"relationships\":[]}]")]
    [InlineData("[{\"path\":\"flows/collaboration.bpmn\",\"elements\":[],\"relationships\":[null]}]")]
    [InlineData("[{\"path\":\"flows/collaboration.bpmn\",\"elements\":[],\"relationships\":[],\"xml\":\"hidden\"}]")]
    public async Task MalformedOptionalDocumentsAreInvalidNotServerErrors(string json)
    {
        await using var fixture = await Host.CreateAsync();
        var node = JsonSerializer.SerializeToNode(DocumentSnapshot(), Json)!;
        node["documents"] = JsonNode.Parse(json);
        await fixture.WriteRawAsync(node.ToJsonString());
        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid", (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!.Status);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("123")]
    public async Task OptionalDocumentInspectionStillRejectsNonObjectSnapshotRoots(string json)
    {
        await using var fixture = await Host.CreateAsync();
        await fixture.WriteRawAsync(json);
        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("carrier-namespace", "invalid-message-flow-carrier")]
    [InlineData("carrier-type", "invalid-message-flow-carrier")]
    [InlineData("flow-kind", "invalid-document-relationship-kind")]
    [InlineData("missing-carrier", "invalid-message-flow-carrier")]
    [InlineData("spoofed-endpoint", "invalid-message-flow-endpoint")]
    [InlineData("unadmitted-endpoint", "invalid-message-flow-endpoint")]
    [InlineData("endpoint-namespace", "unresolved-message-flow-participation")]
    [InlineData("gateway-endpoint", "unresolved-message-flow-participation")]
    [InlineData("root-endpoint", "unresolved-message-flow-participation")]
    [InlineData("same-pool", "unresolved-message-flow-participation")]
    [InlineData("ambiguous-pool", "unresolved-message-flow-participation")]
    [InlineData("missing-pool", "unresolved-message-flow-participation")]
    [InlineData("invalid-process-ref", "unresolved-message-flow-participation")]
    [InlineData("foreign-collaboration", "unresolved-message-flow-participation")]
    [InlineData("nested-carrier", "invalid-message-flow-scope")]
    [InlineData("duplicate-carrier", "invalid-message-flow-carrier")]
    [InlineData("unreported-carrier", "unreported-document-message-flow")]
    [InlineData("cross-process-sequence", "dangling-relationship-target")]
    [InlineData("duplicate-path", "duplicate-document-path")]
    [InlineData("source-join", "document-source-mismatch")]
    [InlineData("foreign-element-source", "element-source-path-mismatch")]
    public async Task DocumentRelationshipsCannotInventOrMisattributeMessageFlows(string failure, string reason)
    {
        await using var fixture = await Host.CreateAsync();
        var snapshot = DocumentSnapshot();
        var document = snapshot.Documents![0];
        var edge = document.Relationships[0];
        switch (failure)
        {
            case "carrier-namespace":
                snapshot = ChangeDocumentElement(snapshot, "Message_1", element => element with { Namespace = "urn:extension" }); break;
            case "carrier-type":
                snapshot = ChangeDocumentElement(snapshot, "Message_1", element => element with { Type = "sequenceFlow" }); break;
            case "flow-kind": edge = edge with { Kind = "sequenceFlow" }; break;
            case "missing-carrier": edge = edge with { ElementIdentity = DocId("Missing") }; break;
            case "spoofed-endpoint":
                snapshot = ChangeDocumentElement(snapshot, "Message_1", element => element with
                {
                    Attributes = new Dictionary<string, string> { ["sourceRef"] = "Pool_A", ["targetRef"] = "Task_B" },
                });
                break;
            case "unadmitted-endpoint": edge = edge with { Source = DocId("Missing") }; break;
            case "endpoint-namespace":
            case "gateway-endpoint":
                var caller = snapshot.Processes[0];
                var task = caller.Elements[1];
                task = failure == "endpoint-namespace" ? task with { Namespace = "urn:extension" } : task with { Type = "exclusiveGateway" };
                snapshot = snapshot with { Processes = [caller with { Elements = [caller.Elements[0], task, .. caller.Elements.Skip(2)] }, snapshot.Processes[1]] };
                break;
            case "root-endpoint": snapshot = SetDocumentEndpoints(snapshot, "Process_A", "Task_B"); break;
            case "same-pool": snapshot = SetDocumentEndpoints(snapshot, "Task_A", "Task_A"); break;
            case "ambiguous-pool":
                snapshot = snapshot with
                {
                    Documents = [document with { Elements = [.. document.Elements, DocumentElement("DuplicatePool", "participant", "Collaboration") with
                    {
                        Attributes = new Dictionary<string, string> { ["processRef"] = "Process_A" },
                    }] }],
                };
                break;
            case "missing-pool":
                snapshot = snapshot with { Documents = [document with { Elements = document.Elements.Where(element => element.Id != "Pool_A").ToArray() }] };
                break;
            case "invalid-process-ref":
                snapshot = ChangeDocumentElement(snapshot, "Pool_A", element => element with
                {
                    Attributes = new Dictionary<string, string> { ["processRef"] = "#Process_A" },
                });
                break;
            case "foreign-collaboration":
            case "nested-carrier":
                snapshot = snapshot with
                {
                    Documents = [document with { Elements = [.. document.Elements,
                        DocumentElement("Other", failure == "foreign-collaboration" ? "collaboration" : "extension", "Definitions")] }],
                };
                snapshot = ChangeDocumentElement(snapshot, failure == "foreign-collaboration" ? "Pool_A" : "Message_1",
                    element => element with { Parent = DocId("Other") });
                break;
            case "duplicate-carrier":
                snapshot = snapshot with { Documents = [document with { Relationships = [edge, edge with { Identity = Snapshots.Identity("relationship", "duplicate") }] }] };
                break;
            case "unreported-carrier":
                snapshot = snapshot with { Documents = [document with { Relationships = [] }] }; break;
            case "cross-process-sequence":
                var process = snapshot.Processes[0] with
                {
                    Relationships = [new(Snapshots.Identity("relationship", "cross"), "sequenceFlow", DocId("Task_A"), DocId("Task_B"), 0)],
                };
                snapshot = snapshot with { Processes = [process, snapshot.Processes[1]] }; break;
            case "duplicate-path": snapshot = snapshot with { Documents = [document, document] }; break;
            case "source-join": snapshot = snapshot with { Documents = [document with { Path = "flows/unadmitted.bpmn" }] }; break;
            case "foreign-element-source":
                snapshot = ChangeDocumentElement(snapshot, "Definitions", element => element with
                {
                    Source = element.Source with { Path = "flows/foreign.bpmn" },
                });
                break;
        }
        if (failure is "flow-kind" or "missing-carrier" or "unadmitted-endpoint")
            snapshot = snapshot with { Documents = [document with { Relationships = [edge] }] };
        await fixture.WriteAsync(snapshot);
        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(reason, (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!.Reason);
    }

    [Fact]
    public async Task UnresolvedDocumentCarriersRemainAvailableOnlyWithExplicitSourceDiagnostics()
    {
        await using var fixture = await Host.CreateAsync();
        var snapshot = DocumentSnapshot();
        snapshot = snapshot with
        {
            Documents = [snapshot.Documents![0] with { Relationships = [] }],
            Coverage = new("partial", [], ["ambiguous-message-flow-participant"]),
            Diagnostics = [new("ambiguous-message-flow-participant", "Synthetic unresolved pool", "unresolved", new(DocumentPath, "Message_1"))],
        };
        await fixture.WriteAsync(snapshot);
        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!;
        Assert.Empty(body.Snapshot!.Documents![0].Relationships);
        Assert.Contains(body.Snapshot.Documents[0].Elements, element => element.Id == "Message_1");
        Assert.Equal("unresolved", Assert.Single(body.Snapshot.Diagnostics).Disposition);
    }

    private static string DocId(string id) => Snapshots.Identity("element", "document-" + id);

    private static ProcessLandscapeElement DocumentElement(string id, string type, string? parent = null, string ns = DocumentBpmn) =>
        Snapshots.Element("document-" + id, DocumentPath, id) with
        {
            Type = type, Namespace = ns, Kind = ns == DocumentBpmn ? "bpmn" : "unsupported",
            Parent = parent is null ? null : DocId(parent),
        };

    private static ProcessLandscapeSnapshot ChangeDocumentElement(
        ProcessLandscapeSnapshot snapshot, string id, Func<ProcessLandscapeElement, ProcessLandscapeElement> change) =>
        snapshot with { Documents = [snapshot.Documents![0] with
        {
            Elements = snapshot.Documents[0].Elements.Select(element => element.Id == id ? change(element) : element).ToArray(),
        }] };

    private static ProcessLandscapeSnapshot SetDocumentEndpoints(ProcessLandscapeSnapshot snapshot, string source, string target)
    {
        snapshot = ChangeDocumentElement(snapshot, "Message_1", element => element with
        {
            Attributes = new Dictionary<string, string> { ["sourceRef"] = source, ["targetRef"] = target },
        });
        return snapshot with { Documents = [snapshot.Documents![0] with
        {
            Relationships = [snapshot.Documents[0].Relationships[0] with { Source = DocId(source), Target = DocId(target) }],
        }] };
    }

    private static ProcessLandscapeSnapshot DocumentSnapshot()
    {
        var diagram = DocumentElement("Diagram", "BPMNDiagram", "Definitions", DocumentDi);
        var plane = DocumentElement("Plane", "BPMNPlane", "Diagram", DocumentDi) with
        {
            Attributes = new Dictionary<string, string> { ["bpmnElement"] = "Collaboration" },
        };
        var shape = DocumentElement("Shape", "BPMNShape", "Plane", DocumentDi) with
        {
            Attributes = new Dictionary<string, string> { ["bpmnElement"] = "Task_A" },
        };
        var bounds = DocumentElement("Bounds", "Bounds", "Shape", "http://www.omg.org/spec/DD/20100524/DC") with
        {
            Attributes = new Dictionary<string, string> { ["x"] = "0", ["y"] = "0", ["width"] = "120", ["height"] = "80" },
        };
        var elements = new ProcessLandscapeElement[]
        {
            DocumentElement("Definitions", "definitions"),
            DocumentElement("Collaboration", "collaboration", "Definitions"),
            DocumentElement("Pool_A", "participant", "Collaboration") with { Attributes = new Dictionary<string, string> { ["processRef"] = "Process_A" } },
            DocumentElement("Pool_B", "participant", "Collaboration") with { Attributes = new Dictionary<string, string> { ["processRef"] = "Process_B" } },
            DocumentElement("Message_1", "messageFlow", "Collaboration") with
            {
                Attributes = new Dictionary<string, string> { ["sourceRef"] = "Task_A", ["targetRef"] = "Task_B" },
            },
            diagram, plane, shape, bounds,
        };
        var caller = Snapshots.Process("doc-a", DocumentPath, 4, Snapshots.Sha256("abcd"),
            [DocumentElement("Process_A", "process"), DocumentElement("Task_A", "sendTask", "Process_A"),
                diagram with { Parent = null }, plane, shape, bounds]) with { ProcessId = "Process_A" };
        var callee = Snapshots.Process("doc-b", DocumentPath, 4, Snapshots.Sha256("abcd"),
            [DocumentElement("Process_B", "process"), DocumentElement("Task_B", "receiveTask", "Process_B")]) with { ProcessId = "Process_B" };
        return Snapshots.Build([caller, callee]) with
        {
            Documents = [new(DocumentPath, elements, [new(Snapshots.Identity("relationship", "doc-message"),
                "messageFlow", DocId("Task_A"), DocId("Task_B"), 0, DocId("Message_1"))])],
        };
    }
}
