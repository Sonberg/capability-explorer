using CapabilityExplorer.Binding;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Store;
using Npgsql;
using Xunit;

namespace CapabilityExplorer.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class BindingSnapshotUpgradeTests(PostgresFixture db)
{
    [Fact]
    public async Task Upgraded_import_preserves_legacy_snapshot_and_creates_every_revision_occurrence_under_a_new_identity()
    {
        var name="binding_upgrade_"+Guid.NewGuid().ToString("N");
        await db.ExecuteAsync($"create database {name}");
        try
        {
            var settings=new NpgsqlConnectionStringBuilder(db.ConnectionString){Database=name};
            await using var source=NpgsqlDataSource.Create(settings.ConnectionString);
            await Migrator.MigrateAsync(source);
            var inputs=new List<(long ArtifactId,Artifact Artifact)>();
            foreach(var path in new[]{"pricing-node/v1-base/_fixture_pricing.jsonl","pricing-node/v2-move/_fixture_pricing.jsonl","forecast-cs/v1-base/Forecast.Api.jsonl"})
            {
                var artifact=ArtifactValidator.Validate(Fixtures.Golden(path)).Artifact!;
                inputs.Add(((await new ArtifactImporter(source).ImportAsync(artifact)).ArtifactId,artifact));
            }
            // Reproduce the actual old writer: its PK silently collapsed the unchanged edge across revisions.
            await Execute(source,"alter table bound_edge drop constraint bound_edge_pkey; alter table bound_edge add primary key(snapshot_id,local_id)");
            var bindings=BindingFile.Load(Path.Combine(Fixtures.RepositoryRoot,"bindings.json"));
            var store=new BindingStore(source);
            var legacy=await store.SaveAsync(inputs,bindings);
            Assert.Equal(2,legacy.BoundEdges);
            Assert.Equal(1L,await CountEdges(source,legacy.SnapshotId));
            // Apply the same occurrence-key upgrade as migration0005. It cannot restore previously dropped rows.
            await Execute(source,"alter table bound_edge drop constraint bound_edge_pkey; alter table bound_edge add primary key(snapshot_id,artifact_id,local_id)");
            Assert.Equal(1L,await CountEdges(source,legacy.SnapshotId));
            var current=await store.SaveImportedAsync(inputs.Select(i=>i.ArtifactId).ToArray(),bindings);
            Assert.True(current.Created);
            Assert.NotEqual(legacy.SnapshotId,current.SnapshotId);
            Assert.Equal(2,current.BoundEdges);
            Assert.Equal(2L,await CountEdges(source,current.SnapshotId));
            Assert.Equal(1L,await CountEdges(source,legacy.SnapshotId));
            var retry=await store.SaveImportedAsync(inputs.Select(i=>i.ArtifactId).Reverse().ToArray(),bindings);
            Assert.False(retry.Created);
            Assert.Equal(current.SnapshotId,retry.SnapshotId);
            Assert.Equal(2L,await CountEdges(source,current.SnapshotId));
            await using var provenance=source.CreateCommand("select count(distinct artifact_id) from bound_edge where snapshot_id=$1");
            provenance.Parameters.AddWithValue(current.SnapshotId);
            Assert.Equal(2L,await provenance.ExecuteScalarAsync());
        }
        finally { await db.ExecuteAsync($"drop database {name} with(force)"); }
    }

    private static async Task<long> CountEdges(NpgsqlDataSource source,string id)
    {
        await using var command=source.CreateCommand("select count(*) from bound_edge where snapshot_id=$1");
        command.Parameters.AddWithValue(id);
        return(long)(await command.ExecuteScalarAsync())!;
    }
    private static async Task Execute(NpgsqlDataSource source,string sql)
    {
        await using var command=source.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
}
