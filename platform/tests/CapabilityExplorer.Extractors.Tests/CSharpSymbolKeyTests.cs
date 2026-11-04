using System.Text.Json;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Extractors.CSharp;
using Xunit;

namespace CapabilityExplorer.Extractors.Tests;

public sealed class CSharpSymbolKeyTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void Long_symbol_identities_are_bounded_consistent_and_valid_with_or_without_packages()
    {
        var sourceRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "CSharpLongKeys");
        var sourcePath = Path.Combine(sourceRoot, "LongKeys.cs");
        var projectPath = Path.Combine(sourceRoot, "Synthetic.csproj");
        const string project = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """;
        var longNamespace = string.Join('.', Enumerable.Repeat("N", 1_100));
        File.WriteAllText(sourcePath, $$"""
            namespace {{longNamespace}}
            {
                public class Item { }

                public class Target
                {
                    public void Choose(int value) { }
                    public void Choose(bool value) { }
                }

                public class Consumer
                {
                    public Consumer(Item item) { }

                    public void Invoke(Target target)
                    {
                        target.Choose(1);
                        target.Choose(true);
                    }
                }
            }

            namespace Plain
            {
                public class Normal
                {
                    public void Keep() { }
                }
            }
            """);

        try
        {
            var packs = Path.GetFullPath(Path.Combine(
                System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "packs"));
            var withoutPackages = Extract(sourceRoot, packs, packagesRoot: null);
            var withPackages = Extract(sourceRoot, packs, packagesRoot: sourceRoot);

            var artifact = ArtifactValidator.Validate(withoutPackages);
            Assert.True(artifact.IsValid, string.Join(Environment.NewLine, artifact.Problems));
            var packageArtifact = ArtifactValidator.Validate(withPackages);
            Assert.True(packageArtifact.IsValid, string.Join(Environment.NewLine, packageArtifact.Problems));

            var records = ReadRecords(withoutPackages);
            var nodes = records.Where(r => r.GetProperty("kind").GetString() == "node").ToArray();
            var edges = records.Where(r => r.GetProperty("kind").GetString() == "edge").ToArray();
            var keys = nodes.Select(n => n.GetProperty("symbolKey").GetString()!)
                .Concat(edges.SelectMany(e => new[] { e.GetProperty("from").GetString(), e.TryGetProperty("to", out var to) ? to.GetString() : null })
                    .OfType<string>())
                .ToArray();

            Assert.All(keys, key => Assert.True(key.Length <= 2048, $"symbol key is {key.Length} UTF-16 code units: {key}"));
            var hashedKeys = keys.Where(key => key.Contains("|@sha256:", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
            Assert.NotEmpty(hashedKeys);
            Assert.All(hashedKeys, key => Assert.Matches(@"^cs\|[^|]+\|[^|]+\|@sha256:[0-9a-f]{64}$", key));

            var item = Assert.Single(nodes, node => node.GetProperty("name").GetString() == "Item");
            Assert.Contains("|@sha256:", item.GetProperty("symbolKey").GetString(), StringComparison.Ordinal);
            var targetKey = Assert.Single(nodes, node => node.GetProperty("name").GetString() == "Target")
                .GetProperty("symbolKey").GetString();
            var targetMethods = nodes.Where(node => node.GetProperty("name").GetString() == "Choose")
                .Select(node => node.GetProperty("symbolKey").GetString()!).ToArray();
            Assert.Equal(2, targetMethods.Distinct(StringComparer.Ordinal).Count());
            Assert.All(targetMethods, methodKey => Assert.Contains("|@sha256:", methodKey, StringComparison.Ordinal));
            Assert.All(targetMethods, methodKey => Assert.Contains(edges, edge =>
                edge.GetProperty("from").GetString() == targetKey && edge.GetProperty("relation").GetString() == "DECLARES" &&
                edge.GetProperty("to").GetString() == methodKey));
            var consumerConstructor = Assert.Single(nodes, node =>
                node.GetProperty("name").GetString() == "Consumer" &&
                node.TryGetProperty("props", out var props) && props.TryGetProperty("constructor", out var ctor) && ctor.GetBoolean());
            var constructorKey = consumerConstructor.GetProperty("symbolKey").GetString();
            var itemKey = item.GetProperty("symbolKey").GetString();
            Assert.Contains(edges, edge => edge.GetProperty("from").GetString() == constructorKey &&
                edge.GetProperty("relation").GetString() == "REFERENCES" && edge.GetProperty("to").GetString() == itemKey);

            var callerKey = Assert.Single(nodes, node => node.GetProperty("name").GetString() == "Invoke")
                .GetProperty("symbolKey").GetString();
            var calls = edges.Where(edge => edge.GetProperty("relation").GetString() == "CALLS" &&
                edge.GetProperty("from").GetString() == callerKey).ToArray();
            Assert.Equal(targetMethods.Order(StringComparer.Ordinal), calls.Select(edge => edge.GetProperty("to").GetString()!).Order(StringComparer.Ordinal));
            Assert.Contains(nodes, node => node.GetProperty("symbolKey").GetString() == "cs|synthetic|Synthetic|M:Plain.Normal.Keep");

            Assert.Equal(withoutPackages, Extract(sourceRoot, packs, packagesRoot: null));
            var packageRecords = ReadRecords(withPackages);
            var packageKeys = packageRecords.Where(r => r.GetProperty("kind").GetString() == "node")
                .Select(r => r.GetProperty("symbolKey").GetString()!)
                .Concat(packageRecords.Where(r => r.GetProperty("kind").GetString() == "edge")
                    .SelectMany(e => new[] { e.GetProperty("from").GetString(), e.TryGetProperty("to", out var to) ? to.GetString() : null }).OfType<string>());
            Assert.Equal(keys.Order(StringComparer.Ordinal), packageKeys.Order(StringComparer.Ordinal));

            var longAssembly = new string('A', 2_200);
            File.WriteAllText(projectPath, $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <AssemblyName>{{longAssembly}}</AssemblyName>
                  </PropertyGroup>
                </Project>
                """);
            var longScopeJsonl = Assert.Single(new CSharpExtractor(new ExtractorOptions(
                sourceRoot, new string('r', 100), Sha, PackagesRoot: null, ReferencePacks: packs)).Extract()).Jsonl;
            var longScopeValidation = ArtifactValidator.Validate(longScopeJsonl);
            Assert.True(longScopeValidation.IsValid, string.Join(Environment.NewLine, longScopeValidation.Problems));
            var longScopeRecords = ReadRecords(longScopeJsonl);
            var longScopeNodes = longScopeRecords.Where(r => r.GetProperty("kind").GetString() == "node").ToArray();
            var longScopeKeys = longScopeNodes.Select(r => r.GetProperty("symbolKey").GetString()!)
                .Concat(longScopeRecords.Where(r => r.GetProperty("kind").GetString() == "edge")
                    .SelectMany(e => new[] { e.GetProperty("from").GetString(), e.TryGetProperty("to", out var to) ? to.GetString() : null })
                    .OfType<string>())
                .Where(key => key.StartsWith("cs|", StringComparison.Ordinal))
                .ToArray();
            Assert.All(longScopeKeys, key => Assert.True(key.Length <= 2048));
            Assert.All(longScopeKeys, key =>
                Assert.Matches(@"^cs\|r{100}\|@sha256:[0-9a-f]{64}\|@sha256:[0-9a-f]{64}$", key));
            var longScopeOverloads = longScopeNodes.Where(node => node.GetProperty("name").GetString() == "Choose")
                .Select(node => node.GetProperty("symbolKey").GetString()!).ToArray();
            Assert.Equal(2, longScopeOverloads.Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            File.Delete(sourcePath);
            File.WriteAllText(projectPath, project);
        }
    }

    private static string Extract(string sourceRoot, string packs, string? packagesRoot) =>
        Assert.Single(new CSharpExtractor(new ExtractorOptions(
            sourceRoot, "synthetic", Sha, packagesRoot, packs)).Extract()).Jsonl;

    private static JsonElement[] ReadRecords(string jsonl) => jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        })
        .ToArray();
}
