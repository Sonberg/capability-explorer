using CapabilityExplorer.Capabilities;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class CollectionFreshnessTests
{
    private static string Hash(char value) => new(value, 64);
    private static CollectionRepositoryInput Repo(string id) =>
        new(id, new string('a', 40), new string('b', 40), Hash('c'), Hash('d'));
    private static CollectionInputVector Vector() =>
        new([Repo("sales"), Repo("policy")], Hash('a'), Hash('b'), Hash('c'), Hash('d'), Hash('e'), true);

    [Fact]
    public void Given_complete_unchanged_inputs_when_order_changes_then_identity_is_current()
    {
        var previous = Vector();
        var observed = previous with { Repositories = previous.Repositories.Reverse().ToArray() };
        var result = CollectionFreshness.Evaluate(previous, observed);
        Assert.Equal("current", result.Status);
        Assert.Empty(result.Reasons);
        Assert.Matches("^[a-f0-9]{64}$", result.InputVectorSha256!);
        Assert.Equal(CollectionFreshness.Evaluate(previous, previous).InputVectorSha256, result.InputVectorSha256);
    }

    [Fact]
    public void Given_any_changed_dependency_or_authority_when_compared_then_not_current()
    {
        var previous = Vector();
        CollectionInputVector[] changed =
        [
            previous with { RecipeSha256 = Hash('f') },
            previous with { ConfigurationSha256 = Hash('f') },
            previous with { EnrollmentSha256 = Hash('f') },
            previous with { RightsSha256 = Hash('f') },
            previous with { ProfilesSha256 = Hash('f') },
            previous with { Repositories = [Repo("sales") with { CommitSha = new string('f', 40) }, Repo("policy")] },
            previous with { Repositories = [Repo("sales") with { TreeSha = new string('f', 40) }, Repo("policy")] },
            previous with { Repositories = [Repo("sales") with { InputsSha256 = Hash('f') }, Repo("policy")] },
            previous with { Repositories = [Repo("sales") with { DependenciesSha256 = Hash('f') }, Repo("policy")] },
            previous with { Repositories = [Repo("sales")] }
        ];
        foreach (var observed in changed)
        {
            var result = CollectionFreshness.Evaluate(previous, observed);
            Assert.Equal("stale", result.Status);
            Assert.NotEmpty(result.Reasons);
        }
    }

    [Fact]
    public void Given_incomplete_or_invalid_vectors_when_compared_then_unknown_with_no_reuse_digest()
    {
        var previous = Vector();
        CollectionInputVector[] invalid =
        [
            previous with { EnumerationComplete = false },
            previous with { RecipeSha256 = "" },
            previous with { Repositories = [] },
            previous with { Repositories = [Repo("sales"), Repo("sales")] },
            previous with { Repositories = [Repo("sales") with { InputsSha256 = "not-a-hash" }] }
        ];
        foreach (var observed in invalid)
        {
            var result = CollectionFreshness.Evaluate(previous, observed);
            Assert.Equal("unknown", result.Status);
            Assert.NotEmpty(result.Reasons);
            Assert.Null(result.InputVectorSha256);
            Assert.Equal("unknown", CollectionFreshness.Evaluate(observed, previous).Status);
        }
    }
}
