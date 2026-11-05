using CapabilityExplorer.Interpretation;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class EmbeddingConfigurationTests
{
    private static readonly InterpretationOptions Options = new()
    {
        Provider = "openai", Endpoint = "https://MODEL.invalid:443/v1/", EmbeddingModel = "embedding", ApiKey = "secret",
    };

    private static EmbeddingConfiguration? Resolve(InterpretationOptions options, IReadOnlyDictionary<string, string>? env = null) =>
        EmbeddingConfiguration.Resolve(options, "nonexistent-model-settings.json", key => env?.GetValueOrDefault(key));

    [Fact]
    public void Identity_is_canonical_nonsecret_and_changes_with_model_endpoint_revision_and_recipe()
    {
        var first = Resolve(Options)!.Identity;
        Assert.Equal(first, Resolve(Options with { Endpoint = "https://model.invalid/v1", ApiKey = "different-secret", ApiKeyVariable = "different-variable" })!.Identity);
        Assert.NotEqual(first.Fingerprint, Resolve(Options with { Endpoint = "https://other.invalid/v1" })!.Identity.Fingerprint);
        Assert.NotEqual(first.Fingerprint, Resolve(Options with { EmbeddingModel = "another" })!.Identity.Fingerprint);
        Assert.NotEqual(first.Fingerprint, Resolve(Options with { EmbeddingRevision = "revision-2" })!.Identity.Fingerprint);
        Assert.NotEqual(first.Fingerprint, Resolve(Options, new Dictionary<string, string> { ["CE_EMBEDDING_REVISION"] = "revision-3" })!.Identity.Fingerprint);
        Assert.DoesNotContain("secret", first.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("model.invalid", first.ToString(), StringComparison.Ordinal);
        Assert.Equal(64, first.Fingerprint.Length);
    }

    [Fact]
    public void Environment_has_same_precedence_in_API_and_CLI_and_missing_is_distinct_from_invalid()
    {
        Assert.Null(Resolve(new InterpretationOptions()));
        var env = new Dictionary<string, string> { ["CE_MODEL_ENDPOINT"] = "http://localhost:11434/v1", ["CE_EMBEDDING_MODEL"] = "local" };
        Assert.Equal("openai:local", Resolve(Options, env)!.Identity.Id);
        Assert.Throws<EmbeddingConfigurationException>(() => Resolve(Options, new Dictionary<string, string> { ["CE_EMBEDDING_MODEL"] = "missing-endpoint" }));
        Assert.Throws<EmbeddingConfigurationException>(() => Resolve(Options with { Endpoint = "https://user:password@model.invalid", EmbeddingModel = null }));
    }

    [Theory]
    [InlineData("https://user:secret@model.invalid/v1")]
    [InlineData("https://model.invalid/v1?key=secret")]
    [InlineData("https://model.invalid/v1#secret")]
    [InlineData("not-an-endpoint-secret")]
    [InlineData("ftp://model.invalid/secret")]
    public void Invalid_endpoints_are_rejected_without_leaking_secrets(string endpoint)
    {
        var error = Assert.Throws<EmbeddingConfigurationException>(() => Resolve(Options with { Endpoint = endpoint }));
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }
}
