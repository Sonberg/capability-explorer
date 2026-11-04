using System.Text.Json;
using CapabilityExplorer.Contracts.Collection;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class CollectionReadBudgetTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(true, 16)]
    [InlineData(false, 16)]
    [InlineData(true, 8)]
    [InlineData(false, 8)]
    public async Task Aggregate_budget_skips_unreadable_later_inputs_with_explicit_disposition(
        bool required, int tailMiB)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "collection-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const int mib = 1_048_576;
            using (var file = File.Create(Path.Combine(root, "large.bin"))) file.SetLength(16 * mib);
            using (var file = File.Create(Path.Combine(root, "tail.bin"))) file.SetLength(tailMiB * mib);
            var lastPath = Path.Combine(root, "last.bin");
            await File.WriteAllBytesAsync(lastPath, [1]);
            // Opening this input would fail, even though its declared metadata is valid.
            await using var locked = new FileStream(lastPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var hash = new string('a', 64);
            var inputs = Enumerable.Range(0, 7).Select(index => new CollectionInputPlan(
                "repo-a", "ce.v1", "large.bin", $"{index:D2}.jsonl", hash, 16 * mib, false, "native-content"))
                .Append(new("repo-a", "ce.v1", "tail.bin", "07.jsonl", hash, tailMiB * mib, false, "native-content"))
                .Append(new("repo-a", "ce.v1", "last.bin", "08.jsonl", hash, 16 * mib, required, "native-content"))
                .ToArray();
            var plan = new CollectionPlan("ce.collection.plan.v1", hash, hash, hash,
                [new("repo-a", "local:synthetic", "test", "synthetic", "explicit",
                    new string('a', 40), new string('b', 40), ["native-content"])], inputs);
            var planPath = Path.Combine(root, "plan.json");
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan, JsonOptions));
            var output = Path.Combine(root, "bundle");

            var result = await CollectionExporter.ExportAsync(planPath, output);

            if (required)
            {
                Assert.False(result.IsValid);
                Assert.Contains(result.Problems, problem => problem.Contains("aggregate read budget", StringComparison.Ordinal));
                Assert.False(Directory.Exists(output));
            }
            else
            {
                Assert.True(result.IsValid, string.Join("; ", result.Problems));
                using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "collection.json")));
                var descriptors = manifest.RootElement.GetProperty("content").GetProperty("inputs");
                Assert.Equal(9, descriptors.GetArrayLength());
                Assert.All(descriptors.EnumerateArray(), input =>
                    Assert.Equal("optional-failed", input.GetProperty("status").GetString()));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
