using CapabilityExplorer.Explorer;
using CapabilityExplorer.Store;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class GlossaryReviewStateTests
{
    private static ExplorerIndex Index() => ExplorerIndex.Load([Fixtures.ExpectedDirectory], new GitSource(Path.Combine(Fixtures.RepositoryRoot, "fixtures", ".repos")));
    private static GlossaryLink Link(GlossaryObservedOccurrence observed) => new(Guid.NewGuid(), Guid.NewGuid(), observed.RepositoryId, observed.UnitPath, observed.SymbolKey, "same", null, observed, "supplied reviewer", 1, false);

    [Fact]
    public void Exact_provenance_is_current_but_same_symbol_with_changed_analysis_is_stale()
    {
        var view = Index().View("api-cs:v1-base");
        var occurrence = Glossary.Build(view).SelectMany(c => c.Occurrences).First(o => o.RepositoryId == "api-cs");
        var observed = GlossaryReviewService.Observe(view, occurrence.Id);
        Assert.Equal("current", Assert.Single(GlossaryReviewService.Resolve(view, [Link(observed)])).State);
        Assert.Equal("stale", Assert.Single(GlossaryReviewService.Resolve(view, [Link(observed with { ArtifactContentSha256 = new string('0', 64) })])).State);
        Assert.Equal("stale", Assert.Single(GlossaryReviewService.Resolve(view, [Link(observed with { CommitSha = new string('0', 40) })])).State);
    }

    [Fact]
    public void Scope_exclusion_and_missing_symbol_are_not_silent_rebinding()
    {
        var index = Index(); var view = index.View("api-cs:v1-base");
        var occurrence = Glossary.Build(view).SelectMany(c => c.Occurrences).First(o => o.RepositoryId == "api-cs");
        var link = Link(GlossaryReviewService.Observe(view, occurrence.Id));
        var scoped = index.View(null, new ViewScope("other", new HashSet<string> { "web-ts" }, new Dictionary<string, string>()));
        Assert.Equal("out-of-scope", Assert.Single(GlossaryReviewService.Resolve(scoped, [link])).State);
        Assert.Equal("orphaned", Assert.Single(GlossaryReviewService.Resolve(view, [link with { SymbolKey = "missing" }])).State);
        Assert.Throws<ArgumentException>(() => GlossaryReviewService.Observe(scoped, occurrence.Id));
    }
    [Fact]
    public void Lost_exact_role_is_ambiguous_when_multiple_occurrences_share_the_symbol()
    {
        var view = Index().View("api-cs:v1-base");
        var alternatives = Glossary.Build(view).SelectMany(c => c.Occurrences)
            .GroupBy(o => (o.RepositoryId, o.UnitPath, o.Key)).First(g => g.Count() > 1).ToList();
        var observed = GlossaryReviewService.Observe(view, alternatives[0].Id);
        var link = Link(observed with { OccurrenceId = "glossary:missing-exact-role" });
        var state = Assert.Single(GlossaryReviewService.Resolve(view, [link]));
        Assert.Equal("ambiguous", state.State);
        Assert.Null(state.OccurrenceId);
    }

}
