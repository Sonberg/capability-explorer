using System.Net;
using System.Net.Http.Json;
using CapabilityExplorer.Explorer;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed partial class ProcessLandscapeApiTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CallActivitiesCanReferenceDeclaredProcessRootsAcrossProcesses(bool sameFile, bool fragment)
    {
        await using var fixture = await Host.CreateAsync();
        var snapshot = CallSnapshot(sameFile, fragment ? "#CalledProcess" : "CalledProcess");
        await fixture.WriteAsync(snapshot);

        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!;
        Assert.Equal(snapshot.Processes[1].Elements[0].Identity,
            Assert.Single(body.Snapshot!.Processes[0].Relationships).Target);
        Assert.Equal(sameFile ? 1 : 2, body.Snapshot.Sources.Count);
    }

    [Theory]
    [InlineData("sequenceFlow")]
    [InlineData("messageFlow")]
    [InlineData("association")]
    public async Task OrdinaryFlowsCannotUseTheCalledProcessException(string kind)
    {
        await using var fixture = await Host.CreateAsync();
        var snapshot = CallSnapshot();
        var caller = snapshot.Processes[0];
        caller = caller with { Relationships = [caller.Relationships[0] with { Kind = kind }] };
        await fixture.WriteAsync(snapshot with { Processes = [caller, snapshot.Processes[1]] });

        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("dangling-relationship-target",
            (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!.Reason);
    }

    [Theory]
    [InlineData("wrong-target-type")]
    [InlineData("wrong-target-namespace")]
    [InlineData("wrong-target-kind")]
    [InlineData("undeclared-root")]
    [InlineData("missing-process-id")]
    [InlineData("wrong-source-type")]
    [InlineData("wrong-source-namespace")]
    [InlineData("wrong-source-kind")]
    [InlineData("missing-called-element")]
    [InlineData("mismatched-called-element")]
    [InlineData("qname")]
    [InlineData("external-uri")]
    [InlineData("unresolved-target")]
    [InlineData("unadmitted-target")]
    [InlineData("colliding-root-identity")]
    public async Task CrossProcessReferencesRejectSpoofedOrUnresolvedTargets(string invalidity)
    {
        await using var fixture = await Host.CreateAsync();
        var snapshot = CallSnapshot();
        var caller = snapshot.Processes[0];
        var callee = snapshot.Processes[1];
        var call = caller.Elements[1];
        var root = callee.Elements[0];
        var relationship = caller.Relationships[0];
        switch (invalidity)
        {
            case "wrong-target-type": root = root with { Type = "task" }; break;
            case "wrong-target-namespace": root = root with { Namespace = "urn:synthetic:extension" }; break;
            case "wrong-target-kind": root = root with { Kind = "unsupported" }; break;
            case "undeclared-root": callee = callee with { ProcessId = "OtherProcess" }; break;
            case "missing-process-id": callee = callee with { ProcessId = null }; break;
            case "wrong-source-type": call = call with { Type = "task" }; break;
            case "wrong-source-namespace": call = call with { Namespace = "urn:synthetic:extension" }; break;
            case "wrong-source-kind": call = call with { Kind = "unsupported" }; break;
            case "missing-called-element": call = call with { Attributes = new Dictionary<string, string>() }; break;
            case "mismatched-called-element":
            case "qname":
            case "external-uri":
                call = call with
                {
                    Attributes = new Dictionary<string, string>
                    {
                        ["calledElement"] = invalidity switch
                        {
                            "qname" => "prefix:CalledProcess",
                            "external-uri" => "other.bpmn#CalledProcess",
                            _ => "MissingProcess",
                        },
                    },
                };
                break;
            case "unresolved-target": relationship = relationship with { Target = Snapshots.Identity("element", "missing") }; break;
        }
        caller = caller with { Elements = [caller.Elements[0], call], Relationships = [relationship] };
        callee = callee with { Elements = [root] };
        IReadOnlyList<ProcessLandscapeProcess> processes = invalidity == "unadmitted-target" ? [caller] : [caller, callee];
        if (invalidity == "colliding-root-identity")
        {
            var collision = Snapshots.Element("collision", "flows/third.bpmn", "SpoofTask") with { Identity = root.Identity };
            processes = [caller, callee, Snapshots.Process("third", "flows/third.bpmn", 4, Snapshots.Sha256("abcd"), [collision])];
        }
        await fixture.WriteAsync(Snapshots.Build(processes));

        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("dangling-relationship-target",
            (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!.Reason);
    }

    [Fact]
    public async Task UnresolvedCallWithoutAnInventedRelationshipRemainsPartialAndAvailable()
    {
        await using var fixture = await Host.CreateAsync();
        var caller = CallSnapshot(calledElement: "MissingProcess").Processes[0] with
        {
            Relationships = [],
            Coverage = new("partial", [], ["unresolved-called-element"]),
            Diagnostics = [new("unresolved-called-element", "Synthetic unresolved call", "unresolved")],
        };
        await fixture.WriteAsync(Snapshots.Build([caller]));

        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!;
        Assert.Equal("partial", body.Snapshot!.Processes[0].Coverage.Status);
        Assert.Equal("unresolved", Assert.Single(body.Snapshot.Processes[0].Diagnostics).Disposition);
        Assert.Empty(body.Snapshot.Processes[0].Relationships);
    }

    private static ProcessLandscapeSnapshot CallSnapshot(bool sameFile = false, string calledElement = "CalledProcess")
    {
        const string callerPath = "flows/caller.bpmn";
        var calleePath = sameFile ? callerPath : "flows/callee.bpmn";
        var callerRoot = Snapshots.Element("caller-root", callerPath, "CallerProcess") with { Type = "process" };
        var call = Snapshots.Element("call", callerPath, "Call_1") with
        {
            Type = "callActivity", Parent = callerRoot.Identity,
            Attributes = new Dictionary<string, string> { ["calledElement"] = calledElement },
        };
        var calleeRoot = Snapshots.Element("callee-root", calleePath, "CalledProcess") with { Type = "process" };
        var caller = Snapshots.Process("caller", callerPath, 4, Snapshots.Sha256("abcd"), [callerRoot, call]) with
        {
            ProcessId = "CallerProcess",
            Relationships = [new(Snapshots.Identity("relationship", "call"), "reference", call.Identity, calleeRoot.Identity, 0)],
        };
        var callee = Snapshots.Process("callee", calleePath, 4, Snapshots.Sha256("abcd"), [calleeRoot]) with { ProcessId = "CalledProcess" };
        return Snapshots.Build([caller, callee]);
    }
}
