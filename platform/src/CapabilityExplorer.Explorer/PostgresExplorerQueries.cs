using System.Globalization;
using System.Text.Json;
using Npgsql;
using CapabilityExplorer.Store;

namespace CapabilityExplorer.Explorer;

public sealed partial class PostgresExplorerQueries(NpgsqlDataSource dataSource, ExplorerQueryCursor cursors) : IExplorerQueries
{
    private const int TimeoutSeconds = 5;
    private const string Selected = """
        with selected as (
          select a.id,a.repository_id,a.commit_sha,a.unit_path,a.unit_name,a.language,a.status from analysis_snapshot_artifact sa join artifact a on a.id=sa.artifact_id
          join unnest(@repos::text[],@shas::text[]) selection(repo,sha)
            on selection.repo=a.repository_id and selection.sha=a.commit_sha::text
          where sa.snapshot_id=@snapshot
        )
        """;
    private const string Nodes = """
        , nodes as (
          select n.symbol_key,n.node_kind,n.name,n.generated,a.repository_id,a.commit_sha,a.unit_name,a.language,
            s.name_fold,s.symbol_fold,s.name_sort,s.local_id is null as projection_missing
          from selected a join node n on n.artifact_id=a.id
          left join node_search s on s.artifact_id=n.artifact_id and s.local_id=n.local_id and s.normalizer_id=@normalizer
          where not exists (
            select 1 from selected other join node n2 on n2.artifact_id=other.id
            where n2.symbol_key=n.symbol_key
              and (other.unit_path collate "C",other.id) < (a.unit_path collate "C",a.id)
          )
        )
        """;

    public async Task<ExplorerQueryContext?> ResolveAsync(string capability, IReadOnlyDictionary<string, string>? selection = null,
        Guid? snapshotId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        await using var command = Command(snapshotId is null
            ? "select s.id,s.generation from published_snapshot p join analysis_snapshot s on s.id=p.snapshot_id where p.capability_id=@cap"
            : "select id,generation from analysis_snapshot where id=@id and capability_id=@cap and status='published'");
        command.Parameters.AddWithValue("cap", capability);
        if (snapshotId is { } id) command.Parameters.AddWithValue("id", id);
        Guid snapshot; long generation;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await ReadProjectionAsync(reader, cancellationToken)) return null;
            snapshot = reader.GetGuid(0); generation = reader.GetInt64(1);
        }
        await using var revisions = Command($"""
            select {GuardedColumns("repository_id", "commit_sha::text")} from snapshot_revision r
            where snapshot_id=@id and is_default and exists(select 1 from analysis_snapshot_artifact sa
              where sa.snapshot_id=r.snapshot_id and sa.repository_id=r.repository_id and sa.commit_sha=r.commit_sha)
            order by repository_id collate "C" limit 1001
            """);
        revisions.Parameters.AddWithValue("id", snapshot);
        var chosen = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var reader = await revisions.ExecuteReaderAsync(cancellationToken))
            while (await ReadProjectionAsync(reader, cancellationToken)) chosen.Add(reader.GetString(0), reader.GetString(1));
        if (chosen.Count > 1000) throw new InvalidOperationException("repository scope exceeds supported limit");
        await using var validateDefaults = Command("select count(distinct repository_id) from analysis_snapshot_artifact where snapshot_id=@id");
        validateDefaults.Parameters.AddWithValue("id", snapshot);
        if ((long)(await validateDefaults.ExecuteScalarAsync(cancellationToken))! != chosen.Count)
            throw new InvalidOperationException("snapshot has no valid default revision for every repository");
        if (selection is not null)
        {
            if (selection.Count > 1000) throw new ArgumentException("too many revision selections", nameof(selection));
            foreach (var pair in selection)
            {
                if (!chosen.ContainsKey(pair.Key)) throw new ArgumentException("repository is outside snapshot", nameof(selection));
                await using var check = Command("select 1 from snapshot_revision where snapshot_id=@id and repository_id=@repo and commit_sha=@sha");
                check.Parameters.AddWithValue("id", snapshot); check.Parameters.AddWithValue("repo", pair.Key); check.Parameters.AddWithValue("sha", pair.Value);
                if (await check.ExecuteScalarAsync(cancellationToken) is null) throw new ArgumentException("revision is outside snapshot", nameof(selection));
                chosen[pair.Key] = pair.Value;
            }
        }
        var context = new ExplorerQueryContext(snapshot, capability, generation, chosen);
        await using var readiness = Scoped(context, Selected + """
            select exists(select 1 from selected a left join artifact_search_projection p
              on p.artifact_id=a.id and p.normalizer_id=@normalizer where p.artifact_id is null)
            """);
        if ((bool)(await readiness.ExecuteScalarAsync(cancellationToken))!)
            throw new ExplorerProjectionUnavailableException("snapshot search projection is not ready; run the bounded normalization backfill");
        return context;
    }

    public async Task<ExplorerDatabaseMetadata> MetadataAsync(ExplorerQueryContext context, CancellationToken cancellationToken = default)
    {
        await using var command = Command("select status from analysis_snapshot where id=@snapshot and capability_id=@cap and status='published'");
        command.Parameters.AddWithValue("snapshot", context.SnapshotId); command.Parameters.AddWithValue("cap", context.Capability);
        var status = await command.ExecuteScalarAsync(cancellationToken) as string ?? throw new InvalidOperationException("snapshot is unavailable");
        return new(context.SnapshotId, context.Generation, context.Capability, status);
    }

    public async Task<ExplorerPage<ExplorerRevisionRow>> RevisionsAsync(ExplorerQueryContext context, ExplorerPageRequest page, CancellationToken cancellationToken = default)
    {
        var after = cursors.Decode(context, "revisions", page.Cursor);
        await using var command = Command($"""
            select {GuardedColumns("repository_id", "commit_sha::text")} from snapshot_revision where snapshot_id=@snapshot
            and (@first or (repository_id collate "C",commit_sha collate "C") > (@repo collate "C",@sha collate "C"))
            order by repository_id collate "C",commit_sha collate "C" limit @limit
            """);
        command.Parameters.AddWithValue("snapshot", context.SnapshotId); command.Parameters.AddWithValue("first", after is null);
        command.Parameters.AddWithValue("repo", after?[0] ?? ""); command.Parameters.AddWithValue("sha", after?[1] ?? "");
        command.Parameters.AddWithValue("limit", page.BoundedLimit + 1);
        var rows = new List<ExplorerRevisionRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await ReadProjectionAsync(reader, cancellationToken)) rows.Add(new(reader.GetString(0), reader.GetString(1)));
        return Page(context, page, "revisions", rows, r => [r.RepositoryId, r.CommitSha]);
    }

    public async Task<ExplorerPage<ExplorerArtifactRow>> ArtifactsAsync(ExplorerQueryContext context, ExplorerPageRequest page, CancellationToken cancellationToken = default)
    {
        var after = cursors.Decode(context, "artifacts", page.Cursor);
        await using var command = Scoped(context, Selected + $"select {GuardedColumns("id", "repository_id", "commit_sha::text", "unit_name", "unit_path", "language", "status")} from selected where id>@after order by id limit @limit");
        command.Parameters.AddWithValue("after", after is null ? 0L : long.Parse(after[0], CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("limit", page.BoundedLimit + 1);
        var rows = new List<ExplorerArtifactRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await ReadProjectionAsync(reader, cancellationToken)) rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6)));
        return Page(context, page, "artifacts", rows, r => [r.Id.ToString(CultureInfo.InvariantCulture)]);
    }

    public async Task<ExplorerPage<SearchHit>> SearchAsync(ExplorerQueryContext context, string? query, string? kind, string? repository,
        ExplorerPageRequest page, CancellationToken cancellationToken = default)
    {
        var q = (query ?? "").Trim();
        if (q.Length > 500) throw new ArgumentException("search query exceeds 500 characters", nameof(query));
        var filter = JsonSerializer.Serialize(new { endpoint = "search.v2", normalizer = NodeSearchNormalization.NormalizerId, q, kind, repository });
        var after = cursors.Decode(context, filter, page.Cursor);
        await using var command = Scoped(context, Selected + Nodes + $"""
            , ranked as (
              select *,case when projection_missing then 5 when @q='' then 1 when name_fold collate "C"=@q collate "C" then 4
              when starts_with(name_fold collate "C",@q collate "C") then 3 when strpos(name_fold collate "C",@q collate "C")>0 then 2
              when strpos(symbol_fold collate "C",@q collate "C")>0 then 1 else 0 end rank
              from nodes where (@kind='' or node_kind=@kind) and (@repo='' or repository_id=@repo)
            )
            select {GuardedColumns("case when projection_missing then null else symbol_key end", "name", "node_kind", "repository_id", "unit_name", "generated", "exists(select 1 from selected a join calculation c on c.artifact_id=a.id where c.symbol_key=r.symbol_key)", "rank", "name_sort")},projection_missing
            from ranked r where rank>0 and (@first or rank<@rank or
              (rank=@rank and (name_sort,symbol_key collate "C") > (@nameSort,@key collate "C")))
            order by rank desc,name_sort,symbol_key collate "C" limit @limit
            """);
        command.Parameters.AddWithValue("q", NodeSearchNormalization.Fold(q)); command.Parameters.AddWithValue("kind", kind ?? ""); command.Parameters.AddWithValue("repo", repository ?? "");
        command.Parameters.AddWithValue("first", after is null); command.Parameters.AddWithValue("rank", after is null ? 0 : int.Parse(after[0], CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("nameSort", after is null ? Array.Empty<byte>() : Convert.FromBase64String(after[1])); command.Parameters.AddWithValue("key", after?[2] ?? ""); command.Parameters.AddWithValue("limit", page.BoundedLimit + 1);
        var rows = new List<SearchHit>();
        var positions = new Dictionary<string, string[]>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await ReadProjectionAsync(reader, 9, cancellationToken))
        {
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5), reader.GetBoolean(6)));
            positions[reader.GetString(0)] = [reader.GetInt32(7).ToString(CultureInfo.InvariantCulture), Convert.ToBase64String(reader.GetFieldValue<byte[]>(8)), reader.GetString(0)];
        }
        return Page(context, page, filter, rows, r => positions[r.Key]);
    }

    public async Task<ExplorerNodeRow?> SymbolAsync(ExplorerQueryContext context, string key, CancellationToken cancellationToken = default)
    {
        await using var command = Scoped(context, Selected + $"""
            select {GuardedColumns("n.symbol_key", "n.node_kind", "n.name", "n.signature", "n.generated", "a.repository_id", "a.commit_sha::text", "a.unit_name", "a.id")}
            from selected a join node n on n.artifact_id=a.id where n.symbol_key=@key
            order by a.unit_path collate "C",a.id limit 1
            """);
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await ReadProjectionAsync(reader, cancellationToken) ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetInt64(8)) : null;
    }

    public async Task<ExplorerPage<ExplorerEdgeRow>> EdgesAsync(ExplorerQueryContext context, string key, string direction,
        ExplorerPageRequest page, CancellationToken cancellationToken = default)
    {
        if (direction is not ("incoming" or "outgoing")) throw new ArgumentException("direction must be incoming or outgoing", nameof(direction));
        var filter = JsonSerializer.Serialize(new { endpoint = "edges", key, direction });
        var after = cursors.Decode(context, filter, page.Cursor);
        var column = direction == "incoming" ? "to_key" : "from_key";
        await using var command = Scoped(context, Selected + $"""
            select {GuardedColumns("e.artifact_id", "e.local_id", "e.from_key", "e.relation", "e.to_key", "e.derivation", "e.resolution", "e.unresolved_reason", "a.repository_id", "a.commit_sha::text")}
            from selected a join edge e on e.artifact_id=a.id where e.{column}=@key
            and (@first or (e.artifact_id,e.local_id collate "C") > (@artifact,@local collate "C"))
            order by e.artifact_id,e.local_id collate "C" limit @limit
            """);
        command.Parameters.AddWithValue("key", key); AddPosition(command, after, page);
        var rows = new List<ExplorerEdgeRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await ReadProjectionAsync(reader, cancellationToken)) rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8), reader.GetString(9)));
        return Page(context, page, filter, rows, r => [r.ArtifactId.ToString(CultureInfo.InvariantCulture), r.LocalId]);
    }

    // SQL suppresses every projected field before values cross the database connection.
    // NULL in the mandatory first column signals an explicit size error, never a skipped row.
    private static string GuardedColumns(params string[] columns)
    {
        var bytes = string.Join("+", columns.Select(column => $"coalesce(octet_length(({column})::text),0)"));
        return string.Join(",", columns.Select(column => $"case when ({bytes})<=8192 then {column} else null end"));
    }
    private static Task<bool> ReadProjectionAsync(NpgsqlDataReader reader, CancellationToken cancellationToken) => ReadProjectionAsync(reader, -1, cancellationToken);
    private static async Task<bool> ReadProjectionAsync(NpgsqlDataReader reader, int missingProjectionColumn, CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken)) return false;
        if (missingProjectionColumn >= 0 && reader.GetBoolean(missingProjectionColumn))
            throw new ExplorerProjectionUnavailableException("selected node search projection is missing");
        if (reader.IsDBNull(0)) throw new InvalidOperationException("projection exceeds eight KiB response limit");
        return true;
    }

    private NpgsqlCommand Command(string sql) { var command = dataSource.CreateCommand(sql); command.CommandTimeout = TimeoutSeconds; return command; }
    private NpgsqlCommand Scoped(ExplorerQueryContext context, string sql)
    {
        var command = Command(sql);
        command.Parameters.AddWithValue("snapshot", context.SnapshotId);
        if (sql.Contains("@normalizer", StringComparison.Ordinal)) command.Parameters.AddWithValue("normalizer", NodeSearchNormalization.NormalizerId);
        command.Parameters.AddWithValue("repos", context.Selection.Keys.ToArray());
        command.Parameters.AddWithValue("shas", context.Selection.Values.ToArray());
        return command;
    }
    private ExplorerPage<T> Page<T>(ExplorerQueryContext context, ExplorerPageRequest request, string filter, List<T> rows, Func<T, string[]> position)
    {
        var more = rows.Count > request.BoundedLimit;
        if (more) rows.RemoveAt(rows.Count - 1);
        return new(rows, more ? cursors.Encode(context, filter, position(rows[^1])) : null, context.SnapshotId);
    }
    private static void AddPosition(NpgsqlCommand command, string[]? after, ExplorerPageRequest page)
    {
        command.Parameters.AddWithValue("first", after is null);
        command.Parameters.AddWithValue("artifact", after is null ? 0L : long.Parse(after[0], CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("local", after?[1] ?? ""); command.Parameters.AddWithValue("limit", page.BoundedLimit + 1);
    }
}
