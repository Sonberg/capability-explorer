using CapabilityExplorer.Store;
using Npgsql;
using Xunit;

namespace CapabilityExplorer.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class DeprecationMigrationTests(PostgresFixture db)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fresh_and_upgrade_migrations_add_global_marks_once_without_changing_existing_rows(bool upgrade)
    {
        var database = "deprecation_test_" + Guid.NewGuid().ToString("N");
        await db.ExecuteAsync($"create database {database}");
        try
        {
            var settings = new NpgsqlConnectionStringBuilder(db.ConnectionString) { Database = database };
            await using var source = NpgsqlDataSource.Create(settings.ConnectionString);
            if (upgrade)
            {
                await using var create = source.CreateCommand(
                    "create table schema_migration(name text primary key,applied_at timestamptz not null default now())");
                await create.ExecuteNonQueryAsync();
                var assembly = typeof(Migrator).Assembly;
                foreach (var resource in assembly.GetManifestResourceNames()
                    .Where(name => name.StartsWith("migrations.", StringComparison.Ordinal)
                        && string.CompareOrdinal(name, "migrations.0008") < 0).Order(StringComparer.Ordinal))
                {
                    await using var stream = assembly.GetManifestResourceStream(resource)!;
                    using var reader = new StreamReader(stream);
                    await using var apply = source.CreateCommand(await reader.ReadToEndAsync());
                    await apply.ExecuteNonQueryAsync();
                    await using var record = source.CreateCommand("insert into schema_migration(name) values($1)");
                    record.Parameters.AddWithValue(resource["migrations.".Length..]);
                    await record.ExecuteNonQueryAsync();
                }
                await using var prior = source.CreateCommand("insert into repository(id) values('existing')");
                await prior.ExecuteNonQueryAsync();
            }

            var applied = await Migrator.MigrateAsync(source);
            Assert.Contains("0008_deprecations.sql", applied);
            Assert.Contains("0009_model_products.sql", applied);
            if (upgrade) Assert.Equal(2, applied.Count);
            Assert.Empty(await Migrator.MigrateAsync(source));
            var repository = new PostgresDeprecationRepository(source);
            var mark = await repository.UpsertAsync(new("group|http|Klarna", DeprecationStatus.Deprecated, "curated"));
            Assert.Equal(mark, Assert.Single(await repository.ListAsync()));
            if (upgrade)
            {
                await using var prior = source.CreateCommand("select count(*) from repository where id='existing'");
                Assert.Equal(1L, await prior.ExecuteScalarAsync());
            }
        }
        finally
        {
            await db.ExecuteAsync($"drop database {database} with (force)");
        }
    }
}
