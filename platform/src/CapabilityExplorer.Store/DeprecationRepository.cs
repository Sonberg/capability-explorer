using System.Globalization;
using System.Text.Json;
using Npgsql;

namespace CapabilityExplorer.Store;

public sealed class PostgresDeprecationRepository(NpgsqlDataSource dataSource) : IDeprecationRepository
{
    private const int MaxMarks = 10_000;
    private const int MaxImportBytes = 8 * 1024 * 1024;
    private const int MaxImportDepth = 8;

    public async Task<IReadOnlyList<DeprecationMark>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("""
            select mark_key,status,note,marked_at,version,marked_at_tick
            from deprecation_mark
            order by mark_key collate "C"
            limit $1
            """);
        command.Parameters.AddWithValue(MaxMarks + 1);
        var marks = new List<DeprecationMark>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (marks.Count == MaxMarks)
                throw new InvalidOperationException($"Deprecation mark count exceeds the supported limit of {MaxMarks}.");
            marks.Add(ReadMark(reader));
        }

        return marks;
    }

    public async Task<DeprecationMark> UpsertAsync(DeprecationUpsert value, CancellationToken cancellationToken = default)
    {
        DeprecationValidation.Upsert(value);
        var sql = value.ExpectedVersion is null
            ? """
              insert into deprecation_mark(mark_key,status,note,marked_at)
              values($1,$2,$3,now())
              on conflict(mark_key) do nothing
              returning mark_key,status,note,marked_at,version,marked_at_tick
              """
            : """
              update deprecation_mark
              set status=$2,note=$3,marked_at=now(),marked_at_tick=0,version=nextval('deprecation_mark_version')
              where mark_key=$1 and version=$4
              returning mark_key,status,note,marked_at,version,marked_at_tick
              """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(value.Key);
        command.Parameters.AddWithValue(DeprecationStatusValues.ToDatabaseValue(value.Status));
        command.Parameters.AddWithValue(value.Note);
        if (value.ExpectedVersion is { } expected)
            command.Parameters.AddWithValue(expected);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
            return ReadMark(reader);
        throw new DeprecationVersionConflictException(value.Key, value.ExpectedVersion);
    }

    public async Task DeleteAsync(string key, long expectedVersion, CancellationToken cancellationToken = default)
    {
        DeprecationValidation.Key(key);
        if (expectedVersion <= 0)
            throw new DeprecationValidationException("Expected version must be positive.");

        await using var command = dataSource.CreateCommand(
            "delete from deprecation_mark where mark_key=$1 and version=$2 returning 1");
        command.Parameters.AddWithValue(key);
        command.Parameters.AddWithValue(expectedVersion);
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
            throw new DeprecationVersionConflictException(key, expectedVersion);
    }

    public async Task<DeprecationImportReport> ImportLegacyAsync(Stream json, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (!json.CanRead)
            throw new DeprecationValidationException("Legacy JSON stream must be readable.");
        using var document = await ReadLegacyDocumentAsync(json, cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new DeprecationValidationException("Legacy deprecations must be a JSON array.");
        if (document.RootElement.GetArrayLength() > MaxMarks)
            throw new DeprecationValidationException($"Legacy JSON exceeds the {MaxMarks}-entry import limit.");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var issues = new List<DeprecationImportIssue>();
        var seen = new Dictionary<string, LegacyMark>(StringComparer.Ordinal);
        var imported = 0;
        var unchanged = 0;
        var entryNumber = 0;
        foreach (var element in document.RootElement.EnumerateArray())
        {
            entryNumber++;
            if (!TryReadLegacy(element, entryNumber, issues, out var value))
                continue;

            var mark = value!;
            if (seen.TryGetValue(mark.Key, out var earlier))
            {
                if (Equivalent(mark, earlier))
                    unchanged++;
                else
                    issues.Add(new(entryNumber, mark.Key, "Conflicts with an earlier entry for this key."));
                continue;
            }
            seen.Add(mark.Key, mark);

            await using var insert = new NpgsqlCommand("""
                insert into deprecation_mark(mark_key,status,note,marked_at,marked_at_tick)
                values($1,$2,$3,$4::timestamptz,$5)
                on conflict(mark_key) do nothing
                """, connection, transaction);
            insert.Parameters.AddWithValue(mark.Key);
            insert.Parameters.AddWithValue(DeprecationStatusValues.ToDatabaseValue(mark.Status));
            insert.Parameters.AddWithValue(mark.Note);
            // Text avoids Npgsql's implicit MinValue/MaxValue conversion to PostgreSQL infinities.
            insert.Parameters.AddWithValue(mark.MarkedAt.ToUniversalTime()
                .ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue((short)(mark.MarkedAt.Ticks % 10));
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                imported++;
                continue;
            }

            var current = await GetAsync(connection, transaction, mark.Key, cancellationToken);
            if (current is not null && Equivalent(mark, current))
                unchanged++;
            else
                issues.Add(new(entryNumber, mark.Key, "Conflicts with an existing database mark; existing value was not changed."));
        }

        await transaction.CommitAsync(cancellationToken);
        return new(imported, unchanged, issues);
    }

    private static async Task<JsonDocument> ReadLegacyDocumentAsync(Stream json, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await json.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                break;
            if (buffer.Length + read > MaxImportBytes)
                throw new DeprecationValidationException($"Legacy JSON exceeds the {MaxImportBytes}-byte import limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        return JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = MaxImportDepth });
    }

    private static async Task<DeprecationMark?> GetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            select mark_key,status,note,marked_at,version,marked_at_tick
            from deprecation_mark where mark_key=$1
            """, connection, transaction);
        command.Parameters.AddWithValue(key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMark(reader) : null;
    }

    private static bool TryReadLegacy(
        JsonElement element,
        int entry,
        List<DeprecationImportIssue> issues,
        out LegacyMark? mark)
    {
        mark = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new(entry, null, "Entry must be an object."));
            return false;
        }
        if (element.EnumerateObject().Select(property => property.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != element.EnumerateObject().Count())
        {
            issues.Add(new(entry, null, "Entry contains duplicate property names."));
            return false;
        }

        var key = StringProperty(element, "key");
        var statusText = StringProperty(element, "status");
        var note = StringProperty(element, "note");
        var markedAtValue = Property(element, "markedAt");
        if (key is null || string.IsNullOrWhiteSpace(key) || key.Length > 4096 || key.Contains('\0', StringComparison.Ordinal))
        {
            issues.Add(new(entry, key, "Key must contain 1 to 4096 non-whitespace characters."));
            return false;
        }
        if (!DeprecationStatusValues.TryParse(statusText, out var status))
        {
            issues.Add(new(entry, key, "Status must be 'deprecated' or 'obsolete'."));
            return false;
        }
        if (note is null || note.Length > 4096 || note.Contains('\0', StringComparison.Ordinal))
        {
            issues.Add(new(entry, key, "Note must be present and no longer than 4096 characters."));
            return false;
        }
        if (markedAtValue is null || markedAtValue.Value.ValueKind != JsonValueKind.String
            || !markedAtValue.Value.TryGetDateTimeOffset(out var markedAt))
        {
            issues.Add(new(entry, key, "markedAt must be a valid timestamp."));
            return false;
        }

        mark = new(key, status, note, markedAt);
        return true;
    }

    private static JsonElement? Property(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }

        return null;
    }

    private static string? StringProperty(JsonElement element, string name)
    {
        var property = Property(element, name);
        return property is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    }

    private static bool Equivalent(LegacyMark left, LegacyMark right) =>
        left.Status == right.Status && left.Note == right.Note && left.MarkedAt == right.MarkedAt;

    private static bool Equivalent(LegacyMark legacy, DeprecationMark current) =>
        legacy.Status == current.Status && legacy.Note == current.Note && legacy.MarkedAt == current.MarkedAt;

    private static DeprecationMark ReadMark(NpgsqlDataReader reader) =>
        new(reader.GetString(0), DeprecationStatusValues.FromDatabaseValue(reader.GetString(1)),
            reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3).AddTicks(reader.GetInt16(5)), reader.GetInt64(4));

    private sealed record LegacyMark(string Key, DeprecationStatus Status, string Note, DateTimeOffset MarkedAt);
}
