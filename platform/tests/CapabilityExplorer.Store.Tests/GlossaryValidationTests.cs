using CapabilityExplorer.Store;
using Xunit;

namespace CapabilityExplorer.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class GlossaryValidationTests(PostgresFixture db)
{
    private readonly GlossaryStore _store = new(db.DataSource);
    private static GlossaryLinkWrite Link() => new(Guid.NewGuid(), Guid.NewGuid(), "repo", "unit", "symbol", "same", null,
        new("repo", new string('a', 40), "unit", "symbol", new string('b', 64), "occurrence"), "Reviewer");

    [Fact]
    public async Task Rejects_empty_identifiers_overlong_text_and_invalid_versions_before_writing()
    {
        var term = new GlossaryTermWrite(Guid.NewGuid(), "Term", "Definition", null, "Reviewer");
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveTermAsync(term with { Id = Guid.Empty }, 0));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveTermAsync(term with { Name = new string('x', 201) }, 0));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveTermAsync(term with { Definition = " " }, 0));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveTermAsync(term with { Reviewer = "" }, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _store.SaveTermAsync(term, -1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _store.SaveTermAsync(term, long.MaxValue));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _store.DeleteTermAsync(term.Id, 0, "Reviewer"));
    }

    [Theory]
    [InlineData("alias_of")]
    [InlineData("SAME")]
    [InlineData("")]
    public async Task Rejects_invalid_relationship_roles(string role) =>
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveLinkAsync(Link() with { Role = role }, 0));

    [Fact]
    public async Task Rejects_cross_scope_provenance_bad_hashes_and_path_traversal()
    {
        var link = Link();
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveLinkAsync(link with { Observed = link.Observed with { RepositoryId = "another" } }, 0));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveLinkAsync(link with { Observed = link.Observed with { SymbolKey = "another" } }, 0));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveLinkAsync(link with { Observed = link.Observed with { UnitPath = "another" } }, 0));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveLinkAsync(link with { Observed = link.Observed with { CommitSha = "bad" } }, 0));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveLinkAsync(link with { Observed = link.Observed with { ArtifactContentSha256 = "bad" } }, 0));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveLinkAsync(link with { UnitPath = "../outside" }, 0));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveLinkAsync(link with { UnitPath = "/absolute" }, 0));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveLinkAsync(link with { RepositoryId = "bad repo" }, 0));
    }

    [Fact]
    public async Task Rejects_invalid_issue_kind_and_status()
    {
        var issue = new GlossaryIssueWrite(Guid.NewGuid(), Guid.NewGuid(), "other", "Issue", null, "open", "Reviewer");
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveIssueAsync(issue with { Kind = "unknown" }, 0));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveIssueAsync(issue with { Status = "closed" }, 0));
    }
}
