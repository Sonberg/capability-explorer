using System.Globalization;
using System.Text.Json;
using Npgsql;

namespace CapabilityExplorer.Explorer;

public sealed partial class PostgresExplorerQueries
{
    private static readonly string[] CalculationFields = ["c.artifact_id", "c.local_id", "c.symbol_key", "coalesce(n.name,c.symbol_key)", "a.repository_id", "a.commit_sha::text", "a.unit_name", "a.language", "c.ir_hash::text"];
    public async Task<ExplorerPage<ExplorerCalculationRow>> CalculationsAsync(ExplorerQueryContext context, ExplorerPageRequest page,
        CancellationToken cancellationToken = default)
    {
        var after = cursors.Decode(context, "calculations", page.Cursor);
        await using var command = Scoped(context, Selected + $"""
            select {GuardedColumns(CalculationFields)} from selected a join calculation c on c.artifact_id=a.id
            left join node n on n.artifact_id=c.artifact_id and n.symbol_key=c.symbol_key
            where (@first or (c.artifact_id,c.local_id collate "C") > (@artifact,@local collate "C"))
            order by c.artifact_id,c.local_id collate "C" limit @limit
            """);
        AddPosition(command, after, page);
        var rows = new List<ExplorerCalculationRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await ReadProjectionAsync(reader, cancellationToken)) rows.Add(CalculationRow(reader));
        return Page(context, page, "calculations", rows, r => [r.ArtifactId.ToString(CultureInfo.InvariantCulture), r.LocalId]);
    }

    public async Task<ExplorerCalculationDetail?> CalculationAsync(ExplorerQueryContext context, string key, CancellationToken cancellationToken = default)
    {
        await using var command = Scoped(context, Selected + $"""
            select {GuardedColumns(CalculationFields)},case when octet_length(c.body::text)<=1048576 then c.body::text else null end
            from selected a join calculation c on c.artifact_id=a.id
            left join node n on n.artifact_id=c.artifact_id and n.symbol_key=c.symbol_key
            where c.symbol_key=@key order by a.unit_path collate "C",c.artifact_id,c.local_id collate "C" limit 1
            """);
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await ReadProjectionAsync(reader, cancellationToken)) return null;
        if (reader.IsDBNull(9)) throw new InvalidOperationException("calculation exceeds one MiB response limit");
        using var body = JsonDocument.Parse(reader.GetString(9));
        return new(CalculationRow(reader), body.RootElement.Clone());
    }

    public async Task<ExplorerPage<ExplorerEvidenceRow>> EvidenceAsync(ExplorerQueryContext context, string key, ExplorerPageRequest page,
        CancellationToken cancellationToken = default)
    {
        var filter = JsonSerializer.Serialize(new { endpoint = "evidence", key });
        var after = cursors.Decode(context, filter, page.Cursor);
        await using var command = Scoped(context, Selected + $"""
            select {GuardedColumns("e.artifact_id", "e.local_id", "e.repository_id", "e.commit_sha::text", "e.path", "e.start_line", "e.start_column", "e.end_line", "e.end_column")}
            from selected a join node n on n.artifact_id=a.id join evidence e on e.artifact_id=n.artifact_id and e.local_id=any(n.evidence)
            where n.symbol_key=@key and (@first or (e.artifact_id,e.local_id collate "C") > (@artifact,@local collate "C"))
            order by e.artifact_id,e.local_id collate "C" limit @limit
            """);
        command.Parameters.AddWithValue("key", key); AddPosition(command, after, page);
        var rows = new List<(long Artifact, ExplorerEvidenceRow Row)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await ReadProjectionAsync(reader, cancellationToken)) rows.Add((reader.GetInt64(0), new(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8))));
        var result = Page(context, page, filter, rows, r => [r.Artifact.ToString(CultureInfo.InvariantCulture), r.Row.Id]);
        return new(result.Items.Select(r => r.Row).ToList(), result.NextCursor, context.SnapshotId);
    }

    public async Task<bool> IsCitedAsync(ExplorerQueryContext context, string repository, string commit, string path,
        CancellationToken cancellationToken = default)
    {
        await using var command = Scoped(context, Selected + """
            select exists(select 1 from selected a join evidence e on e.artifact_id=a.id
            where e.repository_id=@repo and e.commit_sha=@sha and e.path=@path)
            """);
        command.Parameters.AddWithValue("repo", repository); command.Parameters.AddWithValue("sha", commit); command.Parameters.AddWithValue("path", path);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static ExplorerCalculationRow CalculationRow(NpgsqlDataReader reader) => new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
        reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8));
}
