using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CapabilityExplorer.Explorer;
using Xunit;

namespace CapabilityExplorer.Tests;

/// <summary>Navigator-payload and size-cap regressions for <c>ce.process-landscape.v1</c>, split out of
/// <see cref="ProcessLandscapeApiTests"/> (partial class) to keep each file under the 500-line repository
/// standard. Shared fixtures (<c>Host</c>, <c>Snapshots</c>, <c>Json</c>) live in the other partial file.
/// Every fixture here is synthetic: no content is copied from any private business-process workspace.</summary>
public sealed partial class ProcessLandscapeApiTests
{
    [Fact]
    public async Task NavigatorWithAnUnknownTopLevelKeyIsRejected()
    {
        await using var fixture = await Host.CreateAsync();
        var bundleBytes = Encoding.UTF8.GetBytes("{}");
        var bundleHash = Convert.ToHexStringLower(SHA256.HashData(bundleBytes));
        var navigator = new JsonObject
        {
            ["bundle"] = new JsonObject { ["path"] = "navigator/bundle.json", ["byteLength"] = bundleBytes.Length, ["sha256"] = bundleHash },
            ["verified"] = true,
            ["unknownCategory"] = new JsonArray(),
        };
        var snapshot = Snapshots.Build([Snapshots.Process("alpha", "flows/alpha.bpmn", 4, Snapshots.Sha256("abcd"))], navigator: navigator);
        await fixture.WriteAsync(snapshot);

        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("unknown-navigator-key",
            (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!.Reason);
    }

    [Fact]
    public async Task NavigatorMissingTheRequiredBundleRecordIsRejected()
    {
        await using var fixture = await Host.CreateAsync();
        var navigator = new JsonObject { ["verified"] = true, ["catalogue"] = new JsonArray() };
        var snapshot = Snapshots.Build([Snapshots.Process("alpha", "flows/alpha.bpmn", 4, Snapshots.Sha256("abcd"))], navigator: navigator);
        await fixture.WriteAsync(snapshot);

        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("missing-navigator-bundle",
            (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!.Reason);
    }

    [Fact]
    public async Task NavigatorMissingTheRequiredVerifiedFlagIsRejected()
    {
        await using var fixture = await Host.CreateAsync();
        var bundleBytes = Encoding.UTF8.GetBytes("{}");
        var bundleHash = Convert.ToHexStringLower(SHA256.HashData(bundleBytes));
        var navigator = new JsonObject
        {
            ["bundle"] = new JsonObject { ["path"] = "navigator/bundle.json", ["byteLength"] = bundleBytes.Length, ["sha256"] = bundleHash },
            ["catalogue"] = new JsonArray(),
        };
        var snapshot = Snapshots.Build([Snapshots.Process("alpha", "flows/alpha.bpmn", 4, Snapshots.Sha256("abcd"))], navigator: navigator);
        await fixture.WriteAsync(snapshot);

        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid-navigator-verified",
            (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!.Reason);
    }

    [Fact]
    public async Task NavigatorBundleCollidingWithAnAdmittedProcessPathButDisagreeingOnHashIsUnverifiableNotReconstructed()
    {
        await using var fixture = await Host.CreateAsync();
        var process = Snapshots.Process("alpha", "flows/alpha.bpmn", 4, Snapshots.Sha256("abcd"));
        // The navigator bundle claims the exact same path as the already-admitted BPMN process but disagrees on
        // byteLength/sha256: an inconsistent duplicate record, never a legitimate re-observation of one file.
        var navigator = new JsonObject
        {
            ["bundle"] = new JsonObject { ["path"] = "flows/alpha.bpmn", ["byteLength"] = 999, ["sha256"] = Snapshots.Sha256("not-the-same-bytes") },
            ["verified"] = true,
        };
        var snapshot = Snapshots.Build([process], navigator: navigator);
        await fixture.WriteAsync(snapshot);

        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!;
        Assert.Equal(ProcessLandscapeIdentityStatus.Unverifiable, body.IdentityVerification!.Status);
        Assert.Equal("navigator-bundle-path-conflicts-with-an-admitted-file", body.IdentityVerification.Reason);
    }


    /// <summary>Exercises the producer's final structural navigator shape end to end: every documented category
    /// (<c>catalogue</c>, <c>elements</c>, <c>links</c>, <c>events</c>, <c>lifecycle</c>, <c>journey</c>,
    /// <c>contexts</c>, <c>procFile</c>, <c>dmn</c>, <c>phases</c>, <c>generated</c>) plus <c>bundle</c> and
    /// <c>verified</c> must be accepted as additive structural metadata with no XML/raw bytes anywhere, and the
    /// bundle digest must still fold into the reconstructed snapshot identity. A deeply nested XML fragment buried
    /// under one of these categories must still be rejected, proving the scan is not limited to top-level keys.</summary>
    [Fact]
    public async Task StructuralNavigatorAcceptsTheFinalProducerShapeAndFoldsBundleIntoIdentity()
    {
        await using var fixture = await Host.CreateAsync();
        var bundleBytes = Encoding.UTF8.GetBytes("{\"catalogue\":[]}");
        var bundleHash = Convert.ToHexStringLower(SHA256.HashData(bundleBytes));
        var process = Snapshots.Process("alpha", "flows/alpha.bpmn", 4, Snapshots.Sha256("abcd"));

        JsonObject FullNavigator() => new()
        {
            ["bundle"] = new JsonObject { ["path"] = "navigator/bundle.json", ["byteLength"] = bundleBytes.Length, ["sha256"] = bundleHash },
            ["verified"] = true,
            ["catalogue"] = new JsonArray(new JsonObject { ["id"] = "alpha", ["label"] = "Alpha process" }),
            ["elements"] = new JsonObject { ["alpha.bpmn"] = new JsonObject { ["Task_1"] = new JsonObject { ["t"] = "task", ["n"] = "" } } },
            ["links"] = new JsonObject { ["alpha.bpmn"] = new JsonObject { ["Task_1"] = new JsonObject { ["phase"] = "" } } },
            ["events"] = new JsonObject { ["evt-1"] = new JsonObject { ["throw"] = new JsonArray(new JsonArray("alpha.bpmn", "Task_1")) } },
            ["lifecycle"] = new JsonArray(new JsonObject { ["objectId"] = "order-1", ["stage"] = "created" }),
            ["journey"] = new JsonObject { ["phases"] = new JsonArray(), ["entries"] = new JsonArray() },
            ["contexts"] = new JsonArray(new JsonObject { ["tag"] = "finance" }),
            ["procFile"] = new JsonObject { ["path"] = "flows/alpha.bpmn" },
            ["dmn"] = new JsonObject { ["decision-1"] = "Synthetic decision" },
            ["phases"] = new JsonArray(new JsonObject { ["name"] = "discovery" }),
            ["generated"] = "2024-01-01T00:00:00Z",
        };

        var accepted = Snapshots.Build([process], navigator: FullNavigator(), includeNavigatorBundleInIdentity: true);
        await fixture.WriteAsync(accepted);
        using var acceptedResponse = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.OK, acceptedResponse.StatusCode);
        var acceptedBody = (await acceptedResponse.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!;
        Assert.Equal(ProcessLandscapeIdentityStatus.Reconstructed, acceptedBody.IdentityVerification!.Status);
        Assert.Equal("snapshot-id-matches-admitted-bpmn-files-and-navigator-bundle", acceptedBody.IdentityVerification.Reason);
        Assert.Equal("alpha", acceptedBody.Snapshot!.Navigator!.Value.GetProperty("catalogue")[0].GetProperty("id").GetString());

        var withEmbeddedXml = FullNavigator();
        ((JsonObject)withEmbeddedXml["procFile"]!)["raw"] = "<bpmn:definitions/>";
        var rejected = Snapshots.Build([process], navigator: withEmbeddedXml);
        await fixture.WriteAsync(rejected);
        using var rejectedResponse = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.BadRequest, rejectedResponse.StatusCode);
        Assert.Equal("structural-navigator-embedded-content",
            (await rejectedResponse.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!.Reason);

        // The scan also rejects by keyword-named field, independent of whether the value itself looks like XML.
        var withKeywordedField = FullNavigator();
        withKeywordedField["elements"]!["alpha.bpmn"]!["Task_2"] = new JsonObject { ["base64"] = "task" };
        var rejectedByField = Snapshots.Build([process], navigator: withKeywordedField);
        await fixture.WriteAsync(rejectedByField);
        using var rejectedByFieldResponse = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.BadRequest, rejectedByFieldResponse.StatusCode);
        Assert.Equal("structural-navigator-embedded-content",
            (await rejectedByFieldResponse.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!.Reason);
    }

    [Fact]
    public async Task NavigatorBundleMismatchedAgainstTheRealIdentityIsReportedUnverifiableNotReconstructed()
    {
        await using var fixture = await Host.CreateAsync();
        var process = Snapshots.Process("alpha", "flows/alpha.bpmn", 4, Snapshots.Sha256("abcd"));
        // A bundle record is present, but the snapshot's snapshotId was computed without it, so recomputation
        // (which now folds the bundle in) can never match: this must be reported Unverifiable, never a false
        // "Reconstructed" match and never rejected outright (a mismatch is not proof that the content is wrong).
        var navigator = new JsonObject
        {
            ["bundle"] = new JsonObject { ["path"] = "navigator/bundle.json", ["byteLength"] = 2, ["sha256"] = Snapshots.Sha256("x") },
            ["verified"] = true,
        };
        var snapshot = Snapshots.Build([process], navigator: navigator);
        await fixture.WriteAsync(snapshot);

        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!;
        Assert.Equal(ProcessLandscapeIdentityStatus.Unverifiable, body.IdentityVerification!.Status);
    }

    [Fact]
    public async Task NavigatorBundleShapeIsValidated()
    {
        await using var fixture = await Host.CreateAsync();
        var process = Snapshots.Process("alpha", "flows/alpha.bpmn", 4, Snapshots.Sha256("abcd"));
        var navigator = new JsonObject { ["bundle"] = new JsonObject { ["path"] = "../escape.json", ["byteLength"] = 1, ["sha256"] = Snapshots.Sha256("x") } };
        var snapshot = Snapshots.Build([process], navigator: navigator);
        await fixture.WriteAsync(snapshot);

        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid-navigator-bundle-path",
            (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!.Reason);
    }

    [Fact]
    public void ApiSizeCapsAccommodateTheProducersNinetySixMebibyteRawInputBoundWithOverhead()
    {
        Assert.True(ProcessLandscapeSizeLimits.MaxSnapshotFileBytes > 96 * 1024 * 1024,
            "the file-read cap must exceed the producer's raw 96 MiB admitted-byte budget, not just match it");
        Assert.True(ProcessLandscapeSizeLimits.MaxResponseBytes >= ProcessLandscapeSizeLimits.MaxSnapshotFileBytes,
            "the response cap must be at least as large as the file-read cap it re-serializes");
    }

    /// <summary>The documented peak-memory budget must stay within a defensible, explicitly provisioned ceiling
    /// (2 GiB): it is not code-enforced, so this is the regression that keeps the formula itself honest if either
    /// constant it is derived from ever changes.</summary>
    [Fact]
    public void DocumentedPeakWorkingSetStaysWithinADefensibleCeiling()
    {
        const long twoGibibytes = 2L * 1024 * 1024 * 1024;
        Assert.True(ProcessLandscapeSizeLimits.ApproximatePeakWorkingSetBytes < twoGibibytes,
            "the documented peak working-set budget must stay under 2 GiB so operators can provision against a concrete, bounded number");
        Assert.Equal(
            ProcessLandscapeSizeLimits.MaxSnapshotFileBytes * 3L + ProcessLandscapeSizeLimits.MaxResponseBytes,
            ProcessLandscapeSizeLimits.ApproximatePeakWorkingSetBytes);
    }

    [Fact]
    public async Task NavigatorPayloadIsAdditiveButStillGatedByContentProfile()
    {
        await using var fixture = await Host.CreateAsync();
        var bundleBytes = Encoding.UTF8.GetBytes("{}");
        var bundleHash = Convert.ToHexStringLower(SHA256.HashData(bundleBytes));
        var navigatorWithEmbeddedXml = new JsonObject
        {
            ["bundle"] = new JsonObject { ["path"] = "navigator/bundle.json", ["byteLength"] = bundleBytes.Length, ["sha256"] = bundleHash },
            ["verified"] = true,
            ["catalogue"] = new JsonArray(new JsonObject { ["id"] = "overview", ["xml"] = "<bpmn:task/>" }),
        };
        var structural = Snapshots.Build(
            [Snapshots.Process("alpha", "flows/alpha.bpmn", 4, Snapshots.Sha256("abcd"))], navigator: navigatorWithEmbeddedXml);
        await fixture.WriteAsync(structural);
        using var rejected = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("structural-navigator-embedded-content",
            (await rejected.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!.Reason);

        fixture.Options.AllowModelContent = true;
        var bytes = "<bpmn/>"u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var withModelContent = Snapshots.Build(
            [Snapshots.Process("alpha", "flows/alpha.bpmn", bytes.Length, hash)], contentProfile: "model-content",
            sources: [new ProcessLandscapeSource("flows/alpha.bpmn", bytes.Length, hash, Convert.ToBase64String(bytes))],
            navigator: navigatorWithEmbeddedXml);
        await fixture.WriteAsync(withModelContent);
        using var accepted = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var body = (await accepted.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!;
        Assert.Equal("overview", body.Snapshot!.Navigator!.Value.GetProperty("catalogue")[0].GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("coverage")]
    [InlineData("diagnostics")]
    [InlineData("processes")]
    [InlineData("searchIndex")]
    [InlineData("crossProcessEvents")]
    [InlineData("objectLifecycle")]
    [InlineData("phaseJourney")]
    [InlineData("overlays")]
    [InlineData("sources")]
    public async Task ExplicitNullForARequiredTopLevelFieldIsRejectedAsInvalidNotA500(string field)
    {
        await using var fixture = await Host.CreateAsync();
        var snapshot = Snapshots.Build([Snapshots.Process("alpha", "flows/alpha.bpmn", 4, Snapshots.Sha256("abcd"))]);
        var node = JsonNode.Parse(JsonSerializer.Serialize(snapshot, Json))!;
        node[field] = null;
        await fixture.WriteRawAsync(node.ToJsonString());

        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid", (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!.Status);
    }

    [Theory]
    [InlineData("processes")]
    [InlineData("sources")]
    [InlineData("diagnostics")]
    public async Task ExplicitNullItemInsideARequiredTopLevelArrayIsRejectedAsInvalidNotA500(string field)
    {
        await using var fixture = await Host.CreateAsync();
        var snapshot = Snapshots.Build([Snapshots.Process("alpha", "flows/alpha.bpmn", 4, Snapshots.Sha256("abcd"))]);
        var node = JsonNode.Parse(JsonSerializer.Serialize(snapshot, Json))!;
        var array = node[field]!.AsArray();
        array.Add(null);
        await fixture.WriteRawAsync(node.ToJsonString());

        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid", (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!.Status);
    }

    [Fact]
    public async Task ExplicitNullItemInsideOverlayProcessIdentitiesIsRejectedAsInvalidNotA500()
    {
        await using var fixture = await Host.CreateAsync();
        var snapshot = Snapshots.Build([Snapshots.Process("alpha", "flows/alpha.bpmn", 4, Snapshots.Sha256("abcd"))]);
        var node = JsonNode.Parse(JsonSerializer.Serialize(snapshot, Json))!;
        var contextOverlay = new JsonObject { ["tag"] = "some-tag", ["processIdentities"] = new JsonArray((string?)null) };
        node["overlays"]!["context"]!.AsArray().Add(contextOverlay);
        await fixture.WriteRawAsync(node.ToJsonString());

        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid", (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!.Status);
    }

    [Theory]
    [InlineData("catalogue", "{}")]
    [InlineData("catalogue", "[null]")]
    [InlineData("catalogue", "[{\"procs\":[1]}]")]
    [InlineData("elements", "[]")]
    [InlineData("elements", "{\"alpha.bpmn\":[]}")]
    [InlineData("elements", "{\"alpha.bpmn\":{\"Task_1\":false}}")]
    [InlineData("elements", "{\"alpha.bpmn\":{\"Task_1\":{\"ev\":[[\"throw\",\"message\"]]}}}")]
    [InlineData("elements", "{\"alpha.bpmn\":{\"Task_1\":{\"out\":{}}}}")]
    [InlineData("links", "{\"alpha.bpmn\":{\"Task_1\":{\"phase\":[]}}}")]
    [InlineData("links", "[]")]
    [InlineData("events", "[]")]
    [InlineData("events", "{\"event-1\":{\"throw\":[[\"alpha.bpmn\"]]}}")]
    [InlineData("events", "{\"event-1\":{\"catch\":[[\"alpha.bpmn\",1]]}}")]
    [InlineData("events", "{\"event-1\":{\"ref\":null}}")]
    [InlineData("lifecycle", "[{\"states\":[{}]}]")]
    [InlineData("lifecycle", "{}")]
    [InlineData("lifecycle", "[{\"tr\":[null]}]")]
    [InlineData("lifecycle", "[{\"set\":{\"new\":[[\"alpha.bpmn\",\"Task_1\"]]}}]")]
    [InlineData("journey", "[]")]
    [InlineData("journey", "{\"phases\":[{\"contexts\":{}}]}")]
    [InlineData("journey", "{\"entries\":[false]}")]
    [InlineData("contexts", "[{\"color\":1}]")]
    [InlineData("contexts", "{}")]
    [InlineData("phases", "[\"phase-1\"]")]
    [InlineData("dmn", "{\"decision-1\":{}}")]
    [InlineData("catalogue", "null")]
    public async Task MalformedNavigatorCategoriesAreRejectedWithoutSilentlyDroppingThem(string category, string payload)
    {
        await using var fixture = await Host.CreateAsync();
        var navigator = NavigatorEnvelope();
        navigator[category] = JsonNode.Parse(payload);
        await fixture.WriteAsync(Snapshots.Build([], navigator: navigator));

        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid-navigator-category-shape",
            (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!.Reason);
    }

    [Theory]
    [InlineData("catalogue", "[{\"file\":\"../other.bpmn\"}]")]
    [InlineData("elements", "{\"/absolute.bpmn\":{}}")]
    [InlineData("elements", "{\"alpha.bpmn\":{\"Task_1\":{\"tgt\":\"../other.bpmn\"}}}")]
    [InlineData("events", "{\"event-1\":{\"ref\":[[\"../other.bpmn\",\"Task_1\"]]}}")]
    [InlineData("lifecycle", "[{\"set\":{\"new\":[[\"../other.bpmn\",\"Task_1\",\"via\"]]}}]")]
    [InlineData("journey", "{\"phases\":[{\"files\":[\"../other.bpmn\"]}]}")]
    [InlineData("journey", "{\"entries\":[{\"file\":\"../other.bpmn\"}]}")]
    [InlineData("procFile", "{\"Process_1\":\"../other.bpmn\"}")]
    [InlineData("procFile", "{\"Process_1\":{}}")]
    public async Task NavigatorFileReferencesAreSafeEvenWhenUnresolved(string category, string payload)
    {
        await using var fixture = await Host.CreateAsync();
        var navigator = NavigatorEnvelope();
        navigator[category] = JsonNode.Parse(payload);
        await fixture.WriteAsync(Snapshots.Build([], navigator: navigator));

        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid-navigator-file-reference",
            (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!.Reason);
    }

    [Fact]
    public async Task RepresentativeSourceShapesPreservePartialUnresolvedAndAdditiveData()
    {
        await using var fixture = await Host.CreateAsync();
        var navigator = NavigatorEnvelope();
        var categories = JsonNode.Parse("""
            {
              "catalogue": [{"file":"alpha.bpmn","pid":"A","group":"","name":"Synthetic","phases":"","ctx":"","inc":"","grain":"","proc":"","purpose":"","procs":["Process_1","Process_2"]},{}],
              "elements": {"alpha.bpmn":{"Task_1":{"t":"task","n":"","pr":"Process_1","lane":null,"st":null,"src":null,"dst":"Missing_1","msg":null,"ev":[["throw","message","event-1"]],"out":["Missing_1",null],"in":[],"tgt":"missing.bpmn","extension":{"retained":true}}}},
              "links": {"alpha.bpmn":{"Missing_1":{"phase":"","ctx":"","ent":"","ev":"","dmn":"","sf":"","inc":""}}},
              "events": {"event-1":{"ctx":"","kind":"non-canonical","agg":"","phase":"","throw":[["alpha.bpmn","Task_1"]],"catch":[],"ref":[["missing.bpmn","Missing_1"]]}},
              "lifecycle": [{"name":"Synthetic","entity":"","obj":"","ctx":"","states":["new"],"tr":[{"f":"","t":"new","cmd":"","ev":"","guard":"","dmn":""}],"set":{"new":[["missing.bpmn","Missing_1","unresolved"]]}},{}],
              "journey": {"phases":[{"id":"P1","name":"","gate":"","purpose":"","contexts":["missing-context"],"files":["missing.bpmn"]}],"entries":[{"name":"","file":"missing.bpmn","text":""}]},
              "contexts": [{"id":"context-1","name":"","cls":"","color":""}],
              "procFile": {"Process_1":"alpha.bpmn","MissingProcess":"missing.bpmn"},
              "dmn": {"decision-1":"Synthetic decision"},
              "phases": [{"id":"P1","name":"","gate":""}],
              "generated": {"opaque-producer-marker":true}
            }
            """)!.AsObject();
        foreach (var category in categories) navigator[category.Key] = category.Value?.DeepClone();
        var snapshot = Snapshots.Build([], navigator: navigator) with
        {
            Coverage = new("partial", ["navigator-shapes"], ["unresolved references retained"]),
            Diagnostics = [new("navigator-reference-unresolved", "Synthetic unresolved reference", "unresolved")],
        };
        await fixture.WriteAsync(snapshot);

        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!;
        Assert.True(JsonElement.DeepEquals(snapshot.Navigator!.Value, body.Snapshot!.Navigator!.Value));
        Assert.Equal("partial", body.Snapshot.Coverage.Status);
        Assert.Equal(snapshot.Coverage.Limitations, body.Snapshot.Coverage.Limitations);
        Assert.Equal(snapshot.Diagnostics, body.Snapshot.Diagnostics);
    }

    [Fact]
    public async Task UnverifiedEnvelopeWithoutCategoriesRemainsAvailable()
    {
        await using var fixture = await Host.CreateAsync();
        var navigator = NavigatorEnvelope();
        navigator["verified"] = false;
        await fixture.WriteAsync(Snapshots.Build([], navigator: navigator));

        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!;
        Assert.False(body.Snapshot!.Navigator!.Value.GetProperty("verified").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedDiagramAncestorsRemainValidOnlyWithCompleteProcessLocalParentChains(bool omitSecondPlane)
    {
        await using var fixture = await Host.CreateAsync();
        const string path = "flows/shared.bpmn";
        const string di = "http://www.omg.org/spec/BPMN/20100524/DI";
        var diagram = Snapshots.Element("diagram", path, "Diagram_1") with
        {
            Type = "BPMNDiagram", Namespace = di, Kind = "unsupported",
        };
        var plane = Snapshots.Element("plane", path, "Plane_1") with
        {
            Type = "BPMNPlane", Namespace = di, Kind = "unsupported", Parent = diagram.Identity,
        };
        ProcessLandscapeElement Shape(string seed) => Snapshots.Element(seed, path, seed) with
        {
            Type = "BPMNShape", Namespace = di, Kind = "unsupported", Parent = plane.Identity,
        };
        var alpha = Snapshots.Process("alpha", path, 4, Snapshots.Sha256("abcd"), [diagram, plane, Shape("Shape_A")]);
        var beta = Snapshots.Process("beta", path, 4, Snapshots.Sha256("abcd"),
            omitSecondPlane ? [diagram, Shape("Shape_B")] : [diagram, plane, Shape("Shape_B")]);
        await fixture.WriteAsync(Snapshots.Build([alpha, beta]));

        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(omitSecondPlane ? HttpStatusCode.BadRequest : HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!;
        if (omitSecondPlane) Assert.Equal("dangling-element-parent", body.Reason);
        else
        {
            Assert.Equal(2, body.Snapshot!.Processes.Count);
            Assert.Equal(diagram.Identity, body.Snapshot.Processes[1].Elements[0].Identity);
            Assert.Single(body.Snapshot.Sources);
        }
    }

    private static JsonObject NavigatorEnvelope() => new()
    {
        ["bundle"] = new JsonObject { ["path"] = "navigator/data.js", ["byteLength"] = 2, ["sha256"] = Snapshots.Sha256("{}") },
        ["verified"] = true,
    };
}
