using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CapabilityExplorer.Contracts.Collection;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class CollectionBoundaryTests
{
    [Fact]
    public async Task Duplicate_descriptor_members_are_refused_instead_of_choosing_an_interpretation()
    {
        var fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../../extractors/ts/test/collection/fixtures/valid"));
        var root = Path.Combine(AppContext.BaseDirectory, "collection-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var file in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(root, Path.GetRelativePath(fixture, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            var path = Path.Combine(root, "collection.json");
            var metadata = await File.ReadAllTextAsync(path);
            metadata = metadata.Insert(metadata.IndexOf("\"schema\":", StringComparison.Ordinal), "\"schema\":\"ignored\",");
            await File.WriteAllTextAsync(path, metadata);

            var result = await CollectionExporter.ValidateAsync(root);

            Assert.False(result.IsValid);
            Assert.NotEmpty(result.Problems);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("missing-disposition")]
    [InlineData("null-enrollment")]
    [InlineData("null-permissions")]
    public async Task Malformed_plan_is_refused_without_a_completion_marker(string mutation)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "collection-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "../../../../../../extractors/ts/test/collection/fixtures/valid"));
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(fixture, "collection.json")))!;
            var content = manifest["content"]!;
            var bytes = await File.ReadAllBytesAsync(Path.Combine(fixture, "artifacts/repo-a/ce.models.v1/0000.json"));
            await File.WriteAllBytesAsync(Path.Combine(root, "input.json"), bytes);
            var plan = new JsonObject
            {
                ["schema"] = "ce.collection.plan.v1",
                ["recipeSha256"] = new string('a', 64), ["configSha256"] = new string('b', 64),
                ["policySha256"] = new string('c', 64),
                ["repositories"] = new JsonArray(content["repositories"]![0]!.DeepClone()),
                ["inputs"] = new JsonArray(new JsonObject
                {
                    ["repositoryId"] = "repo-a", ["profile"] = "ce.models.v1", ["path"] = "input.json",
                    ["unitPath"] = "models/repo-a.json", ["required"] = true,
                    ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)), ["byteLength"] = bytes.Length
                })
            };
            if (mutation == "missing-disposition") plan["inputs"]![0]!.AsObject().Remove("required");
            else if (mutation == "null-enrollment") plan["repositories"]![0] = null;
            else plan["repositories"]![0]!["permittedContentProfiles"] = null;
            var path = Path.Combine(root, "plan.json");
            await File.WriteAllTextAsync(path, plan.ToJsonString());

            var result = await CollectionExporter.ExportAsync(path, Path.Combine(root, "bundle"));

            Assert.False(result.IsValid);
            Assert.NotEmpty(result.Problems);
            Assert.Contains(result.Problems, problem => problem.Contains("schema", StringComparison.Ordinal));
            Assert.False(Directory.Exists(Path.Combine(root, "bundle")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
