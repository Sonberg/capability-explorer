using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace CapabilityExplorer.Store;

[SuppressMessage("Security", "CA2100", Justification = "SQL identifiers are selected exclusively from a private enum; all caller values use parameters.")]
public sealed partial class GlossaryStore
{
    private enum Entity { Term, Link, Issue }
    private sealed record Row(string Json, long Version, bool Deleted, Guid? TermId);
    // Reviews are low-volume; one transaction lock prevents create races and parent/child deletion races.
    private const long ReviewLock = 0x43455f474c4f53;

    private async Task<T?> GetAsync<T>(Entity entity, Guid id, Func<Row, T> map, CancellationToken ct) where T : class
    {
        GlossaryValidation.Id(id);
        await using var command = dataSource.CreateCommand($"select data::text, version, deleted, {(entity == Entity.Term ? "null::uuid" : "term_id")} from {Table(entity)} where id = $1");
        command.Parameters.AddWithValue(id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? map(new(reader.GetString(0), reader.GetInt64(1), reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetGuid(3))) : null;
    }

    private async Task<GlossaryMutationResult<T>> SaveAsync<T, TWrite>(Entity entity, Guid id, Guid? termId, TWrite value, string actor, long expectedVersion, Func<Row, T> map, CancellationToken ct)
    {
        GlossaryValidation.Version(expectedVersion, true);
        var json = JsonSerializer.Serialize(value, JsonOptions);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await LockAsync(connection, tx, ct);
        var current = await ReadAsync(connection, tx, entity, id, ct);
        if (current is not null && current.TermId != termId)
            return new(GlossaryMutationStatus.Conflict, map(current));
        if (current is null && expectedVersion != 0)
            return new(GlossaryMutationStatus.NotFound, default);
        if (current is not null)
        {
            var same = JsonNode.DeepEquals(JsonNode.Parse(current.Json), JsonNode.Parse(json));
            if (!current.Deleted && same && (expectedVersion == 0 || current.Version == expectedVersion + 1))
                return new(GlossaryMutationStatus.Unchanged, map(current));
            if (current.Deleted || expectedVersion == 0 || current.Version != expectedVersion)
                return new(GlossaryMutationStatus.Conflict, map(current));
        }

        if (termId is { } parentId)
        {
            var parent = await ReadAsync(connection, tx, Entity.Term, parentId, ct);
            if (parent is null || parent.Deleted) return new(GlossaryMutationStatus.NotFound, default);
        }

        var next = new Row(json, current is null ? 1 : current.Version + 1, false, termId);
        await WriteAsync(connection, tx, entity, id, next, current is null, ct);
        await AuditAsync(connection, tx, entity, id, next, current is null ? "create" : "update", actor, ct);
        await tx.CommitAsync(ct);
        return new(current is null ? GlossaryMutationStatus.Created : GlossaryMutationStatus.Updated, map(next));
    }

    private async Task<GlossaryMutationResult<T>> DeleteAsync<T>(Entity entity, Guid id, long expectedVersion, string actor, Func<Row, T> map, CancellationToken ct)
    {
        GlossaryValidation.Id(id);
        GlossaryValidation.Version(expectedVersion, false);
        GlossaryValidation.Reviewer(actor);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await LockAsync(connection, tx, ct);
        var current = await ReadAsync(connection, tx, entity, id, ct);
        if (current is null) return new(GlossaryMutationStatus.NotFound, default);
        if (current.Deleted && current.Version == expectedVersion + 1)
            return new(GlossaryMutationStatus.Unchanged, map(current));
        if (current.Deleted || current.Version != expectedVersion)
            return new(GlossaryMutationStatus.Conflict, map(current));
        var next = Deleted(current, actor);
        await WriteAsync(connection, tx, entity, id, next, false, ct);
        await AuditAsync(connection, tx, entity, id, next, "delete", actor, ct);
        if (entity == Entity.Term)
        {
            await CascadeAsync(connection, tx, Entity.Link, id, actor, ct);
            await CascadeAsync(connection, tx, Entity.Issue, id, actor, ct);
        }
        await tx.CommitAsync(ct);
        return new(GlossaryMutationStatus.Deleted, map(next));
    }

    private static Row Deleted(Row row, string actor)
    {
        var json = JsonNode.Parse(row.Json)!.AsObject();
        json["reviewer"] = actor;
        return row with { Json = json.ToJsonString(), Version = checked(row.Version + 1), Deleted = true };
    }

    private static async Task CascadeAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Entity entity, Guid termId, string actor, CancellationToken ct)
    {
        var children = new List<(Guid Id, Row Row)>();
        await using (var query = new NpgsqlCommand($"select id, data::text, version, deleted, term_id from {Table(entity)} where term_id = $1 and not deleted order by id", connection, tx))
        {
            query.Parameters.AddWithValue(termId);
            await using var reader = await query.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                children.Add((reader.GetGuid(0), new(reader.GetString(1), reader.GetInt64(2), reader.GetBoolean(3), reader.GetGuid(4))));
        }
        foreach (var (id, row) in children)
        {
            var next = Deleted(row, actor);
            await WriteAsync(connection, tx, entity, id, next, false, ct);
            await AuditAsync(connection, tx, entity, id, next, "delete_cascade", actor, ct);
        }
    }

    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("select pg_advisory_xact_lock($1)", connection, tx);
        command.Parameters.AddWithValue(ReviewLock);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string Table(Entity entity) => entity switch
    {
        Entity.Term => "glossary_term", Entity.Link => "glossary_link", Entity.Issue => "glossary_issue", _ => throw new ArgumentOutOfRangeException(nameof(entity)),
    };

    private static async Task<Row?> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Entity entity, Guid id, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand($"select data::text, version, deleted, {(entity == Entity.Term ? "null::uuid" : "term_id")} from {Table(entity)} where id = $1 for update", connection, tx);
        command.Parameters.AddWithValue(id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(reader.GetString(0), reader.GetInt64(1), reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetGuid(3)) : null;
    }

    private static async Task WriteAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Entity entity, Guid id, Row row, bool create, CancellationToken ct)
    {
        var sql = create
            ? $"insert into {Table(entity)} (id, data, version, deleted{(entity == Entity.Term ? "" : ", term_id")}) values ($1, $2, $3, $4{(entity == Entity.Term ? "" : ", $5")})"
            : $"update {Table(entity)} set data = $2, version = $3, deleted = $4 where id = $1";
        await using var command = new NpgsqlCommand(sql, connection, tx);
        command.Parameters.AddWithValue(id);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = row.Json });
        command.Parameters.AddWithValue(row.Version);
        command.Parameters.AddWithValue(row.Deleted);
        if (create && row.TermId is { } termId) command.Parameters.AddWithValue(termId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task AuditAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Entity entity, Guid id, Row row, string action, string actor, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("insert into glossary_audit (entity_type, entity_id, version, action, actor, data) values ($1, $2, $3, $4, $5, $6)", connection, tx);
        command.Parameters.AddWithValue(entity.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(row.Version);
        command.Parameters.AddWithValue(action);
        command.Parameters.AddWithValue(actor);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = row.Json });
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<(IReadOnlyList<T> Items, bool Truncated)> ListAsync<T>(NpgsqlConnection connection, NpgsqlTransaction tx, Entity entity, int limit, Func<Row, T> map, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand($"select data::text, version, deleted, {(entity == Entity.Term ? "null::uuid" : "term_id")} from {Table(entity)} where not deleted order by id limit $1", connection, tx);
        command.Parameters.AddWithValue(limit + 1);
        var items = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add(map(new(reader.GetString(0), reader.GetInt64(1), reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetGuid(3))));
        var truncated = items.Count > limit;
        if (truncated) items.RemoveAt(limit);
        return (items, truncated);
    }
}
