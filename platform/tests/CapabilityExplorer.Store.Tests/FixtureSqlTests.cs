using System.Diagnostics;
using Npgsql;
using Xunit;

namespace CapabilityExplorer.Tests;

/// <summary>
/// The pricing-node migrations are real PostgreSQL. Run them in a throwaway database and check that the
/// engine behaves the way the golden calculation attributes claim, so the fixtures cannot drift into fiction.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class FixtureSqlTests(PostgresFixture db)
{
    private static string GitShow(string repositoryId, string revision, string path)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-C", Fixtures.RepoDirectory(repositoryId), "show", $"{revision}:{path}" })
        {
            start.ArgumentList.Add(a);
        }

        using var process = Process.Start(start)!;
        var text = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        return text;
    }

    private async Task<NpgsqlDataSource> FreshDatabaseAsync(string name)
    {
        await db.ExecuteAsync($"drop database if exists {name} with (force)");
        await db.ExecuteAsync($"create database {name}");
        var builder = new NpgsqlConnectionStringBuilder(db.ConnectionString) { Database = name };
        return NpgsqlDataSource.Create(builder.ConnectionString);
    }

    [Fact]
    public async Task Pricing_migrations_apply_and_weekly_revenue_behaves_as_documented()
    {
        await using var pricing = await FreshDatabaseAsync("fixture_pricing");
        var sha = Fixtures.Lock["pricing-node"]["v1-base"];
        foreach (var migration in new[] { "db/migrations/001_pricing.sql", "db/migrations/002_weekly_revenue.sql" })
        {
            await using var apply = pricing.CreateCommand(GitShow("pricing-node", sha, migration));
            await apply.ExecuteNonQueryAsync();
        }

        await using (var seed = pricing.CreateCommand("""
            set time zone 'UTC';
            insert into pricing.sales_ledger (article_id, sold_at, amount_ore, quantity, currency) values
              ('A1', '2026-09-07 10:00Z', 1000, 1, 'SEK'),  -- ISO week 37
              ('A1', '2026-09-08 10:00Z', 1001, 1, 'SEK'),  -- same week
              ('A1', '2026-09-21 10:00Z', 1000, 1, 'EUR');  -- week 39; week 38 has no sales
            """))
        {
            await seed.ExecuteNonQueryAsync();
        }

        async Task<decimal?> WeeklyRevenue(string from, string to)
        {
            await using var q = pricing.CreateCommand("select pricing.weekly_revenue('A1', $1::date, $2::date)");
            q.Parameters.AddWithValue(from);
            q.Parameters.AddWithValue(to);
            var value = await q.ExecuteScalarAsync();
            return value is DBNull ? null : (decimal)value!;
        }

        // denominatorSource=data: 2 weeks with sales (not the 3 calendar weeks). currency=mixedCurrenciesSummed: SEK + EUR added.
        // (10.00 + 10.01 + 10.00) / 2 = 15.005 → rounding=halfAwayFromZero on numeric → 15.01
        Assert.Equal(15.01m, await WeeklyRevenue("2026-09-07", "2026-09-27"));
        // zeroDenominator=returnsNull: no rows → NULLIF(0) → NULL
        Assert.Null(await WeeklyRevenue("2026-01-01", "2026-01-31"));
        // period is half-open [from, to + 1): a sale on the last day counts.
        Assert.Equal(10.00m, await WeeklyRevenue("2026-09-21", "2026-09-21"));

        // overflow=throws: bigint * integer beyond range raises instead of wrapping.
        await using (var big = pricing.CreateCommand("insert into pricing.sales_ledger (article_id, sold_at, amount_ore, quantity, currency) values ('A2', '2026-09-07Z', 9223372036854775807, 2, 'SEK')"))
        {
            await big.ExecuteNonQueryAsync();
        }

        await using var overflow = pricing.CreateCommand("select pricing.weekly_revenue('A2', '2026-09-01', '2026-09-30')");
        var error = await Assert.ThrowsAsync<PostgresException>(() => overflow.ExecuteScalarAsync());
        Assert.Equal(PostgresErrorCodes.NumericValueOutOfRange, error.SqlState);
    }
}
