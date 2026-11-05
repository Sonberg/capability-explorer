using System.Text.Json;
using CapabilityExplorer.Interpretation;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class EmbeddingHostConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "embedding-host-" + Guid.NewGuid().ToString("N"));

    public EmbeddingHostConfigurationTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Appsettings_environment_and_explicit_configuration_match_API_configuration_values(bool customPath)
    {
        var settings = Path.Combine(_root, "custom-model.json");
        new ModelSettings("https://custom-model.invalid/v1", null, "saved", "synthetic-key").Save(settings);
        var values = customPath ? new Dictionary<string, string> { ["modelSettings"] = settings }
            : new Dictionary<string, string>
            {
                ["interpretation:provider"] = "openai", ["interpretation:endpoint"] = "https://configured.invalid/v1",
                ["interpretation:embeddingModel"] = "configured", ["interpretation:embeddingRevision"] = "immutable-revision",
                ["interpretation:useDefaultAzureCredential"] = "false",
            };
        File.WriteAllText(Path.Combine(_root, "appsettings.json"), JsonSerializer.Serialize(new
        {
            modelSettings = customPath ? settings : null,
            interpretation = customPath ? null : new
            {
                provider = "openai", endpoint = "https://configured.invalid/v1", embeddingModel = "configured",
                embeddingRevision = "immutable-revision", useDefaultAzureCredential = false,
            },
        }));
        var cli = EmbeddingHostConfiguration.Resolve(environment: _ => null, contentRoot: _root)!;
        var api = EmbeddingHostConfiguration.Resolve(configuration: key => values.GetValueOrDefault(key),
            environment: _ => null, contentRoot: _root)!;
        Assert.Equal(api.Identity, cli.Identity);
        Assert.Equal(api.Options, cli.Options);
        Assert.Equal(api.Identity, EmbeddingHostConfiguration.Resolve(values, environment: _ => null, contentRoot: _root)!.Identity);
    }

    [Fact]
    public void Environment_and_shared_configuration_paths_have_the_same_precedence_in_every_host()
    {
        var configured = new Dictionary<string, string>
        {
            ["interpretation:provider"] = "openai", ["interpretation:endpoint"] = "https://environment.invalid/v1",
            ["interpretation:embeddingModel"] = "environment", ["interpretation:embeddingRevision"] = "environment-revision",
        };
        var environment = configured.ToDictionary(p => p.Key.Replace(":", "__", StringComparison.Ordinal), p => p.Value);
        var api = EmbeddingHostConfiguration.Resolve(configuration: key => configured.GetValueOrDefault(key), environment: environment.GetValueOrDefault, contentRoot: _root)!;
        var cli = EmbeddingHostConfiguration.Resolve(environment: environment.GetValueOrDefault, contentRoot: _root)!;
        Assert.Equal(api.Identity, cli.Identity);
        var shared = Path.Combine(_root, "shared.json");
        File.WriteAllText(shared, """{"interpretation":{"provider":"foundry","endpoint":"https://shared.azure.com/v1","embeddingModel":"shared","embeddingRevision":"shared-revision","useDefaultAzureCredential":false}}""");
        environment["CE_MODEL_CONFIG"] = shared;
        api = EmbeddingHostConfiguration.Resolve(configuration: key => configured.GetValueOrDefault(key), environment: environment.GetValueOrDefault, contentRoot: _root)!;
        cli = EmbeddingHostConfiguration.Resolve(environment: environment.GetValueOrDefault, contentRoot: _root)!;
        Assert.Equal(api.Identity, cli.Identity);
        Assert.Equal("foundry:shared", cli.Identity.Id);
        environment["CE_MODEL_ENDPOINT"] = "http://localhost:11434/v1";
        environment["CE_EMBEDDING_MODEL"] = "final";
        Assert.Equal("openai:final", EmbeddingHostConfiguration.Resolve(environment: environment.GetValueOrDefault, contentRoot: _root)!.Identity.Id);
    }

    [Fact]
    public void Custom_settings_environment_is_shared_and_existing_settings_option_remains_an_explicit_override()
    {
        var shared = Path.Combine(_root, "shared-model.json");
        var explicitPath = Path.Combine(_root, "explicit-model.json");
        new ModelSettings("https://shared.invalid/v1", null, "shared", null).Save(shared);
        new ModelSettings("https://explicit.invalid/v1", null, "explicit", null).Save(explicitPath);
        var environment = new Dictionary<string, string> { ["CE_MODEL_SETTINGS"] = shared };
        Assert.Equal("openai:shared", EmbeddingHostConfiguration.Resolve(environment: environment.GetValueOrDefault, contentRoot: _root)!.Identity.Id);
        Assert.Equal("openai:explicit", EmbeddingHostConfiguration.Resolve(new Dictionary<string, string> { ["settings"] = explicitPath },
            environment: environment.GetValueOrDefault, contentRoot: _root)!.Identity.Id);
    }

    [Fact]
    public void Explicit_API_and_CLI_options_override_shared_configuration_identically()
    {
        var shared = Path.Combine(_root, "shared.json");
        File.WriteAllText(shared, """{"interpretation":{"provider":"openai","endpoint":"https://shared.invalid/v1","embeddingModel":"shared"}}""");
        var args = new[] { "--source", "postgres", "--model-config", shared, "--interpretation:embeddingModel=explicit" };
        var options = EmbeddingHostConfiguration.CommandOptions(args);
        var cli = EmbeddingHostConfiguration.Resolve(options, environment: _ => null, contentRoot: _root)!;
        var api = EmbeddingHostConfiguration.Resolve(options, configuration: key => options.GetValueOrDefault(key),
            environment: _ => null, contentRoot: _root)!;
        Assert.Equal("openai:explicit", cli.Identity.Id);
        Assert.Equal(api.Identity, cli.Identity);
    }

    [Fact]
    public void Appsettings_shared_file_and_relative_saved_settings_resolve_identically_in_each_host()
    {
        var shared = Path.Combine(_root, "shared.json");
        new ModelSettings("https://relative.invalid/v1", null, "relative", null).Save(Path.Combine(_root, "saved.json"));
        File.WriteAllText(shared, """{"modelSettings":"saved.json"}""");
        File.WriteAllText(Path.Combine(_root, "appsettings.json"), JsonSerializer.Serialize(new Dictionary<string, string> { ["model-config"] = shared }));
        var cli = EmbeddingHostConfiguration.Resolve(environment: _ => null, contentRoot: _root)!;
        var api = EmbeddingHostConfiguration.Resolve(configuration: key => key == "model-config" ? shared : null,
            environment: _ => null, contentRoot: _root)!;
        Assert.Equal("openai:relative", cli.Identity.Id);
        Assert.Equal(api.Identity, cli.Identity);
    }

    [Fact]
    public void Conflicting_settings_aliases_are_rejected_without_disclosing_their_values()
    {
        var error = Assert.Throws<EmbeddingConfigurationException>(() => EmbeddingHostConfiguration.Resolve(
            new Dictionary<string, string> { ["settings"] = "first-canary", ["modelSettings"] = "second-canary" },
            environment: _ => null, contentRoot: _root));
        Assert.DoesNotContain("canary", error.Message, StringComparison.Ordinal);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
