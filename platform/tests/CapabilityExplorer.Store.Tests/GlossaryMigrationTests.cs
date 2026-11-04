using CapabilityExplorer.Store;
using Npgsql;
using Xunit;

namespace CapabilityExplorer.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class GlossaryMigrationTests(PostgresFixture db)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fresh_and_existing_databases_apply_glossary_once_and_keep_prior_rows(bool upgrade)
    {
        var name = "glossary_test_" + Guid.NewGuid().ToString("N");
        await db.ExecuteAsync($"create database {name}");
        var connection = new NpgsqlConnectionStringBuilder(db.ConnectionString) { Database = name };
        try
        {
            await using var source = NpgsqlDataSource.Create(connection.ConnectionString);
            if (upgrade)
            {
                await using var create = source.CreateCommand("create table schema_migration (name text primary key, applied_at timestamptz not null default now())");
                await create.ExecuteNonQueryAsync();
                var assembly = typeof(Migrator).Assembly;
                foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith("migrations.", StringComparison.Ordinal) && string.CompareOrdinal(n, "migrations.0004") < 0).Order(StringComparer.Ordinal))
                {
                    await using var stream = assembly.GetManifestResourceStream(resource)!;
                    using var reader = new StreamReader(stream);
                    await using var apply = source.CreateCommand(await reader.ReadToEndAsync());
                    await apply.ExecuteNonQueryAsync();
                    await using var remember = source.CreateCommand("insert into schema_migration(name) values ($1)");
                    remember.Parameters.AddWithValue(resource["migrations.".Length..]);
                    await remember.ExecuteNonQueryAsync();
                }
                await using var prior = source.CreateCommand("insert into repository(id) values ('prior-repository')");
                await prior.ExecuteNonQueryAsync();
            }

            var applied = await Migrator.MigrateAsync(source);
            Assert.Equal(upgrade ? 6 : 9, applied.Count);
            Assert.Contains("0009_model_products.sql", applied);
            Assert.Contains("0006_glossary_proposal_review.sql", applied);
            Assert.Contains("0005_analysis_snapshots.sql", applied);
            Assert.Contains("0007_publication_reconciliation.sql", applied);
            Assert.Contains("0008_deprecations.sql", applied);
            Assert.Contains("0004_glossary.sql", applied);
            Assert.Empty(await Migrator.MigrateAsync(source));
            var store = new GlossaryStore(source);
            Assert.Equal(GlossaryMutationStatus.Created, (await store.SaveTermAsync(new(Guid.NewGuid(), "Term", "Definition", null, "Reviewer"), 0)).Status);
            Assert.Single((await store.GetSnapshotAsync()).Terms);
            if (upgrade)
            {
                await using var prior = source.CreateCommand("select count(*) from repository where id = 'prior-repository'");
                Assert.Equal(1L, await prior.ExecuteScalarAsync());
            }
        }
        finally
        {
            await db.ExecuteAsync($"drop database {name} with (force)");
        }
    }
}
