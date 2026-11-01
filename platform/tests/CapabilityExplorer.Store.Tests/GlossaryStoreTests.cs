using System.Text.Json;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Store;
using Npgsql;
using Xunit;

namespace CapabilityExplorer.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class GlossaryStoreTests(PostgresFixture db)
{
    private readonly GlossaryStore _store = new(db.DataSource);
    private static GlossaryTermWrite Term() => new(Guid.NewGuid(), "Weekly sales", "Reviewed business measure", "Sales", "Reviewer A");
    private static GlossaryLinkWrite Link(Guid termId) => new(Guid.NewGuid(), termId, "api-cs", "Sales.Api", "cs|api-cs|Sales.Api|P:Sales.WeeklySales", "same", "Reviewed assertion",
        new("api-cs", new string('a', 40), "Sales.Api", "cs|api-cs|Sales.Api|P:Sales.WeeklySales", new string('b', 64), "node-17"), "Reviewer A");
    private static GlossaryIssueWrite Issue(Guid termId) => new(Guid.NewGuid(), termId, "type", "Decimal and integer differ", "Needs review", "open", "Reviewer A");

    [Fact]
    public async Task Persists_exact_provenance_and_versions_across_store_instances_and_issue_resolution()
    {
        var term = Term();
        var link = Link(term.Id);
        var issue = Issue(term.Id);
        Assert.Equal(GlossaryMutationStatus.Created, (await _store.SaveTermAsync(term, 0)).Status);
        Assert.Equal(GlossaryMutationStatus.Created, (await _store.SaveLinkAsync(link, 0)).Status);
        Assert.Equal(GlossaryMutationStatus.Created, (await _store.SaveIssueAsync(issue, 0)).Status);
        var restarted = new GlossaryStore(db.DataSource);
        var snapshot = await restarted.GetSnapshotAsync();
        var saved = Assert.Single(snapshot.Links, l => l.Id == link.Id);
        Assert.Equal(link.Observed, saved.Observed);
        Assert.Equal(link.SymbolKey, saved.SymbolKey);
        Assert.Equal(1, saved.Version);
        Assert.Equal("same", saved.Role);

        var resolved = await restarted.SaveIssueAsync(issue with { Status = "resolved", Reviewer = "Reviewer B" }, 1);
        Assert.Equal(GlossaryMutationStatus.Updated, resolved.Status);
        Assert.Equal("resolved", resolved.Value!.Status);
        Assert.Equal(2, resolved.Value.Version);
        var stale = await restarted.SaveIssueAsync(issue, 1);
        Assert.Equal(GlossaryMutationStatus.Conflict, stale.Status);
        Assert.Equal("resolved", stale.Value!.Status);
        var audit = await restarted.GetAuditAsync(issue.Id);
        Assert.Equal(["create", "update"], audit.Events.Select(e => e.Action));
        Assert.Equal(["Reviewer A", "Reviewer B"], audit.Events.Select(e => e.Actor));
        Assert.Contains("\"status\": \"open\"", audit.Events[0].DataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exact_create_and_update_replays_are_idempotent_but_different_content_conflicts()
    {
        var term = Term();
        var created = await _store.SaveTermAsync(term, 0);
        Assert.Equal(1, created.Value!.Version);
        Assert.Equal(GlossaryMutationStatus.Unchanged, (await _store.SaveTermAsync(term, 0)).Status);
        Assert.Equal(GlossaryMutationStatus.Conflict, (await _store.SaveTermAsync(term with { Reviewer = "Someone else" }, 0)).Status);
        var changed = term with { Definition = "Updated definition" };
        Assert.Equal(GlossaryMutationStatus.Updated, (await _store.SaveTermAsync(changed, 1)).Status);
        Assert.Equal(GlossaryMutationStatus.Unchanged, (await _store.SaveTermAsync(changed, 1)).Status);
        Assert.Equal(GlossaryMutationStatus.Conflict, (await _store.SaveTermAsync(term, 1)).Status);
        Assert.Equal(2, (await _store.GetAuditAsync(term.Id)).Events.Count);
        Assert.Equal(GlossaryMutationStatus.NotFound, (await _store.SaveTermAsync(Term(), 1)).Status);
        Assert.Equal(GlossaryMutationStatus.NotFound, (await _store.DeleteTermAsync(Guid.NewGuid(), 1, "Reviewer")).Status);
    }

    [Fact]
    public async Task Concurrent_editors_get_one_success_and_one_explicit_stale_version_conflict()
    {
        var term = Term();
        await _store.SaveTermAsync(term, 0);
        var results = await Task.WhenAll(_store.SaveTermAsync(term with { Name = "First edit" }, 1), _store.SaveTermAsync(term with { Name = "Second edit" }, 1));
        Assert.Single(results, r => r.Status == GlossaryMutationStatus.Updated);
        Assert.Single(results, r => r.Status == GlossaryMutationStatus.Conflict);
        Assert.All(results, r => Assert.Equal(2, r.Value!.Version));
        Assert.Equal(2, (await _store.GetAuditAsync(term.Id)).Events.Count);
    }

    [Fact]
    public async Task Deleting_a_term_cascades_soft_deletions_with_audit_and_prevents_resurrection()
    {
        var term = Term();
        var link = Link(term.Id);
        var issue = Issue(term.Id);
        await _store.SaveTermAsync(term, 0);
        await _store.SaveLinkAsync(link, 0);
        await _store.SaveIssueAsync(issue, 0);
        var deleted = await _store.DeleteTermAsync(term.Id, 1, "Reviewer B");
        Assert.Equal(GlossaryMutationStatus.Deleted, deleted.Status);
        Assert.True(deleted.Value!.Deleted);
        Assert.Equal("Reviewer B", deleted.Value.Reviewer);
        Assert.Equal(GlossaryMutationStatus.Unchanged, (await _store.DeleteTermAsync(term.Id, 1, "Reviewer B")).Status);
        Assert.Equal(GlossaryMutationStatus.Conflict, (await _store.DeleteTermAsync(term.Id, 1, "Reviewer C")).Status);
        Assert.Equal(GlossaryMutationStatus.Conflict, (await _store.SaveTermAsync(term, 0)).Status);
        var snapshot = await _store.GetSnapshotAsync();
        Assert.DoesNotContain(snapshot.Terms, t => t.Id == term.Id);
        Assert.DoesNotContain(snapshot.Links, l => l.Id == link.Id);
        Assert.DoesNotContain(snapshot.Issues, i => i.Id == issue.Id);
        foreach (var id in new[] { link.Id, issue.Id })
        {
            var last = (await _store.GetAuditAsync(id)).Events[^1];
            Assert.Equal("delete_cascade", last.Action);
            Assert.Equal("Reviewer B", last.Actor);
            Assert.Equal(2, last.Version);
        }
        Assert.Equal(GlossaryMutationStatus.NotFound, (await _store.SaveLinkAsync(Link(term.Id), 0)).Status);
        Assert.Equal(GlossaryMutationStatus.NotFound, (await _store.SaveIssueAsync(Issue(term.Id), 0)).Status);
        Assert.True((await _store.GetTermAsync(term.Id))!.Deleted);
        Assert.True((await _store.GetLinkAsync(link.Id))!.Deleted);
        Assert.True((await _store.GetIssueAsync(issue.Id))!.Deleted);
        Assert.Null(await _store.GetLinkAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Links_and_issues_support_update_delete_conflict_and_cannot_change_parent()
    {
        var term = Term();
        var other = Term();
        await _store.SaveTermAsync(term, 0);
        await _store.SaveTermAsync(other, 0);
        var link = Link(term.Id);
        var issue = Issue(term.Id);
        await _store.SaveLinkAsync(link, 0);
        await _store.SaveIssueAsync(issue, 0);
        Assert.Equal(GlossaryMutationStatus.Conflict, (await _store.SaveLinkAsync(link with { TermId = other.Id }, 1)).Status);
        Assert.Equal(GlossaryMutationStatus.Conflict, (await _store.SaveIssueAsync(issue with { TermId = other.Id }, 1)).Status);
        Assert.Equal(GlossaryMutationStatus.Updated, (await _store.SaveLinkAsync(link with { Role = "alias" }, 1)).Status);
        Assert.Equal(GlossaryMutationStatus.Conflict, (await _store.DeleteLinkAsync(link.Id, 1, "Reviewer B")).Status);
        Assert.Equal(GlossaryMutationStatus.Deleted, (await _store.DeleteLinkAsync(link.Id, 2, "Reviewer B")).Status);
        Assert.Equal(GlossaryMutationStatus.Deleted, (await _store.DeleteIssueAsync(issue.Id, 1, "Reviewer B")).Status);
        Assert.Equal(GlossaryMutationStatus.Conflict, (await _store.SaveIssueAsync(issue, 2)).Status);
    }

    [Fact]
    public async Task Audit_failure_rolls_back_term_and_child_deletions_together()
    {
        var term = Term();
        var link = Link(term.Id);
        var issue = Issue(term.Id);
        await _store.SaveTermAsync(term, 0);
        await _store.SaveLinkAsync(link, 0);
        await _store.SaveIssueAsync(issue, 0);
        await db.ExecuteAsync("""
            create function glossary_reject_test() returns trigger language plpgsql as
            $$ begin if new.action = 'delete_cascade' then raise exception 'injected audit failure'; end if; return new; end $$;
            create trigger glossary_reject_test before insert on glossary_audit for each row execute function glossary_reject_test();
            """);
        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => _store.DeleteTermAsync(term.Id, 1, "Reviewer B"));
            var snapshot = await _store.GetSnapshotAsync();
            Assert.Equal(1, Assert.Single(snapshot.Terms, t => t.Id == term.Id).Version);
            Assert.Equal(1, Assert.Single(snapshot.Links, l => l.Id == link.Id).Version);
            Assert.Equal(1, Assert.Single(snapshot.Issues, i => i.Id == issue.Id).Version);
            Assert.Single((await _store.GetAuditAsync(term.Id)).Events);
            Assert.Single((await _store.GetAuditAsync(link.Id)).Events);
        }
        finally
        {
            await db.ExecuteAsync("drop trigger glossary_reject_test on glossary_audit; drop function glossary_reject_test();");
        }
    }

    [Fact]
    public async Task Lists_and_audit_expose_bounds_and_truncation()
    {
        var term = Term();
        await _store.SaveTermAsync(term, 0);
        await _store.SaveTermAsync(Term(), 0);
        await _store.SaveLinkAsync(Link(term.Id), 0);
        await _store.SaveLinkAsync(Link(term.Id), 0);
        await _store.SaveIssueAsync(Issue(term.Id), 0);
        await _store.SaveIssueAsync(Issue(term.Id), 0);
        await _store.SaveTermAsync(term with { Name = "Changed" }, 1);
        var snapshot = await _store.GetSnapshotAsync(1);
        Assert.Single(snapshot.Terms);
        Assert.Single(snapshot.Links);
        Assert.Single(snapshot.Issues);
        Assert.True(snapshot.TermsTruncated && snapshot.LinksTruncated && snapshot.IssuesTruncated);
        Assert.Equal(1, snapshot.Limit);
        Assert.True((await _store.GetAuditAsync(term.Id, 1)).Truncated);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _store.GetSnapshotAsync(0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _store.GetSnapshotAsync(2001));
    }

    [Fact]
    public async Task Review_lifecycle_leaves_all_imported_artifact_bytes_and_rows_unchanged()
    {
        var artifact = ArtifactValidator.Validate(Fixtures.Golden("api-cs/v1-base/Sales.Api.jsonl")).Artifact!;
        var imported = await new ArtifactImporter(db.DataSource).ImportAsync(artifact);
        var before = await ArtifactRowsAsync(imported.ArtifactId);
        var counts = await db.CountAllAsync();
        var term = Term() with { Name = "<script>alert('review')</script>" };
        await _store.SaveTermAsync(term, 0);
        var link = Link(term.Id);
        await _store.SaveLinkAsync(link, 0);
        await _store.SaveLinkAsync(link with { Note = "Later observation differs", Role = "derived_from" }, 1);
        await _store.DeleteTermAsync(term.Id, 1, "Reviewer B");
        Assert.Equal(before, await ArtifactRowsAsync(imported.ArtifactId));
        Assert.Equal(counts, await db.CountAllAsync());
        Assert.Equal(link.Observed.ArtifactContentSha256, JsonDocument.Parse((await _store.GetAuditAsync(link.Id)).Events[0].DataJson).RootElement.GetProperty("observed").GetProperty("artifactContentSha256").GetString());
    }

    [Fact]
    public async Task New_analysis_never_rebinds_or_updates_the_observation_that_was_reviewed()
    {
        var oldArtifact = ArtifactValidator.Validate(Fixtures.Golden("api-cs/v1-base/Sales.Api.jsonl")).Artifact!;
        var newArtifact = ArtifactValidator.Validate(Fixtures.Golden("api-cs/v2-rename/Sales.Api.jsonl")).Artifact!;
        await new ArtifactImporter(db.DataSource).ImportAsync(oldArtifact);
        var commonNode = oldArtifact.Nodes.First(n => newArtifact.Nodes.Any(other => other.SymbolKey == n.SymbolKey));
        var term = Term();
        await _store.SaveTermAsync(term, 0);
        var observed = new GlossaryObservedOccurrence(oldArtifact.Header.RepositoryId, oldArtifact.Header.CommitSha, oldArtifact.Header.Unit.Path, commonNode.SymbolKey, oldArtifact.ContentSha256, commonNode.Id);
        var link = new GlossaryLinkWrite(Guid.NewGuid(), term.Id, observed.RepositoryId, observed.UnitPath, observed.SymbolKey, "same", null, observed, "Reviewer A");
        await _store.SaveLinkAsync(link, 0);
        await new ArtifactImporter(db.DataSource).ImportAsync(newArtifact);
        var retained = await _store.GetLinkAsync(link.Id);
        Assert.Equal(observed, retained!.Observed);
        Assert.NotEqual(newArtifact.Header.CommitSha, retained.Observed.CommitSha);
        Assert.NotEqual(newArtifact.ContentSha256, retained.Observed.ArtifactContentSha256);
        Assert.Equal(1, retained.Version);
        Assert.Single((await _store.GetAuditAsync(link.Id)).Events);
    }

    [Fact]
    public async Task Concurrent_parent_delete_and_child_create_never_leave_an_active_orphan()
    {
        var term = Term();
        var link = Link(term.Id);
        await _store.SaveTermAsync(term, 0);
        var save = _store.SaveLinkAsync(link, 0);
        var delete = _store.DeleteTermAsync(term.Id, 1, "Reviewer B");
        await Task.WhenAll(save, delete);
        Assert.Contains((await save).Status, new[] { GlossaryMutationStatus.Created, GlossaryMutationStatus.NotFound });
        Assert.Equal(GlossaryMutationStatus.Deleted, (await delete).Status);
        Assert.DoesNotContain((await _store.GetSnapshotAsync()).Links, l => l.Id == link.Id);
    }

    [Fact]
    public async Task Precancelled_mutations_do_not_create_update_delete_or_add_audit_events()
    {
        var term = Term();
        var neverCreated = Term();
        await _store.SaveTermAsync(term, 0);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.SaveTermAsync(neverCreated, 0, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.SaveTermAsync(term with { Name = "Cancelled update" }, 1, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.DeleteTermAsync(term.Id, 1, "Reviewer B", cancelled.Token));
        Assert.Null(await _store.GetTermAsync(neverCreated.Id));
        var retained = await _store.GetTermAsync(term.Id);
        Assert.False(retained!.Deleted);
        Assert.Equal(term.Name, retained.Name);
        Assert.Equal(1, retained.Version);
        Assert.Single((await _store.GetAuditAsync(term.Id)).Events);
        Assert.Empty((await _store.GetAuditAsync(neverCreated.Id)).Events);
    }

    private async Task<string> ArtifactRowsAsync(long artifactId)
    {
        var rows = new List<string>();
        foreach (var table in new[] { "artifact", "node", "edge", "evidence", "calculation", "diagnostic" })
        {
            var key = table == "artifact" ? "id" : "artifact_id";
            await using var command = db.DataSource.CreateCommand($"select row_to_json(t)::text from {table} t where {key} = $1 order by row_to_json(t)::text");
            command.Parameters.AddWithValue(artifactId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        }
        return string.Join('\n', rows);
    }
}
