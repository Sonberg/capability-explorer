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
