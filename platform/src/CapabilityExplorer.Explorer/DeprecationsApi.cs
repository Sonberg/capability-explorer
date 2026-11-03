using System.Globalization;
using System.Text.Json;
using CapabilityExplorer.Store;
using Npgsql;

namespace CapabilityExplorer.Explorer;

public static class DeprecationsApi
{
    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web);

    public static void MapDeprecationsApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/deprecations", (Delegate)((HttpContext http) => Execute(http, async (marks, ct) =>
            Json(await marks.ListAsync(ct)))));
        app.MapPut("/api/deprecations", (Delegate)((HttpContext http) => Execute(http, async (marks, ct) =>
        {
            var request = await ReadRequest(http, ct);
            return Json(await marks.UpsertAsync(request.Key, request.Status, request.Note ?? "", request.ExpectedVersion, ct));
        })));
        app.MapDelete("/api/deprecations", (Delegate)((HttpContext http) => Execute(http, async (marks, ct) =>
        {
            var key = Query(http, "key", 16384);
            var token = Query(http, "expectedVersion", 20);
            if (!long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version <= 0)
                throw new ArgumentException("expectedVersion must be positive");
            await marks.DeleteAsync(key, version, ct);
            return Json(new { key, deleted = true });
        })));
        app.MapGet("/api/integrations", (Delegate)((HttpContext http) => Execute(http, async (marks, ct) =>
        {
            var queries = http.RequestServices.GetRequiredService<IExplorerQueries>();
            var config = http.RequestServices.GetRequiredService<DatabaseExplorerConfiguration>();
            using var definitions = config.OpenDefinitions();
            var scoped = await PostgresExplorerApi.ResolveContextAsync(http, queries, definitions, ct);
            if (scoped.Error is { } error) return error;
            var context = scoped.Context!;
            var global = await marks.ListAsync(ct);
            var removals = await http.RequestServices.GetRequiredService<PostgresDeprecationRemovals>().ReadAsync(context, ct);
            var graph = await queries.IntegrationsAsync(context, marks.WithRemovals(global, removals), ct);
            return Json(new { context.SnapshotId, context.Generation, selection = context.Selection,
                graph.Services, graph.Http, graph.Channels, graph.Data, graph.Marks });
        })));
    }

    private static async Task<IResult> Execute(HttpContext http, Func<DeprecationService, CancellationToken, Task<IResult>> operation)
    {
        var marks = http.RequestServices.GetService<DeprecationService>();
        if (marks is null) return Json(new { error = "deprecation marks and integrations require the database; configure CE_DATABASE and import a snapshot" }, 503);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try { return await operation(marks, deadline.Token); }
        catch (DeprecationVersionConflictException) { return Json(new { error = "mark changed; reload before retrying" }, 409); }
        catch (ArgumentException) { return Json(new { error = "invalid mark, status, version or snapshot selection" }, 400); }
        catch (JsonException) { return Json(new { error = "capability configuration is invalid" }, 503); }
        catch (NpgsqlException) { return Json(new { error = "database unavailable or query budget exceeded" }, 503); }
        catch (IOException) { return Json(new { error = "capability configuration is unavailable" }, 503); }
        catch (ExplorerProjectionUnavailableException) { return Json(new { error = "snapshot projection is not ready" }, 503); }
        catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
        { return Json(new { error = "request time budget exceeded" }, 504); }
        catch (InvalidOperationException) { return Json(new { error = "request, query or snapshot exceeds the supported budget" }, 413); }
    }

    private static async Task<MarkRequest> ReadRequest(HttpContext http, CancellationToken ct)
    {
        var bytes = new byte[16385];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await http.Request.Body.ReadAsync(bytes.AsMemory(length), ct);
            if (read == 0) break;
            length += read;
        }
        if (length == bytes.Length) throw new InvalidOperationException("mark request exceeds sixteen KiB");
        try
        {
            return JsonSerializer.Deserialize<MarkRequest>(bytes.AsSpan(0, length), RequestJson)
                ?? throw new ArgumentException("mark request is required");
        }
        catch (JsonException error) { throw new ArgumentException("invalid mark JSON", error); }
    }

    private static string Query(HttpContext http, string key, int max)
    {
        var values = http.Request.Query[key];
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]) || values[0]!.Length > max)
            throw new ArgumentException("invalid query field");
        return values[0]!;
    }
    private static BoundedJsonResult Json(object value, int status = 200) => new(value, status);
}
