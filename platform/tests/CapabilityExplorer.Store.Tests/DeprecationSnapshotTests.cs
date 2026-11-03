using System.Text;
using System.Text.Json.Nodes;
using CapabilityExplorer.Explorer;
using CapabilityExplorer.Store;
using Xunit;

namespace CapabilityExplorer.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class DeprecationSnapshotTests(PostgresFixture db)
{
    [Fact]
    public async Task Removal_history_stays_in_the_pinned_snapshot_and_stops_at_the_selected_revision()
    {
        var old = await Import("api-cs/v1-base/Sales.Api.jsonl");
        var newer = await Import("api-cs/v2-rename/Sales.Api.jsonl");
        var scope = "marks-" + Guid.NewGuid().ToString("N");
        var snapshots = new SnapshotStore(db.DataSource);
        var oldSha = Fixtures.Lock["api-cs"]["v1-base"];
        var newSha = Fixtures.Lock["api-cs"]["v2-rename"];
        var selected = new Dictionary<string, string>(StringComparer.Ordinal) { ["api-cs"] = newSha };
        var snapshot = await snapshots.CreateAsync(scope, "both", [old, newer], selected);
        await SetOrder(snapshot.Id, oldSha, newSha);
        await snapshots.PublishAsync(snapshot.Id, 0);
        var query = new PostgresDeprecationRemovals(db.DataSource);
        var context = new ExplorerQueryContext(snapshot.Id, scope, 1, selected);

        var removals = await query.ReadAsync(context);
        var removed = Assert.Single(removals.Where(pair => pair.Value.LastSeen == oldSha).Take(1));
        Assert.Equal(("api-cs", oldSha, newSha),
            (removed.Value.RepositoryId, removed.Value.LastSeen, removed.Value.RemovedIn));
        var repository = new PostgresDeprecationRepository(db.DataSource);
        var mark = await repository.UpsertAsync(new(removed.Key, DeprecationStatus.Deprecated, "global"));
        var service = new DeprecationService(repository);
        Assert.Equal(mark.Version, service.WithRemovals(await service.ListAsync(), removals)
            .Single(value => value.Key == mark.Key).Version);

        var subsequent = await snapshots.CreateAsync(scope, "old-only", [old]);
        await snapshots.PublishAsync(subsequent.Id, 1);
        Assert.Equal(removals[removed.Key], (await query.ReadAsync(context))[removed.Key]);
        var earlierSelection = new Dictionary<string, string>(StringComparer.Ordinal) { ["api-cs"] = oldSha };
        Assert.DoesNotContain(removed.Key, (await query.ReadAsync(context with { Selection = earlierSelection })).Keys);
        await repository.DeleteAsync(mark.Key, mark.Version);
    }

    [Fact]
    public async Task Revisions_outside_the_pinned_snapshot_cannot_create_removal_history()
    {
        var old = await Import("api-cs/v1-base/Sales.Api.jsonl");
        _ = await Import("api-cs/v2-rename/Sales.Api.jsonl");
        var scope = "marks-" + Guid.NewGuid().ToString("N");
        var snapshots = new SnapshotStore(db.DataSource);
        var snapshot = await snapshots.CreateAsync(scope, "only-base", [old]);
        await snapshots.PublishAsync(snapshot.Id, 0);
        var selection = new Dictionary<string, string>(StringComparer.Ordinal)
            { ["api-cs"] = Fixtures.Lock["api-cs"]["v1-base"] };
        var query = new PostgresDeprecationRemovals(db.DataSource);

        Assert.Empty(await query.ReadAsync(new(snapshot.Id, scope, 1, selection)));
        selection["api-cs"] = Fixtures.Lock["api-cs"]["v2-rename"];
        await Assert.ThrowsAsync<ArgumentException>(() => query.ReadAsync(new(snapshot.Id, scope, 1, selection)));
    }

    [Fact]
    public async Task Oversized_presence_keys_fail_explicitly_instead_of_materializing_unbounded_history()
    {
        var records = Fixtures.Golden("web-ts/v1-base/_fixture_web.jsonl").TrimEnd('\n')
            .Split('\n').Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
        records[0]["configHash"] = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', records.Select(row => row.ToJsonString())) + "\n"));
        var artifact = (await new ArtifactImporter(db.DataSource).ImportAsync(stream)).ArtifactId;
        var scope = "oversized-" + Guid.NewGuid().ToString("N");
        var snapshots = new SnapshotStore(db.DataSource);
        var snapshot = await snapshots.CreateAsync(scope, "oversized", [artifact]);
        await snapshots.PublishAsync(snapshot.Id, 0);
        await using var mutation = db.DataSource.CreateCommand("""
            update node set symbol_key=repeat('x',16385) where artifact_id=$1 and local_id=
              (select min(local_id) from node where artifact_id=$1)
            """);
        mutation.Parameters.AddWithValue(artifact);
        Assert.Equal(1, await mutation.ExecuteNonQueryAsync());
        var selection = new Dictionary<string, string>(StringComparer.Ordinal)
            { ["web-ts"] = Fixtures.Lock["web-ts"]["v1-base"] };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PostgresDeprecationRemovals(db.DataSource).ReadAsync(new(snapshot.Id, scope, 1, selection)));
    }

    [Theory]
    [InlineData("partial-next-revision")]
    [InlineData("missing-unit-next-revision")]
    [InlineData("partial-other-repository")]
    public async Task Incomplete_coverage_never_claims_a_global_key_was_removed(string uncertainty)
    {
        var old = await ImportIsolated("api-cs/v1-base/Sales.Api.jsonl");
        var newer = await ImportIsolated("api-cs/v2-rename/Sales.Api.jsonl");
        var oldSha = Fixtures.Lock["api-cs"]["v1-base"];
        var newSha = Fixtures.Lock["api-cs"]["v2-rename"];
        var selected = new Dictionary<string, string>(StringComparer.Ordinal) { ["api-cs"] = newSha };
        var artifacts = new List<long> { old, newer };
        long? partial = uncertainty == "partial-next-revision" ? newer : null;
        if (uncertainty == "missing-unit-next-revision")
            artifacts.Add(await ImportIsolated("api-cs/v1-base/Sales.Domain.jsonl"));
        if (uncertainty == "partial-other-repository")
        {
            partial = await ImportIsolated("web-ts/v1-base/_fixture_web.jsonl");
            artifacts.Add(partial.Value);
            selected.Add("web-ts", Fixtures.Lock["web-ts"]["v1-base"]);
        }
        if (partial is { } id)
        {
            await using var mutation = db.DataSource.CreateCommand("update artifact set status='partial' where id=$1");
            mutation.Parameters.AddWithValue(id);
            Assert.Equal(1, await mutation.ExecuteNonQueryAsync());
        }
        var scope = "uncertain-" + Guid.NewGuid().ToString("N");
        var snapshots = new SnapshotStore(db.DataSource);
        var snapshot = await snapshots.CreateAsync(scope, uncertainty, artifacts, selected);
        await SetOrder(snapshot.Id, oldSha, newSha);
        await snapshots.PublishAsync(snapshot.Id, 0);

        var removals = await new PostgresDeprecationRemovals(db.DataSource)
            .ReadAsync(new(snapshot.Id, scope, 1, selected));

        Assert.Empty(removals);
    }

    [Theory]
    [InlineData("chan|test|scope/Orders", "PUBLISHES", "group|channel|Orders")]
    [InlineData("sql|test|dbo.Orders#id", "READS", "group|data|dbo.Orders")]
    public async Task Resource_groups_come_from_edges_not_node_declarations_matching_existing_presence_semantics(
        string target, string relation, string group)
    {
        var old = await ImportIsolated("api-cs/v1-base/Sales.Api.jsonl");
        var newer = await ImportIsolated("api-cs/v2-rename/Sales.Api.jsonl");
        foreach (var id in new[] { old, newer })
        {
            await using var node = db.DataSource.CreateCommand("""
                update node set symbol_key=$2 where artifact_id=$1
                  and local_id=(select min(local_id) from node where artifact_id=$1)
                """);
            node.Parameters.AddWithValue(id);
            node.Parameters.AddWithValue(target);
            Assert.Equal(1, await node.ExecuteNonQueryAsync());
        }
        await using var edge = db.DataSource.CreateCommand("""
            update edge set to_key=$2,relation=$3 where artifact_id=$1
              and local_id=(select min(local_id) from edge where artifact_id=$1)
            """);
        edge.Parameters.AddWithValue(old);
        edge.Parameters.AddWithValue(target);
        edge.Parameters.AddWithValue(relation);
        Assert.Equal(1, await edge.ExecuteNonQueryAsync());
        var scope = "presence-" + Guid.NewGuid().ToString("N");
        var oldSha = Fixtures.Lock["api-cs"]["v1-base"];
        var newSha = Fixtures.Lock["api-cs"]["v2-rename"];
        var selected = new Dictionary<string, string>(StringComparer.Ordinal) { ["api-cs"] = newSha };
        var snapshots = new SnapshotStore(db.DataSource);
        var snapshot = await snapshots.CreateAsync(scope, "groups", [old, newer], selected);
        await SetOrder(snapshot.Id, oldSha, newSha);
        await snapshots.PublishAsync(snapshot.Id, 0);

        var removals = await new PostgresDeprecationRemovals(db.DataSource)
            .ReadAsync(new(snapshot.Id, scope, 1, selected));

        Assert.Equal(newSha, removals[group].RemovedIn);
        Assert.DoesNotContain(target, removals.Keys);
    }

    private async Task<long> ImportIsolated(string fixture)
    {
        var records = Fixtures.Golden(fixture).TrimEnd('\n')
            .Split('\n').Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
        records[0]["configHash"] = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', records.Select(row => row.ToJsonString())) + "\n"));
        return (await new ArtifactImporter(db.DataSource).ImportAsync(stream)).ArtifactId;
    }

    private async Task<long> Import(string fixture) =>
        (await new ArtifactImporter(db.DataSource).ImportAsync(Fixtures.Golden(fixture))).ArtifactId;

    private async Task SetOrder(Guid snapshot, string oldSha, string newSha)
    {
        await using var command = db.DataSource.CreateCommand("""
            update snapshot_revision set commit_time=case when commit_sha=$2 then '2024-01-01'::timestamptz
              when commit_sha=$3 then '2024-01-02'::timestamptz end where snapshot_id=$1
            """);
        command.Parameters.AddWithValue(snapshot);
        command.Parameters.AddWithValue(oldSha);
        command.Parameters.AddWithValue(newSha);
        await command.ExecuteNonQueryAsync();
    }
}
