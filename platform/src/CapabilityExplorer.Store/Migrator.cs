using System.Reflection;
using Npgsql;

namespace CapabilityExplorer.Store;

/// <summary>Applies embedded Migrations/*.sql in order, once each, under an advisory lock.</summary>
public static class Migrator
{
    private const long LockKey = 0x43455f4d4947; // "CE_MIG"

    public static async Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        var assembly = Assembly.GetExecutingAssembly();
        var migrations = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("migrations.", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await Execute(connection, tx, $"select pg_advisory_xact_lock({LockKey})", cancellationToken);
        await Execute(connection, tx, "create table if not exists schema_migration (name text primary key, applied_at timestamptz not null default now())", cancellationToken);

        var applied = new List<string>();
        foreach (var resource in migrations)
        {
            var name = resource["migrations.".Length..];
            await using (var check = new NpgsqlCommand("select 1 from schema_migration where name = $1", connection, tx))
            {
                check.Parameters.AddWithValue(name);
                if (await check.ExecuteScalarAsync(cancellationToken) is not null)
                {
                    continue;
                }
            }

            await using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            await Execute(connection, tx, await reader.ReadToEndAsync(cancellationToken), cancellationToken);
            await using var record = new NpgsqlCommand("insert into schema_migration (name) values ($1)", connection, tx);
            record.Parameters.AddWithValue(name);
            await record.ExecuteNonQueryAsync(cancellationToken);
            applied.Add(name);
        }

        await tx.CommitAsync(cancellationToken);
        // Explicit trusted migration/import work, not Explorer request-time work.
        // Return this connection first: callers may deliberately budget a one-connection pool.
        await connection.CloseAsync();
        await NodeSearchNormalization.EnsureAsync(dataSource, cancellationToken: cancellationToken);
        return applied;
    }

    private static async Task Execute(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
