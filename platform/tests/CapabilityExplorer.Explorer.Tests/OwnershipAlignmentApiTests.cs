using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CapabilityExplorer.Explorer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class OwnershipAlignmentApiTests
{
    [Fact]
    public async Task DisabledAndUngrantedRequestsDoNotReadConfiguredFilesOrDisclosePaths()
    {
        await using var fixture = await Host.CreateAsync(enabled: false);
        foreach (var route in new[] { "?audience=engineering", "?repository=private-repository" })
        {
            using var response = await fixture.Client.GetAsync("/api/ownership-alignment" + route);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var raw = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(fixture.Root, raw, StringComparison.Ordinal);
            Assert.DoesNotContain("private-repository", raw, StringComparison.Ordinal);
            Assert.Empty((await response.Content.ReadFromJsonAsync<OwnershipReport>())!.Findings);
        }
        fixture.Options.Enabled = true;
        using var ungranted = await fixture.Client.GetAsync("/api/ownership-alignment?repository=private-repository");
        Assert.Equal(HttpStatusCode.Forbidden, ungranted.StatusCode);
        Assert.Equal("ownership-repository-not-granted", (await ungranted.Content.ReadFromJsonAsync<OwnershipReport>())!.Reason);
        Assert.Empty(Directory.GetFiles(fixture.Root));
    }

    [Fact]
    public async Task ActualConfiguredLoaderAndHttpProjectionPreserveParityAndNullTriage()
    {
        await using var fixture = await Host.CreateAsync();
        await fixture.WriteAsync(OwnershipTestInputs.Create());
        var engineering = await fixture.Client.GetFromJsonAsync<OwnershipReport>("/api/ownership-alignment?audience=engineering");
        var architecture = await fixture.Client.GetFromJsonAsync<OwnershipReport>("/api/ownership-alignment?audience=architecture");
        Assert.NotNull(engineering); Assert.NotNull(architecture);
        Assert.Equal("aligned", engineering.Alignment);
        Assert.Equal(engineering.InputVector, architecture.InputVector);
        Assert.Equal(OwnershipCanonical.Hash(engineering.Findings), OwnershipCanonical.Hash(architecture.Findings));
        using var response = await fixture.Client.GetAsync("/api/ownership-alignment");
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        using var wire = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, wire.RootElement.GetProperty("findings")[0].GetProperty("resolutionOwner").ValueKind);
        var inputs = OwnershipTestInputs.Snapshot(OwnershipTestInputs.Create(), "leanix", snapshot => snapshot with { Records = [] });
        await fixture.WriteAsync(inputs);
        var changed = await fixture.Client.GetFromJsonAsync<OwnershipReport>("/api/ownership-alignment");
        Assert.Equal("action-needed", changed!.Alignment);
        Assert.Equal("missing-source-record", OwnershipTestInputs.Find(changed, "identity", "leanix").Kind);
        Assert.Equal("action-needed", OwnershipTestInputs.Find(changed, "require", rule: "required-leanix").Status);
        Assert.Equal("prerequisite-not-met", OwnershipTestInputs.Find(changed, "relation").Kind);
    }

    [Theory]
    [InlineData("?audience=other")]
    [InlineData("?audience=engineering&audience=architecture")]
    [InlineData("?path=../../secret")]
    [InlineData("?root=/private")]
    public async Task RequestsNeverAcceptClientPathsOrAmbiguousParameters(string query)
    {
        await using var fixture = await Host.CreateAsync();
        using var response = await fixture.Client.GetAsync("/api/ownership-alignment" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid", (await response.Content.ReadFromJsonAsync<OwnershipReport>())!.Status);
    }

    [Fact]
    public async Task LoaderRejectsDuplicateUnknownNullFutureAndUnsafeLinkInputs()
    {
        await using var fixture = await Host.CreateAsync();
        var inputs = OwnershipTestInputs.Create();
        await fixture.WriteAsync(inputs);
        var original = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "leanix.json"));
        var malformed = new List<string>
        {
            original.Replace("\"system\":\"leanix\"", "\"system\":\"leanix\",\"system\":\"leanix\"", StringComparison.Ordinal),
            original[..^1] + ",\"unexpected\":true}",
            original.Replace("\"records\":[", "\"records\":[null,", StringComparison.Ordinal),
            original.Replace("\"native-owner\"", "null", StringComparison.Ordinal)
        };
        var future = inputs.Snapshots["leanix"].Value! with { CapturedAt = OwnershipTestInputs.Now.AddMinutes(6).ToString("O") };
        malformed.Add(JsonSerializer.Serialize(future, OwnershipTestInputs.Json));
        malformed.Add(JsonSerializer.Serialize(future with
        {
            CapturedAt = OwnershipTestInputs.Now.ToString("O"),
            Records = [future.Records.Single() with { Link = "https://user:password@example.invalid/" }]
        }, OwnershipTestInputs.Json));
        foreach (var raw in malformed)
        {
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, "leanix.json"), raw);
            var report = await fixture.Client.GetFromJsonAsync<OwnershipReport>("/api/ownership-alignment");
            Assert.Equal("invalid", OwnershipTestInputs.Find(report!, "feed", "leanix").Kind);
            Assert.DoesNotContain("password", JsonSerializer.Serialize(report), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("../outside.json")]
    [InlineData("/etc/passwd")]
    [InlineData("sub/../leanix.json")]
    [InlineData("linked.json")]
    [InlineData("linked-directory/leanix.json")]
    public async Task LoaderRejectsTraversalAndLinkedFilesOrParents(string path)
    {
        await using var fixture = await Host.CreateAsync();
        await fixture.WriteAsync(OwnershipTestInputs.Create());
        File.CreateSymbolicLink(Path.Combine(fixture.Root, "linked.json"), Path.Combine(fixture.Root, "leanix.json"));
        Directory.CreateSymbolicLink(Path.Combine(fixture.Root, "linked-directory"), fixture.Root);
        fixture.Options.Snapshots["leanix"] = path;
        var report = await fixture.Client.GetFromJsonAsync<OwnershipReport>("/api/ownership-alignment");
        Assert.Equal("invalid", OwnershipTestInputs.Find(report!, "feed", "leanix").Kind);
        Assert.DoesNotContain(fixture.Root, JsonSerializer.Serialize(report), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("freshness")]
    [InlineData("duplicate-rule")]
    [InlineData("relation")]
    [InlineData("null-list")]
    public async Task InvalidRulesCannotSupplyFreshnessExceptionsOrNotApplicable(string mutation)
    {
        await using var fixture = await Host.CreateAsync();
        await fixture.WriteAsync(OwnershipTestInputs.Create());
        var path = Path.Combine(fixture.Root, "rules.json");
        var rules = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        switch (mutation)
        {
            case "freshness": rules["freshness"]!.AsObject().Remove("backstage"); break;
            case "duplicate-rule": rules["relations"]![0]!["ruleId"] = "required-github"; break;
            case "relation": rules["relations"]![0]!["left"]!["role"] = "unrequired-role"; break;
            case "null-list": rules["exceptions"] = null; break;
        }
        await File.WriteAllTextAsync(path, rules.ToJsonString());
        var report = await fixture.Client.GetFromJsonAsync<OwnershipReport>("/api/ownership-alignment");
        Assert.Equal("rules-invalid", OwnershipTestInputs.Find(report!, "rules").Kind);
        Assert.All(report!.Coverage.Systems, source => Assert.Equal("freshness-unknown", source.State));
        Assert.DoesNotContain(report.Findings, finding => finding.Status is "aligned" or "excepted" or "not-applicable");
    }

    [Fact]
    public async Task RawEvidenceChangesInvalidateAnExceptionEvenWhenSnapshotIdentityAndVersionAreUnchanged()
    {
        await using var fixture = await Host.CreateAsync();
        var inputs = OwnershipTestInputs.Create();
        inputs = OwnershipTestInputs.Snapshot(inputs, "leanix", snapshot => snapshot with
            { Records = [snapshot.Records.Single() with { Party = new("team", "different-team") }] });
        inputs = inputs with { Crosswalk = OwnershipTestInputs.Loaded(inputs.Crosswalk.Value! with
            { Parties = [.. inputs.Crosswalk.Value!.Parties, new("party-q", [new("leanix", "team", "different-team")], "reviewed")] }) };
        await fixture.WriteAsync(inputs);
        var before = (await fixture.Client.GetFromJsonAsync<OwnershipReport>("/api/ownership-alignment"))!;
        var conflict = OwnershipTestInputs.Find(before, "relation");
        Assert.Equal("owner-conflict", conflict.Kind);
        var rulesPath = Path.Combine(fixture.Root, "rules.json");
        var rules = inputs.Rules.Value! with { Exceptions =
            [new("accepted", conflict.FindingId, conflict.Fingerprint, "Synthetic exception", "synthetic-review", OwnershipTestInputs.Now.AddDays(1).ToString("O"))] };
        await File.WriteAllTextAsync(rulesPath, JsonSerializer.Serialize(rules, OwnershipTestInputs.Json));
        var after = (await fixture.Client.GetFromJsonAsync<OwnershipReport>("/api/ownership-alignment"))!;
        Assert.Equal("excepted", OwnershipTestInputs.Find(after, "relation").Status);
        Assert.NotEqual(before.InputVector, after.InputVector);
        await File.AppendAllTextAsync(Path.Combine(fixture.Root, "leanix.json"), "\n");
        var reexported = (await fixture.Client.GetFromJsonAsync<OwnershipReport>("/api/ownership-alignment"))!;
        Assert.Equal(conflict.FindingId, OwnershipTestInputs.Find(reexported, "relation").FindingId);
        Assert.Equal("action-needed", OwnershipTestInputs.Find(reexported, "relation").Status);
        Assert.NotEqual(conflict.Fingerprint, OwnershipTestInputs.Find(reexported, "relation").Fingerprint);
        Assert.Contains(reexported.Diagnostics, diagnostic => diagnostic.Code == "exception-invalidated");
    }

    [Theory]
    [InlineData(true, "ownership-response-limit")]
    [InlineData(false, "ownership-evaluation-limit")]
    public async Task EvaluationAndWireLimitsNeverReturnACompleteLookingPartialReport(bool largeLinks, string reason)
    {
        await using var fixture = await Host.CreateAsync();
        var inputs = OwnershipTestInputs.Snapshot(OwnershipTestInputs.Create(), "github", snapshot => snapshot with
        {
            Records = Enumerable.Range(0, 1000).Select(index => new OwnershipRecord(
                "record-" + index, "subject-github", "native-owner", new("team", "team-github"),
                largeLinks ? "https://example.invalid/" + new string('x', 1600) : null)).ToArray()
        });
        if (!largeLinks)
            inputs = inputs with { Rules = OwnershipTestInputs.Loaded(inputs.Rules.Value! with
            {
                Requirements = [.. inputs.Rules.Value!.Requirements, .. Enumerable.Range(0, 124).Select(index =>
                    new OwnershipRequirement("budget-" + index, "github", "native-owner", 1, 2))],
                Relations = []
            }) };
        await fixture.WriteAsync(inputs);
        using var response = await fixture.Client.GetAsync("/api/ownership-alignment");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var report = (await response.Content.ReadFromJsonAsync<OwnershipReport>())!;
        Assert.Equal("limit", report.Status);
        Assert.Equal(reason, report.Reason);
        Assert.Equal("not-evaluable", report.Alignment);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public async Task ByteAndRecordLimitsFailExplicitlyAndMissingFilesRemainUnavailable()
    {
        await using var fixture = await Host.CreateAsync();
        await fixture.WriteAsync(OwnershipTestInputs.Create());
        var path = Path.Combine(fixture.Root, "leanix.json");
        await File.WriteAllTextAsync(path, new string(' ', 4 * 1024 * 1024 + 1));
        var oversized = await fixture.Client.GetFromJsonAsync<OwnershipReport>("/api/ownership-alignment");
        Assert.Equal("invalid", OwnershipTestInputs.Find(oversized!, "feed", "leanix").Kind);
        var inputs = OwnershipTestInputs.Snapshot(OwnershipTestInputs.Create(), "leanix", snapshot => snapshot with
        {
            Records = Enumerable.Range(0, 10_001).Select(index => new OwnershipRecord(
                "record-" + index, "subject-leanix", "native-owner", new("team", "team-leanix"))).ToArray()
        });
        await fixture.WriteAsync(inputs);
        var records = await fixture.Client.GetFromJsonAsync<OwnershipReport>("/api/ownership-alignment");
        Assert.Equal("invalid", OwnershipTestInputs.Find(records!, "feed", "leanix").Kind);
        File.Delete(path);
        var missing = await fixture.Client.GetFromJsonAsync<OwnershipReport>("/api/ownership-alignment");
        Assert.Equal("unavailable", OwnershipTestInputs.Find(missing!, "feed", "leanix").Kind);
    }

    [Fact]
    public async Task ActualProgramEntryDeniesByDefaultAndUsesIndependentConfiguredGrant()
    {
        await using var fixture = await Host.CreateAsync();
        await fixture.WriteAsync(OwnershipTestInputs.Create());
        using var deniedFactory = new EntryFactory(fixture.Options, enabled: false);
        using var denied = deniedFactory.CreateClient();
        using var response = await denied.GetAsync("/api/ownership-alignment");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var grantedFactory = new EntryFactory(fixture.Options, enabled: true);
        using var client = grantedFactory.CreateClient();
        var report = await client.GetFromJsonAsync<OwnershipReport>("/api/ownership-alignment");
        Assert.Equal("aligned", report!.Alignment);
        using var taskResponse = await client.GetAsync("/api/tasks/v1/snapshot");
        Assert.Equal(HttpStatusCode.Forbidden, taskResponse.StatusCode);
        using var hostile = new HttpRequestMessage(HttpMethod.Get, "/api/ownership-alignment");
        hostile.Headers.Host = "external.example";
        using var guarded = await client.SendAsync(hostile);
        Assert.Equal(HttpStatusCode.Forbidden, guarded.StatusCode);
    }

    private sealed class EntryFactory(OwnershipAlignmentOptions options, bool enabled) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["artifacts"] = Fixtures.ExpectedDirectory, ["source"] = "files",
                ["OwnershipAlignment:Enabled"] = enabled.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["OwnershipAlignment:Root"] = options.Root, ["OwnershipAlignment:Repositories:0"] = "svc-a",
                ["OwnershipAlignment:Snapshots:github"] = "github.json", ["OwnershipAlignment:Snapshots:leanix"] = "leanix.json",
                ["OwnershipAlignment:Snapshots:servicenow"] = "servicenow.json", ["OwnershipAlignment:Snapshots:backstage"] = "backstage.json",
                ["OwnershipAlignment:Crosswalk"] = "crosswalk.json", ["OwnershipAlignment:Rules"] = "rules.json",
            }));
            builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(new FixedClock()));
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => OwnershipTestInputs.Now;
    }

    private sealed class Host(string root, WebApplication app, HttpClient client, OwnershipAlignmentOptions options) : IAsyncDisposable
    {
        internal string Root => root;
        internal HttpClient Client => client;
        internal OwnershipAlignmentOptions Options => options;

        internal static async Task<Host> CreateAsync(bool enabled = true)
        {
            var root = Path.Combine(Fixtures.RepositoryRoot, ".sandbox-cache", "ownership-api-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddOwnershipAlignment();
            var options = new OwnershipAlignmentOptions
            {
                Enabled = enabled, Root = root, Repositories = ["svc-a"], Crosswalk = "crosswalk.json", Rules = "rules.json",
                Snapshots = OwnershipSystems.All.ToDictionary(system => system, system => system + ".json", StringComparer.Ordinal)
            };
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton<TimeProvider>(new FixedClock());
            builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);
            var app = builder.Build();
            app.MapOwnershipAlignmentApi();
            await app.StartAsync();
            return new(root, app, app.GetTestClient(), options);
        }

        internal async Task WriteAsync(OwnershipInputs inputs)
        {
            foreach (var slot in inputs.Snapshots)
                await File.WriteAllTextAsync(Path.Combine(root, slot.Key + ".json"), JsonSerializer.Serialize(slot.Value.Value, OwnershipTestInputs.Json));
            await File.WriteAllTextAsync(Path.Combine(root, "crosswalk.json"), JsonSerializer.Serialize(inputs.Crosswalk.Value, OwnershipTestInputs.Json));
            await File.WriteAllTextAsync(Path.Combine(root, "rules.json"), JsonSerializer.Serialize(inputs.Rules.Value, OwnershipTestInputs.Json));
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }
}
