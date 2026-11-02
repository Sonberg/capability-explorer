using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CapabilityExplorer.Contracts;
using Json.Schema;
using Xunit;

namespace CapabilityExplorer.Tests;

/// <summary>Draft envelope shape conformance only, not an intake service, authorization or artifact verifier.</summary>
public sealed class PlatformExchangeContractTests
{
    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromText(File.ReadAllText(
        Path.Combine(Fixtures.RepositoryRoot, "schema", "platform", "v1", "envelope.schema.json"))));

    private static JsonObject Example(string name) => JsonNode.Parse(File.ReadAllText(
        Path.Combine(Fixtures.RepositoryRoot, "tests", "fixtures", "platform", name + ".json")))!.AsObject();

    private static bool IsValid(JsonObject value, bool formats = true)
    {
        using var document = JsonDocument.Parse(value.ToJsonString());
        return Schema.Value.Evaluate(document.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.Flag,
            RequireFormatValidation = formats
        }).IsValid;
    }

    private static void Set(JsonObject value, string path, JsonNode? replacement)
    {
        var parts = path.Split('.');
        JsonNode current = value;
        foreach (var part in parts[..^1])
        {
            current = current is JsonArray array
                ? array[int.Parse(part, CultureInfo.InvariantCulture)]!
                : current[part]!;
        }

        current[parts[^1]] = replacement;
    }

    [Theory]
    [InlineData("enrollment")]
    [InlineData("bundle-full")]
    [InlineData("bundle-partial")]
    [InlineData("receipt-received")]
    [InlineData("receipt-validated")]
    [InlineData("receipt-published")]
    [InlineData("receipt-rejected")]
    [InlineData("receipt-quarantined")]
    public void Controlled_examples_conform_to_their_draft_profiles(string name) => Assert.True(IsValid(Example(name)));

    [Theory]
    [InlineData("enrollment", "enrollmentId")]
    [InlineData("enrollment", "registryRevisionHash")]
    [InlineData("enrollment", "repository.repositoryId")]
    [InlineData("enrollment", "repository.providerRepositoryId")]
    [InlineData("enrollment", "repository.authority")]
    [InlineData("enrollment", "repository.locator")]
    [InlineData("bundle-full", "content.commitSha")]
    [InlineData("bundle-full", "contentSha256")]
    [InlineData("bundle-full", "content.artifacts.0.path")]
    [InlineData("bundle-full", "content.artifacts.0.extractor.name")]
    [InlineData("bundle-full", "content.artifacts.0.extractor.version")]
    [InlineData("bundle-full", "publicationAttempt.observedAt")]
    [InlineData("receipt-rejected", "errors.0.code")]
    [InlineData("receipt-rejected", "errors.0.message")]
    public void Terminal_newlines_are_not_accepted_by_dotnet_regex_anchors(string name, string path)
    {
        var value = Example(name);
        JsonNode current = value;
        foreach (var part in path.Split('.'))
            current = current is JsonArray array ? array[int.Parse(part, CultureInfo.InvariantCulture)]! : current[part]!;
        Set(value, path, JsonValue.Create(current.GetValue<string>() + "\n"));
        Assert.False(IsValid(value));
    }

    [Theory]
    [InlineData("enrollment")]
    [InlineData("bundle-full")]
    [InlineData("receipt-received")]
    public void Wire_family_and_profile_cannot_be_silently_changed(string name)
    {
        var value = Example(name);
        value["schema"] = "ce.v1";
        Assert.False(IsValid(value));
        value["schema"] = "ce.platform.v1";
        value["kind"] = "model-proposal";
        Assert.False(IsValid(value));
    }

    [Theory]
    [InlineData("enrollment", "producerRoleIds")]
    [InlineData("enrollment", "repository")]
    [InlineData("enrollment", "membership")]
    [InlineData("bundle-full", "content")]
    [InlineData("bundle-full", "contentSha256")]
    [InlineData("bundle-full", "publicationAttempt")]
    [InlineData("receipt-received", "status")]
    [InlineData("receipt-received", "recordedAt")]
    [InlineData("receipt-received", "errors")]
    public void Required_boundaries_are_not_optional(string name, string field)
    {
        var value = Example(name);
        Assert.True(value.Remove(field));
        Assert.False(IsValid(value));
    }

    [Theory]
    [InlineData("enrollment", "state", "\"guessed\"")]
    [InlineData("enrollment", "enrollmentVersion", "0")]
    [InlineData("enrollment", "enrollmentVersion", "9007199254740992")]
    [InlineData("enrollment", "registryRevisionHash", "\"short\"")]
    [InlineData("enrollment", "producerRoleIds", "[]")]
    [InlineData("enrollment", "producerRoleIds", "[\"role:one\",\"role:one\"]")]
    [InlineData("enrollment", "repository.repositoryId", "\"../other\"")]
    [InlineData("enrollment", "repository.providerRepositoryId", "\"repo-name\"")]
    [InlineData("enrollment", "repository.authority", "\"https://github.com\"")]
    [InlineData("enrollment", "repository.authority", "\"github..com\"")]
    [InlineData("enrollment", "repository.locator", "\"http://github.com/example/repo\"")]
    [InlineData("enrollment", "repository.locator", "\"https://user:secret@github.com/example/repo\"")]
    [InlineData("enrollment", "repository.locator", "\"https://github.com/example/repo?token=secret\"")]
    [InlineData("enrollment", "repository.locator", "\"https://github.com/example/repo#fragment\"")]
    [InlineData("enrollment", "membership.clusterId", "\"\"")]
    [InlineData("bundle-full", "content.commitSha", "\"main\"")]
    [InlineData("bundle-full", "content.repositoryId", "\"API Uppercase\"")]
    [InlineData("bundle-full", "content.artifacts", "[]")]
    [InlineData("bundle-full", "content.artifacts.0.artifactSchema", "\"ce.v2\"")]
    [InlineData("bundle-full", "content.artifacts.0.sha256", "\"not-a-digest\"")]
    [InlineData("bundle-full", "content.artifacts.0.byteLength", "0")]
    [InlineData("bundle-full", "content.artifacts.0.byteLength", "-1")]
    [InlineData("bundle-full", "content.artifacts.0.byteLength", "16777217")]
    [InlineData("bundle-full", "content.artifacts.0.byteLength", "1.5")]
    [InlineData("bundle-full", "content.artifacts.0.extractor.version", "\"\"")]
    [InlineData("bundle-full", "content.artifacts.0.configHash", "\"short\"")]
    [InlineData("bundle-full", "content.artifacts.0.inputHash", "\"short\"")]
    [InlineData("bundle-full", "content.coverage.status", "\"unknown-means-full\"")]
    [InlineData("bundle-full", "content.coverage.omittedUnitPaths", "[\"missing.csproj\"]")]
    [InlineData("receipt-received", "status", "\"accepted-means-published\"")]
    [InlineData("receipt-received", "errors", "[{\"code\":\"failure\",\"message\":\"unexpected\"}]")]
    [InlineData("receipt-validated", "errors", "[{\"code\":\"failure\",\"message\":\"unexpected\"}]")]
    [InlineData("receipt-published", "errors", "[{\"code\":\"failure\",\"message\":\"unexpected\"}]")]
    [InlineData("receipt-published", "publication.generation", "0")]
    [InlineData("receipt-published", "publication.inputRevisionVectorHash", "\"main\"")]
    [InlineData("receipt-rejected", "errors", "[]")]
    [InlineData("receipt-quarantined", "errors", "[]")]
    [InlineData("receipt-rejected", "errors.0.artifactPath", "\"../outside.jsonl\"")]
    [InlineData("receipt-rejected", "errors.0.message", "\"raw\\nsource\"")]
    public void Invalid_profile_values_fail(string name, string path, string replacement)
    {
        var value = Example(name);
        Set(value, path, JsonNode.Parse(replacement));
        Assert.False(IsValid(value));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../outside.jsonl")]
    [InlineData("artifacts/../outside.jsonl")]
    [InlineData("artifacts/./input.jsonl")]
    [InlineData("/absolute.jsonl")]
    [InlineData("C:/absolute.jsonl")]
    [InlineData("artifacts\\input.jsonl")]
    [InlineData("artifacts//input.jsonl")]
    [InlineData("artifacts/")]
    [InlineData("artifacts/input\n.jsonl")]
    [InlineData("artifacts/input\0.jsonl")]
    public void Artifact_and_unit_paths_must_be_literal_relative_paths(string path)
    {
        foreach (var field in new[] { "path", "unitPath" })
        {
            var value = Example("bundle-full");
            Set(value, "content.artifacts.0." + field, JsonValue.Create(path));
            Assert.False(IsValid(value));
        }
    }

    [Fact]
    public void Closed_profiles_reject_unnegotiated_fields()
    {
        var enrollment = Example("enrollment");
        enrollment["autoMerge"] = true;
        Assert.False(IsValid(enrollment));
        var bundle = Example("bundle-full");
        bundle["content"]!["deploymentProven"] = true;
        Assert.False(IsValid(bundle));
        bundle = Example("bundle-full");
        bundle["content"]!["artifacts"]![0]!["inferredBusinessMeaning"] = "money";
        Assert.False(IsValid(bundle));
    }

    [Fact]
    public void Collections_are_bounded_and_exact_duplicate_artifact_descriptors_fail()
    {
        var value = Example("bundle-full");
        var artifacts = value["content"]!["artifacts"]!.AsArray();
        artifacts.Add(artifacts[0]!.DeepClone());
        Assert.False(IsValid(value));
        var enrollment = Example("enrollment");
        enrollment["producerRoleIds"] = new JsonArray(Enumerable.Range(0, 33)
            .Select(i => (JsonNode?)JsonValue.Create("role:" + i.ToString(CultureInfo.InvariantCulture))).ToArray());
        Assert.False(IsValid(enrollment));
    }

    [Fact]
    public void Publication_metadata_is_required_only_for_published_receipts()
    {
        var published = Example("receipt-published");
        var publication = published["publication"]!.DeepClone();
        published.Remove("publication");
        Assert.False(IsValid(published));
        foreach (var status in new[] { "received", "validated", "rejected", "quarantined" })
        {
            var value = Example("receipt-" + status);
            value["publication"] = publication.DeepClone();
            Assert.False(IsValid(value));
        }
    }

    [Theory]
    [InlineData("not-a-time")]
    [InlineData("2026-02-30T08:00:00Z")]
    [InlineData("2026-11-02")]
    public void Timestamp_formats_are_asserted_not_merely_annotated(string timestamp)
    {
        var receipt = Example("receipt-received");
        receipt["recordedAt"] = timestamp;
        Assert.True(IsValid(receipt, formats: false));
        Assert.False(IsValid(receipt));
        var bundle = Example("bundle-full");
        bundle["publicationAttempt"]!["observedAt"] = timestamp;
        Assert.False(IsValid(bundle));
    }

    [Fact]
    public void Attempt_metadata_is_outside_the_content_identity_boundary()
    {
        var value = Example("bundle-full");
        var originalContent = value["content"]!.ToJsonString();
        var originalDigest = value["contentSha256"]!.GetValue<string>();
        value["publicationAttempt"]!["attemptId"] = "attempt:retry";
        value["publicationAttempt"]!["observedAt"] = "2026-11-02T09:00:00Z";
        Assert.True(IsValid(value));
        Assert.Equal(originalContent, value["content"]!.ToJsonString());
        Assert.Equal(originalDigest, value["contentSha256"]!.GetValue<string>());
    }

    [Fact]
    public void Partial_artifact_coverage_can_have_no_entirely_omitted_unit()
    {
        var value = Example("bundle-partial");
        value["content"]!["coverage"]!["omittedUnitPaths"] = new JsonArray();
        Assert.True(IsValid(value));
    }

    [Fact]
    public void Shape_success_does_not_verify_digest_bytes_or_enrollment_authorization()
    {
        var value = Example("bundle-full");
        value["contentSha256"] = new string('0', 64);
        value["content"]!["enrollmentId"] = "enr:unverified-registry-entry";
        Assert.True(IsValid(value));
        Assert.False(ArtifactValidator.Validate(value.ToJsonString()).IsValid);
    }
}
