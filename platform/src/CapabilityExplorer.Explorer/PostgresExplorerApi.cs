using System.Globalization;
using CapabilityExplorer.Capabilities;
using Npgsql;

namespace CapabilityExplorer.Explorer;

/// <summary>Opt-in bounded database API. It never falls back to file indexes.</summary>
public static class PostgresExplorerApi
{
    private static readonly string[] Supported = ["meta", "generation", "view", "revisions", "search", "symbol", "symbol/edges", "symbol/evidence", "artifacts", "calculations", "calculation", "source", "integrations", "deprecations", "embeddings/status"];

    public static void MapPostgresExplorerApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/{**path}", async (string? path, HttpContext http, IExplorerQueries queries, DatabaseExplorerConfiguration config) =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var cancellationToken = deadline.Token;
            try
            {
                if (!Supported.Contains(path, StringComparer.Ordinal))
                    return Json(new { error = "this view is not yet migrated to PostgreSQL", source = "postgres", status = "pending", supported = Supported }, 501);
                using var definitions = config.OpenDefinitions();
                var scoped = await ResolveContextAsync(http, queries, definitions, cancellationToken);
                if (scoped.Error is { } error) return error;
                var context = scoped.Context!;
                var cursor = Text(http, "cursor", 16384);
                var page = Page(http, cursor);
                switch (path)
                {
                    case "meta": return Json(new
                    {
                        source = "postgres", status = "ready", context.SnapshotId, context.Generation, context.Capability,
                        selection = context.Selection, supported = Supported,
                        capabilities = definitions.Capabilities.Select(c => new { c.Id, c.Name, repositories = c.Repositories.Select(r => r.Id).ToArray() }).ToArray(),
                    });
                    case "generation": return Json(new { context.Generation, context.SnapshotId });
                    case "embeddings/status": return Json(await EmbeddingStatus.Read(context,
                        http.RequestServices.GetRequiredService<Intelligence>(),
                        http.RequestServices.GetRequiredService<NpgsqlDataSource>(),
                        Supported.Contains("similar", StringComparer.Ordinal), cancellationToken));
                    case "view": return Json(context.Selection);
                    case "revisions": return Json(await queries.RevisionsAsync(context, page, cancellationToken));
                    case "artifacts": return Json(await queries.ArtifactsAsync(context, page, cancellationToken));
                    case "search": return Json(await queries.SearchAsync(context, Text(http, "q", 500), Text(http, "kind", 100), Text(http, "repo", 100), page, cancellationToken));
                    case "symbol":
                        var node = await queries.SymbolAsync(context, Required(http, "key", 16384), cancellationToken);
                        return node is null ? Json(new { error = "symbol is not declared in this selection" }, 404) : Json(node);
                    case "symbol/edges": return Json(await queries.EdgesAsync(context, Required(http, "key", 16384), Text(http, "direction", 10) ?? "outgoing", page, cancellationToken));
                    case "symbol/evidence": return Json(await queries.EvidenceAsync(context, Required(http, "key", 16384), page, cancellationToken));
                    case "calculations": return Json(await queries.CalculationsAsync(context, page, cancellationToken));
                    case "calculation":
                        var calculation = await queries.CalculationAsync(context, Required(http, "key", 16384), cancellationToken);
                        return calculation is null ? Json(new { error = "calculation not found" }, 404) : Json(calculation);
                    case "source": return await Source(http, queries, config, context, cancellationToken);
                    default: return Json(new { error = "not found" }, 404);
                }
            }
            catch (ArgumentException) { return Json(new { error = "invalid query, selection or continuation cursor; restart pagination if necessary" }, 400); }
            catch (CapabilityExplorer.Store.SnapshotConflictException) { return Json(new { error = "snapshot is unavailable; restart pagination", source = "postgres" }, 409); }
            catch (CapabilityExplorer.Interpretation.EmbeddingConfigurationException) { return Json(new { error = "embedding configuration is invalid", reasonCode = "invalid-model-configuration" }, 503); }
            catch (System.Text.Json.JsonException) { return Json(new { error = "capability configuration is invalid" }, 503); }
            catch (IOException) { return Json(new { error = "capability configuration or source is temporarily unavailable" }, 503); }
            catch (NpgsqlException) { return Json(new { error = "database unavailable or query budget exceeded" }, 503); }
            catch (ExplorerProjectionUnavailableException) { return Json(new { error = "search projection is not ready; complete the trusted database migration before browsing" }, 503); }
            catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested) { return Json(new { error = "request time budget exceeded" }, 504); }
            catch (InvalidOperationException) { return Json(new { error = "query or snapshot exceeds the supported budget" }, 413); }
        });
        app.MapMethods("/api/{**path}", ["POST", "PUT", "DELETE", "PATCH"], () => Json(new { error = "write workflows are not yet migrated to PostgreSQL", status = "pending" }, 501));
    }

    internal static async Task<(ExplorerQueryContext? Context, IResult? Error)> ResolveContextAsync(HttpContext http,
        IExplorerQueries queries, CapabilityStore definitions, CancellationToken ct)
    {
        var cap = Text(http, "cap", 100) ?? "*";
        if (cap != "*" && definitions.Find(cap) is null) return (null, Json(new { error = "unknown capability" }, 400));
        var cursor = Text(http, "cursor", 16384);
        var snapshotText = Text(http, "snapshot", 36);
        Guid? snapshot = snapshotText is null ? null : Guid.TryParse(snapshotText, out var id) ? id : throw new ArgumentException("invalid snapshot");
        if (cursor is not null && snapshot is null) throw new ArgumentException("continuation requires its snapshot");
        var context = await queries.ResolveAsync(cap, Selection(Text(http, "view", 16384)), snapshot, ct);
        if (context is null) return (null, Json(new { error = snapshot is null ? "no published analysis; import and publish a snapshot first" : "snapshot is unavailable; restart pagination", source = "postgres" }, snapshot is null ? 503 : 409));
        if (cap != "*" && definitions.Find(cap) is { } definition
            && context.Selection.Keys.Any(repo => !definition.Repositories.Any(r => r.Id == repo)))
            return (null, Json(new { error = "snapshot is outside the current capability scope" }, 403));
        return (context, null);
    }

    private static async Task<IResult> Source(HttpContext http, IExplorerQueries queries, DatabaseExplorerConfiguration config, ExplorerQueryContext context, CancellationToken ct)
    {
        var repo = Required(http, "repo", 100); var commit = Required(http, "commit", 40); var path = Required(http, "path", 4096);
        if (!await queries.IsCitedAsync(context, repo, commit, path, ct)) return Json(new { error = "only cited files in this selection may be shown" }, 403);
        var text = await config.Git.ReadFileAsync(repo, commit, path, ct);
        if (text is null) return Json(new { error = "source is unavailable at this revision" }, 404);
        var lines = text.Split('\n');
        var start = Number(http, "start", 1); var end = Number(http, "end", start);
        if (start < 1 || end < start || end - start > 200) throw new ArgumentException("invalid source span");
        var from = Math.Max(1, start - 4); var to = Math.Min(lines.Length, end + 4);
        if (from > to) throw new ArgumentException("source span outside file");
        return Json(new { repo, commit, path, highlightStart = start, highlightEnd = end, totalLines = lines.Length,
            lines = Enumerable.Range(from, to - from + 1).Select(n => new { n, text = lines[n - 1] }) });
    }

    private static Dictionary<string, string> Selection(string? spec)
    {
        var selection = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in (spec ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = part.IndexOf(':', StringComparison.Ordinal);
            if (split <= 0 || selection.Count >= 100 || !selection.TryAdd(part[..split], part[(split + 1)..])) throw new ArgumentException("invalid revision selection");
            var sha = part[(split + 1)..];
            if (sha.Length != 40 || !sha.All(char.IsAsciiHexDigit)) throw new ArgumentException("full revision SHA required");
        }
        return selection;
    }
    private static ExplorerPageRequest Page(HttpContext http, string? cursor)
    {
        var limit = Number(http, "limit", 50);
        if (limit is < 1 or > 100) throw new ArgumentException("page limit must be 1 to 100");
        return new(limit, cursor);
    }
    private static int Number(HttpContext http, string key, int fallback) => Text(http, key, 10) is { } value
        ? int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : throw new ArgumentException("invalid number") : fallback;
    private static string Required(HttpContext http, string key, int max) => Text(http, key, max) ?? throw new ArgumentException("missing query field");
    private static string? Text(HttpContext http, string key, int max)
    {
        var values = http.Request.Query[key];
        if (values.Count > 1) throw new ArgumentException("duplicate query field");
        var value = values.ToString();
        if (value.Length > max) throw new ArgumentException("query field too long");
        return value.Length == 0 ? null : value;
    }
    private static BoundedJsonResult Json(object value, int status = 200) => new(value, status);
}
