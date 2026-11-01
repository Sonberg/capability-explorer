using CapabilityExplorer.Explorer;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class ExplorerQueryCursorTests
{
    private static ExplorerQueryContext Context() => new(Guid.NewGuid(), "sample", 42,
        new Dictionary<string, string> { ["repo"] = new('a', 40) });

    [Fact]
    public void ContinuationRetainsExactPosition()
    {
        var codec = new ExplorerQueryCursor(new byte[32]); var context = Context();
        var token = codec.Encode(context, "search:needle", "4", "needle", "key");
        Assert.Equal(["4", "needle", "key"], codec.Decode(context, "search:needle", token)!);
    }
    [Fact]
    public void TamperAndDifferentKeyReject()
    {
        var codec = new ExplorerQueryCursor(new byte[32]); var context = Context();
        var token = codec.Encode(context, "filter", "position");
        Assert.Throws<ArgumentException>(() => codec.Decode(context, "filter", token[..^2] + "AA"));
        Assert.Throws<ArgumentException>(() => new ExplorerQueryCursor(Enumerable.Repeat((byte)1, 32).ToArray()).Decode(context, "filter", token));
    }
    [Fact]
    public void FiltersSnapshotCapabilitySelectionAndGenerationAreBound()
    {
        var codec = new ExplorerQueryCursor(new byte[32]); var context = Context();
        var token = codec.Encode(context, "search:needle", "position");
        Assert.Throws<ArgumentException>(() => codec.Decode(context, "search:other", token));
        Assert.Throws<ArgumentException>(() => codec.Decode(context with { SnapshotId = Guid.NewGuid() }, "search:needle", token));
        Assert.Throws<ArgumentException>(() => codec.Decode(context with { Capability = "other" }, "search:needle", token));
        Assert.Throws<ArgumentException>(() => codec.Decode(context with { Generation = 43 }, "search:needle", token));
        Assert.Throws<ArgumentException>(() => codec.Decode(context with { Selection = new Dictionary<string, string> { ["repo"] = new('b', 40) } }, "search:needle", token));
    }
    [Fact]
    public void SelectionDictionaryInsertionOrderDoesNotInvalidateCursor()
    {
        var codec = new ExplorerQueryCursor(new byte[32]); var context = Context() with { Selection = new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" } };
        var token = codec.Encode(context, "filter", "position");
        Assert.Equal(["position"], codec.Decode(context with { Selection = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" } }, "filter", token)!);
    }
    [Theory]
    [InlineData(0, 1)] [InlineData(-99, 1)] [InlineData(99999, 100)] [InlineData(50, 50)]
    public void PageSizeHasServerBound(int supplied, int expected) => Assert.Equal(expected, new ExplorerPageRequest(supplied).BoundedLimit);
    [Fact]
    public void RejectsHugeAndMalformedTokensAndWeakKeys()
    {
        var codec = new ExplorerQueryCursor(new byte[32]);
        Assert.Throws<ArgumentException>(() => codec.Decode(Context(), "filter", new string('a', 16385)));
        Assert.Throws<ArgumentException>(() => codec.Decode(Context(), "filter", "bad"));
        Assert.Throws<ArgumentException>(() => new ExplorerQueryCursor(new byte[31]));
    }

    [Fact]
    public void ExpiredContinuationRequiresRestartWithoutChangingSnapshot()
    {
        var time = new Clock();
        var codec = new ExplorerQueryCursor(new byte[32], time);
        var context = Context();
        var token = codec.Encode(context, "search", "last-key");
        time.Now = time.Now.AddMinutes(59);
        Assert.Equal(["last-key"], codec.Decode(context, "search", token)!);
        time.Now = time.Now.AddMinutes(1);
        Assert.Throws<ArgumentException>(() => codec.Decode(context, "search", token));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-11-01T00:00:00Z", global::System.Globalization.CultureInfo.InvariantCulture);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
