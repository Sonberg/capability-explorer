using CapabilityExplorer.Explorer.GeneratedModels;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class GeneratedModelStoreTests
{
    [Fact]
    public void Given_a_successful_snapshot_when_a_later_run_fails_then_the_snapshot_is_preserved()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "ce-generated-model-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new GeneratedModelStore(root);
            var snapshot = new GeneratedModelSnapshot(
                GeneratedModelEngine.Schema, "complete", "capability", Guid.NewGuid().ToString(), 1,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["repo"] = new('a', 40) },
                new('b', 64), [], [], [], [], "not-used", "published artifacts only", false, DateTimeOffset.UtcNow, "");
            snapshot = snapshot with { ContentHash = GeneratedModelEngine.ContentHash(snapshot) };
            store.Write(new GeneratedModelStoreDocument(snapshot, null));

            var failedRun = new GeneratedModelRunState(
                Guid.NewGuid().ToString(), "failed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                "analysis-unavailable", "provider", "scope", snapshot.SnapshotId, snapshot.Generation,
                snapshot.Selection);
            store.Write(new GeneratedModelStoreDocument(snapshot, failedRun));

            var persisted = store.Read();
            Assert.Equal(snapshot.ContentHash, persisted.LatestSuccessful?.ContentHash);
            Assert.Equal("failed", persisted.LatestRun?.Status);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
