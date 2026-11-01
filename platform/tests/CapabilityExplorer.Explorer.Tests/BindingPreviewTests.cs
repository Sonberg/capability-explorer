using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CapabilityExplorer.Binding;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Explorer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class BindingPreviewTests
{
    private const string Hint = "@unbound:pricing-node/@fixture/pricing:env=FORECAST_API_URL";
    private const string Target = "forecast-cs/Forecast.Api";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static List<(LoadedFrom, Artifact)> Inputs(string forecast = "v1-base") =>
        [Load("pricing-node", "v1-base", "@fixture/pricing"), Load("forecast-cs", forecast, "Forecast.Api")];
    private static (LoadedFrom, Artifact) Load(string repo, string revision, string unit)
    {
        var artifact = ArtifactValidator.Validate(Fixtures.Golden($"{repo}/{revision}/{unit.Replace('/', '_').Replace('@', '_')}.jsonl")).Artifact!;
        return (new(repo, artifact.Header.CommitSha, artifact.Header.Unit.Path), artifact);
    }
    private static BindingPreviewRequest Request(List<(LoadedFrom, Artifact)> input, bool reviewed = false, string target = Target, BindingFile? existing = null) =>
        new(BindingPreview.Catalog(input, existing ?? BindingFile.Empty).Fingerprint, [new(Hint, target, "Reviewed source", reviewed)]);

    [Fact]
    public void Catalog_auto_groups_hints_and_binds_exact_source_hashes()
    {
        var inputs = Inputs(); var first = BindingPreview.Catalog(inputs, BindingFile.Empty);
        var second = BindingPreview.Catalog(inputs.AsEnumerable().Reverse(), BindingFile.Empty);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Contains(first.Hints, h => h.Hint == Hint && h.Occurrences.Count > 0);
        Assert.All(first.Sources, s => Assert.Equal(64, s.ArtifactHash.Length));
        Assert.Contains(Target, first.Targets);
    }
    [Fact]
    public void Preview_preserves_extractor_facts_and_requires_human_review()
    {
        var input = Inputs(); var original = input[0].Item2.Edges.ToArray();
        var result = BindingPreview.Preview(input, BindingFile.Empty, Request(input));
        Assert.Equal("evidenced", Assert.Single(result.Rows).State); Assert.False(result.CanExport);
        Assert.Equal(original, input[0].Item2.Edges);
        Assert.Throws<ArgumentException>(() => BindingPreview.Export(input, BindingFile.Empty, Request(input)));
    }
    [Fact]
    public void Export_is_reader_compatible_and_retains_source_provenance()
    {
        var input = Inputs(); var exported = BindingPreview.Export(input, BindingFile.Empty, Request(input, true));
        var parsed = BindingFile.Parse(JsonSerializer.Serialize(exported, Json));
        Assert.Equal(Target, Assert.Single(parsed.Entries).Target);
        Assert.Equal("reviewed-export-not-adopted", exported.ReviewState);
        Assert.Equal(BindingPreview.Catalog(input, BindingFile.Empty).Fingerprint, exported.SourceFingerprint);
        Assert.Equal(2, exported.Sources.Count);
    }
    [Fact]
    public void Export_preserves_unrelated_existing_mappings()
    {
        var input = Inputs(); var existing = BindingFile.Parse("""{"bindings":[{"hint":"@unbound:other/u:env=DB","target":"known-store"}]}""");
        var exported = BindingPreview.Export(input, existing, Request(input, true, existing: existing));
        Assert.Equal(2, exported.Bindings.Count); Assert.Contains(exported.Bindings, x => x.Target == "known-store");
    }
    [Fact]
    public void Stale_revision_or_existing_binding_bytes_reject_the_preview()
    {
        var input = Inputs(); var request = Request(input, true);
        Assert.Throws<BindingPreviewStaleException>(() => BindingPreview.Preview(Inputs("v2-contract"), BindingFile.Empty, request));
        Assert.Throws<BindingPreviewStaleException>(() => BindingPreview.Preview(input, BindingFile.Parse("{\"bindings\":[]}"), request));
    }
    [Fact]
    public void Same_revision_with_changed_artifact_bytes_invalidates_review()
    {
        var input = Inputs(); var request = Request(input, true);
        var text = Fixtures.Golden("pricing-node/v1-base/_fixture_pricing.jsonl");
        var changed = text.Replace(input[0].Item2.Header.ConfigHash, new string('f', 64), StringComparison.Ordinal);
        var artifact = ArtifactValidator.Validate(changed).Artifact!;
        input[0] = (input[0].Item1, artifact);
        Assert.Throws<BindingPreviewStaleException>(() => BindingPreview.Preview(input, BindingFile.Empty, request));
    }
    [Fact]
    public void Named_external_resource_remains_a_human_assertion()
    {
        var input = Inputs(); var catalog = BindingPreview.Catalog(input, BindingFile.Empty);
        var external = catalog.Hints.First(h => h.Occurrences.All(o => o.Relation != "REQUESTS"));
        var request = new BindingPreviewRequest(catalog.Fingerprint, [new(external.Hint, "reviewed-resource", "Explicit owner choice", true)]);
        var result = BindingPreview.Preview(input, BindingFile.Empty, request);
        Assert.Equal("external", Assert.Single(result.Rows).State); Assert.True(result.CanExport);
        Assert.Contains("not verified", result.Rows[0].Message, StringComparison.Ordinal);
    }
    [Fact]
    public void Source_identity_mismatch_is_rejected()
    {
        var input = Inputs(); input[0] = (input[0].Item1 with { CommitSha = new string('f', 40) }, input[0].Item2);
        Assert.Throws<ArgumentException>(() => BindingPreview.Catalog(input, BindingFile.Empty));
    }
    [Fact]
    public void Removed_endpoint_is_uncertain_and_cannot_be_exported()
    {
        var input = Inputs("v2-contract"); var request = Request(input, true);
        Assert.Equal("uncertain", Assert.Single(BindingPreview.Preview(input, BindingFile.Empty, request).Rows).State);
        Assert.Throws<ArgumentException>(() => BindingPreview.Export(input, BindingFile.Empty, request));
    }
    [Fact]
    public void Duplicate_proposals_and_existing_different_targets_are_conflicts()
    {
        var input = Inputs(); var request = Request(input, true);
        Assert.All(BindingPreview.Preview(input, BindingFile.Empty, request with { Entries = [request.Entries[0], request.Entries[0]] }).Rows, r => Assert.Equal("conflicting", r.State));
        var existing = BindingFile.Parse($$"""{"bindings":[{"hint":"{{Hint}}","target":"elsewhere/Api"}]}""");
        Assert.Equal("conflicting", Assert.Single(BindingPreview.Preview(input, existing, Request(input, true, existing: existing)).Rows).State);
    }
    [Theory]
    [InlineData("a|b")]
    [InlineData("..")]
    [InlineData("service//Api")]
    [InlineData("@unbound:x/y:env=A")]
    [InlineData("https://user:password@example.invalid")]
    [InlineData("with spaces")]
    [InlineData("destination?secret=x")]
    public void Invalid_destinations_fail_closed(string target)
    {
        var input = Inputs(); Assert.Throws<ArgumentException>(() => BindingPreview.Preview(input, BindingFile.Empty, Request(input, true, target)));
    }
    [Fact]
    public void Unknown_hint_cannot_be_injected_into_snapshot()
    {
        var input = Inputs(); var request = Request(input) with { Entries = [new("@unbound:other/u:env=X", "anything", null, true)] };
        Assert.Throws<ArgumentException>(() => BindingPreview.Preview(input, BindingFile.Empty, request));
    }

    [Fact]
    public async Task Api_requires_local_json_intent_rejects_stale_and_exports_without_database()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new ExplorerState(new ExplorerOptions([Fixtures.ExpectedDirectory], null, null,
            Path.Combine(Fixtures.RepositoryRoot, "capabilities.json"), Path.Combine(Path.GetTempPath(), "ce-preview-no-workspace"))));
        await using var app = builder.Build(); app.MapBindingPreviewApi(); await app.StartAsync();
        using var client = app.GetTestClient();
        var inputs = Inputs();
        var scope = $"?view=pricing-node:{inputs[0].Item1.CommitSha},forecast-cs:{inputs[1].Item1.CommitSha}";
        var catalog = (await client.GetFromJsonAsync<BindingPreviewCatalog>("/api/binding-preview" + scope))!;
        var request = new BindingPreviewRequest(catalog.Fingerprint, [new(Hint, Target, "human review", true)]);
        var denied = await client.PostAsJsonAsync("/api/binding-preview" + scope, request);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        client.DefaultRequestHeaders.Add("X-CE-Binding-Preview", "1");
        var valid = await client.PostAsJsonAsync("/api/binding-preview" + scope, request);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        var stale = await client.PostAsJsonAsync("/api/binding-preview" + scope, request with { ExpectedFingerprint = new string('0', 64) });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var exported = await client.PostAsJsonAsync("/api/binding-preview/export" + scope, request);
        Assert.Equal(HttpStatusCode.OK, exported.StatusCode); Assert.Contains("attachment", exported.Content.Headers.ContentDisposition!.ToString(), StringComparison.Ordinal);
        Assert.Single(BindingFile.Parse(await exported.Content.ReadAsStringAsync()).Entries);
        client.DefaultRequestHeaders.Add("Origin", "https://attacker.invalid");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/binding-preview/export" + scope, request)).StatusCode);
    }
}
