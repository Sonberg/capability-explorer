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
            ["elements"] = new JsonArray(new JsonObject { ["id"] = "Task_1", ["type"] = "task" }),
            ["links"] = new JsonArray(new JsonObject { ["from"] = "Task_1", ["to"] = "Task_2" }),
            ["events"] = new JsonArray(new JsonObject { ["id"] = "evt-1" }),
            ["lifecycle"] = new JsonArray(new JsonObject { ["objectId"] = "order-1", ["stage"] = "created" }),
            ["journey"] = new JsonArray(new JsonObject { ["phase"] = "intake" }),
            ["contexts"] = new JsonArray(new JsonObject { ["tag"] = "finance" }),
            ["procFile"] = new JsonObject { ["path"] = "flows/alpha.bpmn" },
            ["dmn"] = new JsonArray(),
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
        ((JsonArray)withKeywordedField["elements"]!).Add(new JsonObject { ["id"] = "Task_2", ["base64"] = "task" });
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
}
