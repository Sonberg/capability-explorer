using CapabilityExplorer.Contracts;
using CapabilityExplorer.Explorer;
using CapabilityExplorer.Store;
using Npgsql;
using Xunit;

namespace CapabilityExplorer.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class PostgresExplorerQueryTests(PostgresFixture db)
{
    private readonly PostgresExplorerQueries _queries = new(db.DataSource, new ExplorerQueryCursor(new byte[32]));

    private async Task<ExplorerQueryContext> Snapshot(string? capability = null, long generation = 1)
    {
        var cap = capability ?? "queries-" + Guid.NewGuid().ToString("N");
        var snapshot = Guid.NewGuid();
        await using var insert = db.DataSource.CreateCommand("""
            insert into analysis_snapshot(id,capability_id,status,input_identity,generation,published_at)
            values(@id,@cap,'published',@identity,@generation,now())
            """);
        insert.Parameters.AddWithValue("id", snapshot); insert.Parameters.AddWithValue("cap", cap);
        insert.Parameters.AddWithValue("identity", snapshot.ToString()); insert.Parameters.AddWithValue("generation", generation);
        await insert.ExecuteNonQueryAsync();
        foreach (var file in Fixtures.GoldenFiles.Where(x => x.Contains("/v1-base/", StringComparison.Ordinal)))
        {
            var artifact = ArtifactValidator.Validate(Fixtures.Golden(file)).Artifact!;
            var imported = await new ArtifactImporter(db.DataSource).ImportAsync(artifact);
            await using var member = db.DataSource.CreateCommand("""
                insert into analysis_snapshot_artifact(snapshot_id,artifact_id,repository_id,commit_sha,unit_path)
                select @snapshot,id,repository_id,commit_sha,unit_path from artifact where id=@artifact;
                insert into snapshot_revision(snapshot_id,repository_id,commit_sha,label)
                select @snapshot,repository_id,commit_sha,'base' from artifact where id=@artifact on conflict do nothing;
                """);
            member.Parameters.AddWithValue("snapshot", snapshot); member.Parameters.AddWithValue("artifact", imported.ArtifactId);
            await member.ExecuteNonQueryAsync();
        }
        await using var publish = db.DataSource.CreateCommand("""
            insert into published_snapshot(capability_id,snapshot_id,generation) values(@cap,@snapshot,@generation)
            on conflict(capability_id) do update set snapshot_id=excluded.snapshot_id,generation=excluded.generation
            """);
        publish.Parameters.AddWithValue("cap", cap); publish.Parameters.AddWithValue("snapshot", snapshot); publish.Parameters.AddWithValue("generation", generation);
        await publish.ExecuteNonQueryAsync();
        return (await _queries.ResolveAsync(cap))!;
    }

    [Fact]
    public async Task SearchPagesAreStableUniqueBoundedAndRetainRank()
    {
        var context = await Snapshot();
        var all = new List<SearchHit>(); string? cursor = null;
        do
        {
            var page = await _queries.SearchAsync(context, null, null, null, new(3, cursor));
            Assert.InRange(page.Items.Count, 1, 3); Assert.Equal(context.SnapshotId, page.SnapshotId);
            all.AddRange(page.Items); cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.True(all.Count > 10); Assert.Equal(all.Count, all.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count());
        var name = all.First(x => x.Name.Length > 3).Name;
        var matches = await _queries.SearchAsync(context, name, null, null, new(100));
        Assert.NotEmpty(matches.Items); Assert.Equal(name, matches.Items[0].Name, StringComparer.OrdinalIgnoreCase);
        var prefixed = await _queries.SearchAsync(context, name[..3], null, null, new(100));
        Assert.Contains(prefixed.Items, x => x.Key == matches.Items[0].Key);
        var typed = await _queries.SearchAsync(context, null, all[0].Kind, all[0].RepositoryId, new(100));
        Assert.All(typed.Items, x => { Assert.Equal(all[0].Kind, x.Kind); Assert.Equal(all[0].RepositoryId, x.RepositoryId); });
    }

    [Fact]
    public async Task SearchMatchesExistingGoldenViewForExactPrefixSubstringAndKeyQueries()
    {
        var context = await Snapshot();
        var index = ExplorerIndex.Load([Fixtures.ExpectedDirectory], null);
        var view = index.View(string.Join(',', context.Selection.Select(x => x.Key + ":" + x.Value)),
            new ViewScope(context.Capability, context.Selection.Keys.ToHashSet(StringComparer.Ordinal), context.Selection));
        foreach (var query in new[] { "", "Order", "ord", "total", "cs|", "ts|", "http|", "not-present" })
        {
            var expected = Queries.Search(view, query, null, null, int.MaxValue);
            var actual = new List<SearchHit>(); string? cursor = null;
            do
            {
                var page = await _queries.SearchAsync(context, query, null, null, new(7, cursor));
                actual.AddRange(page.Items); cursor = page.NextCursor;
            } while (cursor is not null);
            Assert.Equal(expected.OrderBy(x => x.Key, StringComparer.Ordinal), actual.OrderBy(x => x.Key, StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task LiteralSearchDoesNotTreatSqlWildcardsAsPatterns()
    {
        var context = await Snapshot();
        Assert.Empty((await _queries.SearchAsync(context, "%_does-not-exist", null, null, new(10))).Items);
        Assert.Empty((await _queries.SearchAsync(context, "' OR true --", null, null, new(10))).Items);
    }

    [Fact]
    public async Task PublishedPointerAdvanceDoesNotChangeOldPagesAndCursorScopeCannotChange()
    {
        var old = await Snapshot();
        var first = await _queries.SearchAsync(old, null, null, null, new(2));
        Assert.NotNull(first.NextCursor);
        var newer = await Snapshot(old.Capability, 2);
        Assert.NotEqual(old.SnapshotId, newer.SnapshotId);
        var resolvedOld = await _queries.ResolveAsync(old.Capability, snapshotId: old.SnapshotId);
        Assert.Equal(old.SnapshotId, resolvedOld!.SnapshotId);
        Assert.NotEmpty((await _queries.SearchAsync(resolvedOld, null, null, null, new(2, first.NextCursor))).Items);
        await Assert.ThrowsAsync<ArgumentException>(() => _queries.SearchAsync(newer, null, null, null, new(2, first.NextCursor)));
        await Assert.ThrowsAsync<ArgumentException>(() => _queries.SearchAsync(old, "other", null, null, new(2, first.NextCursor)));
        Assert.Null(await _queries.ResolveAsync("another-capability", snapshotId: old.SnapshotId));
    }

    [Fact]
    public async Task MetadataRevisionsArtifactsAndSymbolEvidenceAreDatabaseOnly()
    {
        var context = await Snapshot();
        Assert.Equal("published", (await _queries.MetadataAsync(context)).Status);
        Assert.NotEmpty((await _queries.RevisionsAsync(context, new(1))).Items);
        var artifacts = await _queries.ArtifactsAsync(context, new(1));
        Assert.Single(artifacts.Items); Assert.NotNull(artifacts.NextCursor);
        var hit = (await _queries.SearchAsync(context, null, null, null, new(1))).Items[0];
        var node = await _queries.SymbolAsync(context, hit.Key); Assert.NotNull(node);
        var evidence = await _queries.EvidenceAsync(context, hit.Key, new(1)); Assert.Single(evidence.Items);
        var e = evidence.Items[0]; Assert.True(await _queries.IsCitedAsync(context, e.RepositoryId, e.CommitSha, e.Path));
        Assert.False(await _queries.IsCitedAsync(context, e.RepositoryId, e.CommitSha, "not-cited.txt"));
        Assert.Null(await _queries.SymbolAsync(context, "missing"));
        foreach (var direction in new[] { "incoming", "outgoing" })
            Assert.InRange((await _queries.EdgesAsync(context, hit.Key, direction, new(1))).Items.Count, 0, 1);
        await Assert.ThrowsAsync<ArgumentException>(() => _queries.EdgesAsync(context, hit.Key, "bad", new(1)));
    }

    [Fact]
    public async Task CalculationsAreMetadataPagesAndBodyIsExplicitDetail()
    {
        var context = await Snapshot();
        var page = await _queries.CalculationsAsync(context, new(1)); Assert.Single(page.Items);
        var detail = await _queries.CalculationAsync(context, page.Items[0].Key); Assert.NotNull(detail);
        Assert.Equal(page.Items[0].Key, detail.Metadata.Key); Assert.True(detail.Body.TryGetProperty("expr", out _));
        Assert.Null(await _queries.CalculationAsync(context, "missing"));
    }

    [Fact]
    public async Task UnknownSelectionAndCancellationRejectWithoutFileFallback()
    {
        var context = await Snapshot();
        await Assert.ThrowsAsync<ArgumentException>(() => _queries.ResolveAsync(context.Capability, new Dictionary<string, string> { ["outside"] = new('a', 40) }));
        var repo = context.Selection.Keys.First();
        await Assert.ThrowsAsync<ArgumentException>(() => _queries.ResolveAsync(context.Capability, new Dictionary<string, string> { [repo] = new('f', 40) }));
        await Assert.ThrowsAsync<ArgumentException>(() => _queries.SearchAsync(context, new string('a', 501), null, null, new(50)));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _queries.SearchAsync(context, null, null, null, new(50), cancelled.Token));
    }
}
