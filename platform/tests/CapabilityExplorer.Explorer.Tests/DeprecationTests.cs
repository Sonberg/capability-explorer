using CapabilityExplorer.Explorer;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class DeprecationTests
{
    [Fact]
    public void Upsert_replaces_and_delete_removes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"deprecations-{Guid.NewGuid():N}.json");
        try
        {
            var store = new DeprecationStore(path);
            Assert.Empty(store.List());
            store.Upsert("group|http|Klarna", "deprecated", "moving to Adyen");
            store.Upsert("group|http|Klarna", "obsolete", "gone");
            var mark = Assert.Single(store.List());
            Assert.Equal(("obsolete", "gone"), (mark.Status, mark.Note));
            store.Delete("group|http|Klarna");
            Assert.Empty(store.List());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
