using CapabilityExplorer.Explorer;
using CapabilityExplorer.Interpretation;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class ModelSecurityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ce-model-security-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public void Invalid_configured_embedding_is_disabled_without_blocking_startup_or_exposing_credentials()
    {
        var model = new Intelligence(new InterpretationOptions
        {
            Provider = "openai", Endpoint = "https://user:private-canary@model.invalid/v1", EmbeddingModel = "fake",
        }, Path.Combine(root, "settings.json"), null);
        Assert.False(model.Layer.Enabled);
        Assert.Null(model.Embeddings);
        Assert.NotNull(model.Problem);
        Assert.DoesNotContain("private-canary", model.Problem, StringComparison.Ordinal);
        Assert.Throws<EmbeddingConfigurationException>(() => model.ResolveEmbeddingConfiguration());
    }

    [Fact]
    public void Changing_endpoint_does_not_reuse_another_services_key()
    {
        var model = new Intelligence(new(), Path.Combine(root, "settings.json"), null);
        Assert.Null(model.Save(new("https://original.test/v1", "model", null, "synthetic-key")));
        Assert.NotNull(model.Save(new("https://other.test/v1", "model", null, null)));
        Assert.Equal("https://original.test/v1", model.Saved!.Endpoint);
        Assert.Null(model.Save(new("https://other.test/v1", "model", null, null, ClearApiKey: true)));
        Assert.Null(model.Saved!.ApiKey);
    }
    [Theory]
    [InlineData("https://user:password@model.test/v1")]
    [InlineData("https://model.test/v1#fragment")]
    public void Credentials_and_fragments_are_not_allowed_in_endpoint(string endpoint)
    {
        var model = new Intelligence(new(), Path.Combine(root, "settings.json"), null);
        Assert.NotNull(model.Save(new(endpoint, "model", null, null)));
        Assert.False(File.Exists(model.SettingsPath));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
