using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CapabilityExplorer.Explorer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CapabilityExplorer.Tests;

/// <summary>Every fixture here is synthetic: no content is copied from any private business-process workspace.</summary>
public sealed partial class ProcessLandscapeApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public async Task SuccessReturnsCompleteStructuralSnapshotWithReconstructedIdentity()
    {
        await using var fixture = await Host.CreateAsync();
        var snapshot = Snapshots.Build([
            Snapshots.Process("alpha", "flows/alpha.bpmn", 10, Snapshots.Sha256("alpha-bytes")),
            Snapshots.Process("beta", "flows/beta.bpmn", 20, Snapshots.Sha256("beta-bytes")),
        ]);
        await fixture.WriteAsync(snapshot);

        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        var body = (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!;
        Assert.Equal("ok", body.Status);
        Assert.Equal(2, body.Snapshot!.Processes.Count);
        Assert.Equal(ProcessLandscapeIdentityStatus.Reconstructed, body.IdentityVerification!.Status);
        Assert.Equal("draft", body.Snapshot.Processes[0].ProposalStatus);
    }

    [Fact]
    public async Task StructuralProfileRejectsEmbeddedSourceBytesEvenWhenModelContentIsAllowed()
    {
        await using var fixture = await Host.CreateAsync(allowModelContent: true);
        var snapshot = Snapshots.Build(
            [Snapshots.Process("alpha", "flows/alpha.bpmn", 4, Snapshots.Sha256("abcd"))],
            sources: [new ProcessLandscapeSource("flows/alpha.bpmn", 4, Snapshots.Sha256("abcd"), Convert.ToBase64String("abcd"u8))]);
        await fixture.WriteAsync(snapshot);

        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!;
        Assert.Equal("invalid", body.Status);
        Assert.Equal("structural-source-bytes", body.Reason);
    }

    [Fact]
    public async Task ModelContentIsDeniedByDefaultAndAcceptedOnlyWhenExplicitlyOptedIn()
    {
        await using var fixture = await Host.CreateAsync(allowModelContent: false);
        var bytes = "<bpmn/>"u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var snapshot = Snapshots.Build(
            [Snapshots.Process("alpha", "flows/alpha.bpmn", bytes.Length, hash)],
            contentProfile: "model-content",
            sources: [new ProcessLandscapeSource("flows/alpha.bpmn", bytes.Length, hash, Convert.ToBase64String(bytes))]);
        await fixture.WriteAsync(snapshot);

        using var denied = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var deniedBody = (await denied.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!;
        Assert.Equal("denied", deniedBody.Status);
        Assert.Equal("model-content-disabled", deniedBody.Reason);
        Assert.Null(deniedBody.Snapshot);

        fixture.Options.AllowModelContent = true;
        using var allowed = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        var allowedBody = (await allowed.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!;
        Assert.Equal("ok", allowedBody.Status);
        Assert.Equal(bytes.Length, allowedBody.Snapshot!.Sources![0].ByteLength);
    }

    [Fact]
    public async Task MissingOversizedAndMalformedSnapshotFilesFailClearly()
    {
        await using var fixture = await Host.CreateAsync();

        using var missing = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, missing.StatusCode);
        Assert.Equal("process-landscape-snapshot-missing",
            (await missing.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!.Reason);

        using (var file = new FileStream(fixture.SnapshotPath, FileMode.Create, FileAccess.Write))
            file.SetLength(ProcessLandscapeSizeLimits.MaxSnapshotFileBytes + 1L);
        using var oversized = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        Assert.Equal("process-landscape-snapshot-too-large",
            (await oversized.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!.Reason);

        await File.WriteAllTextAsync(fixture.SnapshotPath, "{ not valid json");
        using var malformed = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal("process-landscape-snapshot-malformed",
            (await malformed.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!.Reason);

        var valid = Snapshots.Build([Snapshots.Process("alpha", "flows/alpha.bpmn", 4, Snapshots.Sha256("abcd"))]);
        var node = JsonNode.Parse(JsonSerializer.Serialize(valid, Json))!;
        node["unexpectedTopLevelMember"] = true;
        await File.WriteAllTextAsync(fixture.SnapshotPath, node.ToJsonString());
        using var unknown = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Theory]
    [InlineData("schema", "not-a-real-schema")]
    [InlineData("snapshotId", "landscape:not-hex")]
    [InlineData("repositoryId", "")]
    public async Task TopLevelContractViolationsAreRejected(string field, string value)
    {
        await using var fixture = await Host.CreateAsync();
        var snapshot = Snapshots.Build([Snapshots.Process("alpha", "flows/alpha.bpmn", 4, Snapshots.Sha256("abcd"))]);
        var node = JsonNode.Parse(JsonSerializer.Serialize(snapshot, Json))!;
        node[field] = value;
        await fixture.WriteRawAsync(node.ToJsonString());

        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("../outside.bpmn")]
    [InlineData("/etc/passwd")]
    [InlineData("flows/../../outside.bpmn")]
    public async Task ProcessPathsCannotEscapeOrBeAbsolute(string path)
    {
        await using var fixture = await Host.CreateAsync();
        // An explicit, syntactically-safe sources[] row (irrelevant to the unsafe process path under test) keeps
        // ValidateSources from being the thing that rejects first, so this exercises the process-path check itself.
        var snapshot = Snapshots.Build(
            [Snapshots.Process("alpha", path, 4, Snapshots.Sha256("abcd"))],
            sources: [new ProcessLandscapeSource("flows/placeholder.bpmn", 1, Snapshots.Sha256("placeholder"), SourceBytesBase64: null)]);
        await fixture.WriteAsync(snapshot);

        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!;
        Assert.Equal("unsafe-process-path", body.Reason);
    }

    [Fact]
    public async Task ProcessSha256MustMatchThePatternNotJustAnyString()
    {
        await using var fixture = await Host.CreateAsync();
        // An explicit sources[] row with a syntactically valid (but process-mismatching) sha256 keeps
        // ValidateSources from rejecting first, so this exercises the process-level sha256 check itself.
        var snapshot = Snapshots.Build(
            [Snapshots.Process("alpha", "flows/alpha.bpmn", 4, "not-a-sha256")],
            sources: [new ProcessLandscapeSource("flows/alpha.bpmn", 4, Snapshots.Sha256("abcd"), SourceBytesBase64: null)]);
        await fixture.WriteAsync(snapshot);

        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid-process-sha256",
            (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!.Reason);
    }

    [Fact]
    public async Task MultipleProcessesMayShareOnePathAndHashButConflictingRecordsAreRejected()
    {
        await using var fixture = await Host.CreateAsync();
        var hash = Snapshots.Sha256("shared-file");
        var consistent = Snapshots.Build([
            Snapshots.Process("alpha", "flows/multi.bpmn", 30, hash),
            Snapshots.Process("beta", "flows/multi.bpmn", 30, hash),
            Snapshots.Process("gamma", "flows/multi.bpmn", 30, hash),
        ]);
        await fixture.WriteAsync(consistent);
        using var ok = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var okBody = (await ok.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!;
        Assert.Equal(3, okBody.Snapshot!.Processes.Count);

        var conflicting = Snapshots.Build([
            Snapshots.Process("alpha", "flows/multi.bpmn", 30, hash),
            Snapshots.Process("beta", "flows/multi.bpmn", 999, hash),
        ]);
        await fixture.WriteAsync(conflicting);
        using var rejected = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        // sources[] is now the single source of truth for file identity in both profiles: the second process's
        // declared (byteLength, sha256) is checked against the one sources[] row for that path (synthesized here
        // from the first process seen), not against a sibling process directly.
        Assert.Equal("process-source-mismatch",
            (await rejected.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!.Reason);
    }

    [Fact]
    public async Task ModelContentProcessesMustJoinAnExistingSourceRecordByPathAndHash()
    {
        await using var fixture = await Host.CreateAsync(allowModelContent: true);
        var bytes = "<bpmn/>"u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var snapshot = Snapshots.Build(
            [Snapshots.Process("alpha", "flows/alpha.bpmn", bytes.Length, hash)],
            contentProfile: "model-content", sources: []);
        await fixture.WriteAsync(snapshot);

        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("process-missing-source-reference",
            (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!.Reason);
    }

    [Fact]
    public async Task ModelContentRejectsASourceRecordNoProcessReferences()
    {
        await using var fixture = await Host.CreateAsync(allowModelContent: true);
        var referencedBytes = "<bpmn id=\"a\"/>"u8.ToArray();
        var referencedHash = Convert.ToHexStringLower(SHA256.HashData(referencedBytes));
        var unreferencedBytes = "<bpmn id=\"b\"/>"u8.ToArray();
        var unreferencedHash = Convert.ToHexStringLower(SHA256.HashData(unreferencedBytes));
        var snapshot = Snapshots.Build(
            [Snapshots.Process("alpha", "flows/alpha.bpmn", referencedBytes.Length, referencedHash)],
            contentProfile: "model-content",
            sources:
            [
                new ProcessLandscapeSource("flows/alpha.bpmn", referencedBytes.Length, referencedHash, Convert.ToBase64String(referencedBytes)),
                new ProcessLandscapeSource("flows/orphan.bpmn", unreferencedBytes.Length, unreferencedHash, Convert.ToBase64String(unreferencedBytes)),
            ]);
        await fixture.WriteAsync(snapshot);

        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("unreferenced-source",
            (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!.Reason);
    }

    /// <summary>sources[] is required in both content profiles, not just model-content: a structural process
    /// whose path has no matching sources[] row must be rejected the same way a model-content one would be.</summary>
    [Fact]
    public async Task StructuralProcessesAlsoMustJoinAnExistingSourceRecordByPathAndHash()
    {
        await using var fixture = await Host.CreateAsync();
        var snapshot = Snapshots.Build(
            [Snapshots.Process("alpha", "flows/alpha.bpmn", 4, Snapshots.Sha256("abcd"))], sources: []);
        await fixture.WriteAsync(snapshot);

        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("process-missing-source-reference",
            (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!.Reason);
    }

    /// <summary>sources[] rows must omit bytes in structural, but the unreferenced-row check still applies: a
    /// structural sources[] row nobody references is rejected, same as in model-content.</summary>
    [Fact]
    public async Task StructuralProfileAlsoRejectsASourceRecordNoProcessReferences()
    {
        await using var fixture = await Host.CreateAsync();
        var snapshot = Snapshots.Build(
            [Snapshots.Process("alpha", "flows/alpha.bpmn", 4, Snapshots.Sha256("abcd"))],
            sources:
            [
                new ProcessLandscapeSource("flows/alpha.bpmn", 4, Snapshots.Sha256("abcd"), SourceBytesBase64: null),
                new ProcessLandscapeSource("flows/orphan.bpmn", 9, Snapshots.Sha256("orphan"), SourceBytesBase64: null),
            ]);
        await fixture.WriteAsync(snapshot);

        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("unreferenced-source",
            (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!.Reason);
    }

    [Fact]
    public async Task ElementSourcePathMustMatchItsOwningProcessPath()
    {
        await using var fixture = await Host.CreateAsync();
        var element = Snapshots.Element("task", "flows/other-file.bpmn", "Task_1");
        var snapshot = Snapshots.Build([Snapshots.Process("alpha", "flows/alpha.bpmn", 4, Snapshots.Sha256("abcd"), [element])]);
        await fixture.WriteAsync(snapshot);

        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("element-source-path-mismatch",
            (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!.Reason);
    }

    [Fact]
    public async Task ElementSourceElementMustMatchItsOwnId()
    {
        await using var fixture = await Host.CreateAsync();
        var element = Snapshots.Element("task", "flows/alpha.bpmn", "Task_1", sourceElementId: "Task_wrong");
        var snapshot = Snapshots.Build([Snapshots.Process("alpha", "flows/alpha.bpmn", 4, Snapshots.Sha256("abcd"), [element])]);
        await fixture.WriteAsync(snapshot);

        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("element-source-element-mismatch",
            (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!.Reason);
    }

    [Fact]
    public async Task ElementWithConsistentSourceIsAccepted()
    {
        await using var fixture = await Host.CreateAsync();
        var element = Snapshots.Element("task", "flows/alpha.bpmn", "Task_1");
        var snapshot = Snapshots.Build([Snapshots.Process("alpha", "flows/alpha.bpmn", 4, Snapshots.Sha256("abcd"), [element])]);
        await fixture.WriteAsync(snapshot);

        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ResolvedCrossProcessReferencesMustPointAtARealProcessNotADanglingOne()
    {
        await using var fixture = await Host.CreateAsync();
        var process = Snapshots.Process("alpha", "flows/alpha.bpmn", 4, Snapshots.Sha256("abcd"));
        var snapshot = Snapshots.Build([process]) with
        {
            CrossProcessEvents = [new("evt-1", new("task-1", true, ProcessIdentity: Snapshots.Identity("process", "nonexistent")),
                new("task-2", false))],
        };
        await fixture.WriteAsync(snapshot);

        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("resolved-reference-missing-process",
            (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!.Reason);
    }

    private sealed class Host(string root, WebApplication app, HttpClient client, ProcessLandscapeOptions options) : IAsyncDisposable
    {
        internal HttpClient Client => client;
        internal ProcessLandscapeOptions Options => options;
        internal string SnapshotPath => options.SnapshotPath!;

        internal static async Task<Host> CreateAsync(bool allowModelContent = false)
        {
            var root = Path.Combine(Fixtures.RepositoryRoot, ".sandbox-cache", "process-landscape-api-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddProcessLandscape();
            var options = new ProcessLandscapeOptions { SnapshotPath = Path.Combine(root, "snapshot.json"), AllowModelContent = allowModelContent };
            builder.Services.AddSingleton(options);
            builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);
            var app = builder.Build();
            app.MapProcessLandscapeApi();
            await app.StartAsync();
            return new(root, app, app.GetTestClient(), options);
        }

        internal Task WriteAsync(ProcessLandscapeSnapshot snapshot) => WriteRawAsync(JsonSerializer.Serialize(snapshot, Json));

        internal Task WriteRawAsync(string json) => File.WriteAllTextAsync(options.SnapshotPath!, json);

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Builds minimal, synthetic <c>ce.process-landscape.v1</c> snapshots for tests. <c>snapshotId</c> is
    /// computed with the same formula as <see cref="ProcessLandscapeIdentity"/> (distinct admitted files by path,
    /// "{path}\0{byteLength}\0{sha256}" sorted ordinally) so a default-built snapshot is self-consistent and
    /// reports <see cref="ProcessLandscapeIdentityStatus.Reconstructed"/>.</summary>
    private static class Snapshots
    {
        internal static string Sha256(string seed) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(seed)));

        internal static string Identity(string kind, string seed) => $"landscape-{kind}:{Sha256(seed)}";

        internal static ProcessLandscapeProcess Process(
            string seed, string path, long byteLength, string sha256, IReadOnlyList<ProcessLandscapeElement>? elements = null) => new(
            Identity("process", seed), path, byteLength, sha256, [], [],
            new("full", [], []), [], elements ?? [], [], ProposalStatus: "draft");

        /// <summary>A minimal BPMN element whose <c>Source</c> mirrors the producer invariant
        /// (<c>source: { path, element: id }</c> in <c>process-landscape-xml.ts</c>): <paramref name="sourcePath"/>
        /// and <paramref name="sourceElementId"/> are passed through verbatim so tests can also construct
        /// deliberately inconsistent fixtures.</summary>
        internal static ProcessLandscapeElement Element(string seed, string sourcePath, string id, string? sourceElementId = null) => new(
            Identity("element", seed), "bpmn", "http://www.omg.org/spec/BPMN/20100524/MODEL", id, "task", 0,
            new Dictionary<string, string>(), new ProcessLandscapeElementSource(sourcePath, sourceElementId ?? id));

        internal static ProcessLandscapeSnapshot Build(
            IReadOnlyList<ProcessLandscapeProcess> processes, string contentProfile = "structural",
            IReadOnlyList<ProcessLandscapeSource>? sources = null, JsonNode? navigator = null,
            bool includeNavigatorBundleInIdentity = false)
        {
            const string schema = "ce.process-landscape.v1";
            const string repositoryId = "synthetic-repo";
            var byPath = new Dictionary<string, (long ByteLength, string Sha256)>(StringComparer.Ordinal);
            foreach (var process in processes) byPath.TryAdd(process.Path, (process.ByteLength, process.Sha256));

            // sources[] is required in both profiles (final producer contract). Most fixtures only care about the
            // process-level assertions they're actually testing, so when a test does not pass an explicit
            // `sources:` override, auto-synthesize one identity-only (no bytes) row per distinct process path from
            // byPath above - this keeps every pre-existing structural-profile call site valid against the
            // now-mandatory collection without having to touch each one individually. Tests exercising
            // model-content, or a deliberately conflicting/missing/unreferenced sources[] shape, always pass an
            // explicit override and bypass this synthesis entirely.
            sources ??= byPath
                .Select(pair => new ProcessLandscapeSource(pair.Key, pair.Value.ByteLength, pair.Value.Sha256, SourceBytesBase64: null))
                .ToArray();

            if (includeNavigatorBundleInIdentity && navigator?["bundle"] is JsonObject bundleNode)

            {
                var bundleElement = JsonSerializer.Deserialize<JsonElement>(bundleNode.ToJsonString());
                var bundlePath = bundleElement.GetProperty("path").GetString()!;
                var bundleByteLength = bundleElement.GetProperty("byteLength").GetInt64();
                var bundleSha256 = bundleElement.GetProperty("sha256").GetString()!;
                byPath.TryAdd(bundlePath, (bundleByteLength, bundleSha256));
            }

            var records = byPath
                .Select(pair => $"{pair.Key}\0{pair.Value.ByteLength}\0{pair.Value.Sha256}")
                .OrderBy(record => record, StringComparer.Ordinal);
            var preimage = string.Join('\n', new[] { schema, repositoryId }.Concat(records));
            var snapshotId = "landscape:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(preimage)));

            JsonElement? navigatorElement = navigator is null ? null : JsonSerializer.Deserialize<JsonElement>(navigator.ToJsonString());
            return new(
                schema, snapshotId, 1, repositoryId, contentProfile, new("full", [], []), [],
                processes, [], [], [], [], new([], []), sources, navigatorElement);
        }
    }
}
