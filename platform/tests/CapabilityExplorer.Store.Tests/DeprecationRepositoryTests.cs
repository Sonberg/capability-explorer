using System.Text;
using CapabilityExplorer.Store;
using Npgsql;
using Xunit;

namespace CapabilityExplorer.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class DeprecationRepositoryTests(PostgresFixture db)
{
    [Fact]
    public async Task Versioned_create_update_stale_update_and_delete_are_persisted()
    {
        var repository = new PostgresDeprecationRepository(db.DataSource);
        var key = "test|" + Guid.NewGuid().ToString("N");

        var created = await repository.UpsertAsync(new(key, DeprecationStatus.Deprecated, "first"));
        Assert.True(created.Version > 0);
        var updated = await repository.UpsertAsync(new(key, DeprecationStatus.Obsolete, "second", created.Version));
        Assert.True(updated.Version > created.Version);

        await Assert.ThrowsAsync<DeprecationVersionConflictException>(
            () => repository.UpsertAsync(new(key, DeprecationStatus.Deprecated, "stale", created.Version)));
        var persisted = Assert.Single(await repository.ListAsync(), mark => mark.Key == key);
        Assert.Equal((DeprecationStatus.Obsolete, "second", updated.Version),
            (persisted.Status, persisted.Note, persisted.Version));

        await Assert.ThrowsAsync<DeprecationVersionConflictException>(
            () => repository.DeleteAsync(key, created.Version));
        await repository.DeleteAsync(key, updated.Version);
        Assert.DoesNotContain(await repository.ListAsync(), mark => mark.Key == key);
        var recreated = await repository.UpsertAsync(new(key, DeprecationStatus.Deprecated, "recreated"));
        Assert.True(recreated.Version > updated.Version);
        await Assert.ThrowsAsync<DeprecationVersionConflictException>(
            () => repository.UpsertAsync(new(key, DeprecationStatus.Obsolete, "old lifetime", created.Version)));
    }

    [Fact]
    public async Task Explicit_import_is_idempotent_preserves_timestamp_and_reports_invalid_or_conflicting_entries()
    {
        var repository = new PostgresDeprecationRepository(db.DataSource);
        var importedKey = "import|" + Guid.NewGuid().ToString("N");
        var conflictKey = "conflict|" + Guid.NewGuid().ToString("N");
        const string markedAt = "2024-02-03T04:05:06.7890123+02:00";
        await repository.UpsertAsync(new(conflictKey, DeprecationStatus.Obsolete, "database value"));
        var json = $$"""
            [
              {"key":"{{importedKey}}","status":"deprecated","note":"kept","markedAt":"{{markedAt}}"},
              {"key":"{{conflictKey}}","status":"deprecated","note":"must not replace","markedAt":"{{markedAt}}"},
              {"key":"bad-status","status":"active","note":"invalid","markedAt":"{{markedAt}}"},
              {"key":"{{importedKey}}","status":"obsolete","note":"duplicate conflict","markedAt":"{{markedAt}}"}
            ]
            """;

        var first = await Import(repository, json);
        Assert.Equal(1, first.Imported);
        Assert.Equal(0, first.Unchanged);
        Assert.Equal(3, first.Issues.Count);
        var mark = Assert.Single(await repository.ListAsync(), value => value.Key == importedKey);
        Assert.Equal(DateTimeOffset.Parse(markedAt), mark.MarkedAt);

        var repeated = await Import(repository, $$"""
            [{"key":"{{importedKey}}","status":"deprecated","note":"kept","markedAt":"{{markedAt}}"}]
            """);
        Assert.Equal(0, repeated.Imported);
        Assert.Equal(1, repeated.Unchanged);
        Assert.Empty(repeated.Issues);
        Assert.Equal("database value", Assert.Single(await repository.ListAsync(), value => value.Key == conflictKey).Note);
    }

    [Fact]
    public async Task Invalid_mutations_are_rejected_without_changing_persisted_value()
    {
        var repository = new PostgresDeprecationRepository(db.DataSource);
        var key = "invalid|" + Guid.NewGuid().ToString("N");
        await Assert.ThrowsAsync<DeprecationValidationException>(
            () => repository.UpsertAsync(new(key, (DeprecationStatus)42, "invalid")));

        var created = await repository.UpsertAsync(new(key, DeprecationStatus.Deprecated, "valid"));
        await Assert.ThrowsAsync<DeprecationValidationException>(
            () => repository.DeleteAsync(key, 0));
        var persisted = Assert.Single(await repository.ListAsync(), mark => mark.Key == key);
        Assert.Equal((created.Status, created.Note, created.Version), (persisted.Status, persisted.Note, persisted.Version));
    }

    [Fact]
    public async Task Database_outage_does_not_return_an_empty_success_or_use_a_file()
    {
        var settings = new NpgsqlConnectionStringBuilder(db.ConnectionString)
            { Host = "127.0.0.1", Port = 1, Timeout = 1, Pooling = false };
        await using var unavailable = NpgsqlDataSource.Create(settings.ConnectionString);
        var repository = new PostgresDeprecationRepository(unavailable);
        await Assert.ThrowsAnyAsync<NpgsqlException>(() => repository.ListAsync());
        await Assert.ThrowsAnyAsync<NpgsqlException>(
            () => repository.UpsertAsync(new("outage", DeprecationStatus.Deprecated, "no fallback")));
        await Assert.ThrowsAnyAsync<NpgsqlException>(() => repository.DeleteAsync("outage", 1));
    }

    [Fact]
    public async Task Concurrent_writers_with_one_token_have_exactly_one_winner()
    {
        var repository = new PostgresDeprecationRepository(db.DataSource);
        var mark = await repository.UpsertAsync(new("race|" + Guid.NewGuid().ToString("N"), DeprecationStatus.Deprecated, ""));
        async Task<bool> Write(string note)
        {
            try
            {
                await repository.UpsertAsync(new(mark.Key, DeprecationStatus.Obsolete, note, mark.Version));
                return true;
            }
            catch (DeprecationVersionConflictException) { return false; }
        }
        var winners = await Task.WhenAll(Write("first"), Write("second"));
        Assert.Single(winners, won => won);
    }

    [Fact]
    public async Task Invalid_legacy_entries_are_reported_individually_without_preventing_valid_entries()
    {
        var repository = new PostgresDeprecationRepository(db.DataSource);
        var key = "validation|" + Guid.NewGuid().ToString("N");
        var report = await Import(repository, $$"""
            [
              null,
              {"key":"missing-note","status":"deprecated","markedAt":"2024-01-01T00:00:00Z"},
              {"key":"invalid-date","status":"obsolete","note":"","markedAt":"not-a-date"},
              {"key":"duplicate-field","Key":"ambiguous","status":"obsolete","note":"","markedAt":"2024-01-01T00:00:00Z"},
              {"key":"{{key}}","status":"obsolete","note":"","markedAt":"2024-01-01T00:00:00Z"}
            ]
            """);

        Assert.Equal(1, report.Imported);
        Assert.Equal(4, report.Issues.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, report.Issues.Select(issue => issue.Entry));
        Assert.Single(await repository.ListAsync(), mark => mark.Key == key);
        await Assert.ThrowsAsync<DeprecationValidationException>(() => Import(repository, "{}"));
        await Assert.ThrowsAsync<DeprecationValidationException>(
            () => Import(repository, "[" + string.Join(',', Enumerable.Repeat("null", 10_001)) + "]"));
    }

    [Fact]
    public async Task Valid_extreme_timestamps_remain_finite_and_round_trip_without_Npgsql_infinity_conversion()
    {
        var repository = new PostgresDeprecationRepository(db.DataSource);
        var prefix = "extreme|" + Guid.NewGuid().ToString("N");
        DeprecationImportReport? report = null;
        var exception = await Record.ExceptionAsync(async () => report = await Import(repository, $$"""
            [
              {"key":"{{prefix}}min","status":"deprecated","note":"","markedAt":"0001-01-01T00:00:00+00:00"},
              {"key":"{{prefix}}max","status":"obsolete","note":"","markedAt":"9999-12-31T23:59:59.9999999+00:00"}
            ]
            """));

        Assert.Null(exception);
        Assert.Equal(2, report!.Imported);
        Assert.Empty(report.Issues);
        var marks = await repository.ListAsync();
        Assert.Equal(DateTimeOffset.MinValue, Assert.Single(marks, mark => mark.Key == prefix + "min").MarkedAt);
        Assert.Equal(DateTimeOffset.MaxValue, Assert.Single(marks, mark => mark.Key == prefix + "max").MarkedAt);
    }

    [Fact]
    public async Task Explicit_import_and_runtime_CRUD_never_rewrite_capabilities_or_the_legacy_source()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "deprecation-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var capabilities = Path.Combine(directory, "capabilities.json");
            var legacy = Path.Combine(directory, "deprecations.json");
            const string capabilitiesContent = """{"capabilities":[]}""";
            var key = "file-boundary|" + Guid.NewGuid().ToString("N");
            var legacyContent = $$"""
                [{"key":"{{key}}","status":"deprecated","note":"explicit import","markedAt":"2024-01-01T00:00:00Z"}]
                """;
            await File.WriteAllTextAsync(capabilities, capabilitiesContent);
            await File.WriteAllTextAsync(legacy, legacyContent);
            var repository = new PostgresDeprecationRepository(db.DataSource);
            await using (var stream = File.OpenRead(legacy))
                Assert.Equal(1, (await repository.ImportLegacyAsync(stream)).Imported);
            var mark = Assert.Single(await repository.ListAsync(), value => value.Key == key);
            var updated = await repository.UpsertAsync(new(key, DeprecationStatus.Obsolete, "database only", mark.Version));
            await repository.DeleteAsync(key, updated.Version);

            Assert.Equal(capabilitiesContent, await File.ReadAllTextAsync(capabilities));
            Assert.Equal(legacyContent, await File.ReadAllTextAsync(legacy));
            Assert.DoesNotContain(await repository.ListAsync(), value => value.Key == key);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<DeprecationImportReport> Import(PostgresDeprecationRepository repository, string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return await repository.ImportLegacyAsync(stream);
    }
}
