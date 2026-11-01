using System.Text.Json.Nodes;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Store;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace CapabilityExplorer.Tests;

/// <summary>One disposable PostgreSQL (with pgvector) per test run, started by Testcontainers. Never a shared or local database.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:0.8.6-pg17@sha256:cf134a767f474095eeba57e0117be8e568e011a63f33fbf252f14c9b760f8e6f").Build();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    /// <summary>Includes the password (NpgsqlDataSource.ConnectionString strips it).</summary>
    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        DataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
        await Migrator.MigrateAsync(DataSource);
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }

    public async Task<long> CountAsync(string table)
    {
        await using var command = DataSource.CreateCommand($"select count(*) from {table}");
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public async Task<IReadOnlyDictionary<string, long>> CountAllAsync()
    {
        var counts = new Dictionary<string, long>();
        foreach (var table in new[] { "artifact", "node", "edge", "evidence", "calculation", "diagnostic" })
        {
            counts[table] = await CountAsync(table);
        }

        return counts;
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var command = DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "database";
}

/// <summary>Tests share one database and run sequentially; each asserts on deltas, not absolute state.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class ImporterTests(PostgresFixture db)
{
    private readonly ArtifactImporter _importer = new(db.DataSource);

    private static string Mutate(string file, Action<List<JsonObject>> change)
    {
        var records = Fixtures.Golden(file).TrimEnd('\n').Split('\n').Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        change(records);
        return string.Join('\n', records.Select(r => r.ToJsonString())) + "\n";
    }

    [Fact]
    public async Task Migrations_are_idempotent()
    {
        Assert.Empty(await Migrator.MigrateAsync(db.DataSource));
    }

    [Fact]
    public async Task All_golden_artifacts_import_with_every_record()
    {
        foreach (var file in Fixtures.GoldenFiles)
        {
            var artifact = ArtifactValidator.Validate(Fixtures.Golden(file)).Artifact!;
            var result = await _importer.ImportAsync(artifact);

            foreach (var (table, expected) in new (string, int)[]
            {
                ("node", artifact.Nodes.Count), ("edge", artifact.Edges.Count), ("evidence", artifact.Evidence.Count),
                ("calculation", artifact.Calculations.Count), ("diagnostic", artifact.Diagnostics.Count),
            })
            {
                await using var command = db.DataSource.CreateCommand($"select count(*) from {table} where artifact_id = $1");
                command.Parameters.AddWithValue(result.ArtifactId);
                Assert.Equal(expected, (long)(await command.ExecuteScalarAsync())!);
            }
        }
    }

    [Fact]
    public async Task Reimport_is_a_no_op()
    {
        var jsonl = Fixtures.Golden("web-ts/v1-base/_fixture_web.jsonl");
        var first = await _importer.ImportAsync(jsonl);
        var before = await db.CountAllAsync();

        var again = await _importer.ImportAsync(jsonl);

        Assert.Equal(first.ArtifactId, again.ArtifactId);
        Assert.Equal(ImportOutcome.AlreadyImported, again.Outcome);
        Assert.Equal(before, await db.CountAllAsync());
    }

    [Fact]
    public async Task Concurrent_duplicate_imports_publish_exactly_once()
    {
        var jsonl = Mutate("worker-cs/v1-base/Sales.Worker.jsonl", r => r[0]["configHash"] = new string('c', 64));
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => _importer.ImportAsync(jsonl))));

        Assert.Single(results, r => r.Outcome == ImportOutcome.Imported);
        Assert.Single(results.Select(r => r.ArtifactId).Distinct());
    }

    [Fact]
    public async Task Same_analysis_key_with_different_content_is_a_conflict_not_an_overwrite()
    {
        const string file = "api-cs/v1-base/Sales.Domain.jsonl";
        await _importer.ImportAsync(Fixtures.Golden(file));
        var before = await db.CountAllAsync();
        var changed = Mutate(file, r => r.First(x => (string)x["kind"]! == "node")["name"] = "Renamed");

        await Assert.ThrowsAsync<ArtifactConflictException>(() => _importer.ImportAsync(changed));
        Assert.Equal(before, await db.CountAllAsync());
    }

    [Fact]
    public async Task Invalid_artifact_writes_nothing()
    {
        var before = await db.CountAllAsync();
        var truncated = Mutate("api-cs/v2-rename/Sales.Api.jsonl", r => r.RemoveAt(r.Count - 1));

        var error = await Assert.ThrowsAsync<InvalidArtifactException>(() => _importer.ImportAsync(truncated));
        Assert.Contains(error.Problems, p => p.Message.Contains("truncated", StringComparison.Ordinal));
        Assert.Equal(before, await db.CountAllAsync());
    }

    [Fact]
    public async Task Failure_midway_through_the_copy_rolls_back_the_whole_artifact()
    {
        // A trigger that fails on the last table written proves the earlier tables are rolled back too.
        await db.ExecuteAsync("""
            create or replace function fail_in_test() returns trigger language plpgsql as
            $$ begin raise exception 'injected failure'; end $$;
            create trigger fail_in_test before insert on diagnostic for each row execute function fail_in_test();
            """);
        try
        {
            var jsonl = Mutate("worker-cs/v2-broken/Sales.Worker.jsonl", r => r[0]["configHash"] = new string('f', 64));
            var before = await db.CountAllAsync();

            await Assert.ThrowsAnyAsync<PostgresException>(() => _importer.ImportAsync(jsonl));
            Assert.Equal(before, await db.CountAllAsync());
        }
        finally
        {
            await db.ExecuteAsync("drop trigger fail_in_test on diagnostic; drop function fail_in_test();");
        }
    }

    [Fact]
    public async Task Partial_artifacts_are_stored_as_partial_with_their_diagnostics()
    {
        var result = await _importer.ImportAsync(Fixtures.Golden("worker-cs/v2-broken/Sales.Worker.jsonl"));
        await using var command = db.DataSource.CreateCommand(
            "select a.status, count(d.*) filter (where d.affects_coverage) from artifact a join diagnostic d on d.artifact_id = a.id where a.id = $1 group by a.status");
        command.Parameters.AddWithValue(result.ArtifactId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("partial", reader.GetString(0));
        Assert.Equal(2, reader.GetInt64(1));
    }

    [Fact]
    public async Task Occurrences_are_queryable_per_revision_by_symbol_key()
    {
        foreach (var tag in new[] { "v1-base", "v2-rename" })
        {
            await _importer.ImportAsync(Fixtures.Golden($"api-cs/{tag}/Sales.Api.jsonl"));
        }

        await using var command = db.DataSource.CreateCommand("""
            select a.commit_sha, e.path, e.start_line
            from node n
            join artifact a on a.id = n.artifact_id
            join evidence e on e.artifact_id = n.artifact_id and e.local_id = n.evidence[1]
            where n.symbol_key like $1
            order by a.commit_sha
            """);
        command.Parameters.AddWithValue("cs|api-cs|Sales.Api|M:Sales.Api.Calculations.WeeklySalesCalculator.%");
        var rows = new List<(string Sha, string Path)>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                rows.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        Assert.Contains((Fixtures.Lock["api-cs"]["v1-base"], "src/Sales.Api/Calculations/WeeklySalesCalculator.cs"), rows);
        Assert.Contains((Fixtures.Lock["api-cs"]["v2-rename"], "src/Sales.Api/Calculations/Weekly/WeeklySalesCalculator.cs"), rows);
    }
}
