using CapabilityExplorer.Store;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CapabilityExplorer.Explorer;

/// <summary>HTTP front end for the six bounded task-query operations (<c>/api/tasks/v1/*</c>), over the same
/// <see cref="TaskQueryService"/> the MCP stdio front end (<see cref="TaskQueryMcpHost"/>) uses. Registration is
/// split in two, mirroring how <c>Program.cs</c> already wires <c>IExplorerQueries</c>/<c>ExplorerState</c>:
/// <see cref="AddTaskQueries"/> (mode-dependent DI, called once the caller already knows <c>source</c>) and
/// <see cref="MapTaskQueryApi"/> (mode-independent routing). Neither is called by this project itself; integrating
/// them into <c>Program.cs</c> is the parent's responsibility.</summary>
public static class TaskQueryApi
{
    /// <summary>Registers the one typed service over whichever evidence source <paramref name="source"/> selects
    /// (<c>files</c> or <c>postgres</c>, no silent fallback): assumes <c>ExplorerState</c> (files) or
    /// <c>IExplorerQueries</c>/<c>NpgsqlDataSource</c>/<c>DatabaseExplorerConfiguration</c> (postgres) are already
    /// registered, exactly as <c>Program.cs</c> already registers them for <see cref="ModelApi"/>.</summary>
    public static IServiceCollection AddTaskQueries(this IServiceCollection services, string source)
    {
        if (source is not ("files" or "postgres")) throw new ArgumentException("source must be files or postgres", nameof(source));
        services.AddSingleton<TaskQueryAccessPolicy>();
        services.AddSingleton<TaskQueryCursorCodec>();
        services.AddSingleton<TaskQueryService>();
        if (source == "files")
        {
            services.AddSingleton<ITaskEvidenceSource, FileTaskEvidenceSource>();
        }
        else
        {
            services.AddSingleton<ITaskEvidenceSource>(sp => new PostgresTaskEvidenceSource(
                    sp.GetRequiredService<IExplorerQueries>(), sp.GetRequiredService<NpgsqlDataSource>(),
                    sp.GetRequiredService<DatabaseExplorerConfiguration>())
                .WithGit(sp.GetRequiredService<DatabaseExplorerConfiguration>().Git));
        }

        return services;
    }

    public static void MapTaskQueryApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/tasks/v1");

        api.MapGet("/snapshot", (TaskQueryService service, string? cap, string? view, string? snapshot, CancellationToken ct) =>
            Respond(service, TaskQueryOperations.SnapshotResolve,
                token => service.SnapshotResolveAsync(new TaskQueryRequest(Cap: cap, View: view, SnapshotId: snapshot), token), ct));

        api.MapGet("/models", (TaskQueryService service, string? snapshot, string? generation, string? kind, string? limit, string? cursor, CancellationToken ct) =>
            Build(service, TaskQueryOperations.ModelsList, (gen, lim) =>
                new TaskQueryRequest(SnapshotId: snapshot, Generation: gen, Kind: kind, Limit: lim, Cursor: cursor), generation, limit, ct));

        api.MapGet("/models/decision-references", (TaskQueryService service, string? snapshot, string? generation, string? id, string? limit, string? cursor, CancellationToken ct) =>
            Build(service, TaskQueryOperations.DecisionReferences, (gen, lim) =>
                new TaskQueryRequest(SnapshotId: snapshot, Generation: gen, ModelId: id, Limit: lim, Cursor: cursor), generation, limit, ct));

        api.MapGet("/contracts/consumers", (TaskQueryService service, string? snapshot, string? generation, string? endpoint, string? limit, string? cursor, CancellationToken ct) =>
            Build(service, TaskQueryOperations.ContractConsumers, (gen, lim) =>
                new TaskQueryRequest(SnapshotId: snapshot, Generation: gen, Key: endpoint, Limit: lim, Cursor: cursor), generation, limit, ct));

        api.MapGet("/evidence", (TaskQueryService service, string? snapshot, string? generation, string? key, string? limit, string? cursor, CancellationToken ct) =>
            Build(service, TaskQueryOperations.EvidenceList, (gen, lim) =>
                new TaskQueryRequest(SnapshotId: snapshot, Generation: gen, Key: key, Limit: lim, Cursor: cursor), generation, limit, ct));

        api.MapGet("/source", (TaskQueryService service, string? snapshot, string? generation, string? repo, string? commit, string? path, string? start, string? end, CancellationToken ct) =>
            BuildSourceExcerpt(service, snapshot, generation, repo, commit, path, start, end, ct));
    }

    /// <summary>Parses the shared <c>generation</c>/<c>limit</c> raw string inputs before dispatch: ASP.NET's own
    /// non-nullable/numeric model binding (<c>long? generation</c>, <c>int? limit</c>) rejects a malformed value
    /// with a generic framework 400 BEFORE the handler ever runs, bypassing the ce.task-query envelope and the
    /// shared deadline entirely. Binding every query parameter as <c>string?</c> and parsing it here means a
    /// malformed HTTP request returns the exact same typed <c>invalid</c> envelope MCP would. Finding #5 fix.</summary>
    private static async Task<IResult> Build(TaskQueryService service, string operation,
        Func<long?, int?, TaskQueryRequest> build, string? generation, string? limit, CancellationToken ct)
    {
        if (!TryParseLong(generation, out var gen)) return Json(InvalidEnvelope(service, operation, "invalid-generation"));
        if (!TryParseInt(limit, out var lim)) return Json(InvalidEnvelope(service, operation, "invalid-limit"));
        var handler = (CancellationToken token) => DispatchHttp(service, operation, build(gen, lim), token);
        return await Respond(service, operation, handler, ct);
    }

    private static async Task<IResult> BuildSourceExcerpt(TaskQueryService service,
        string? snapshot, string? generation, string? repo, string? commit, string? path, string? start, string? end, CancellationToken ct)
    {
        if (!TryParseLong(generation, out var gen)) return Json(InvalidEnvelope(service, TaskQueryOperations.SourceExcerpt, "invalid-generation"));
        if (!TryParseInt(start, out var s)) return Json(InvalidEnvelope(service, TaskQueryOperations.SourceExcerpt, "invalid-start"));
        if (!TryParseInt(end, out var e)) return Json(InvalidEnvelope(service, TaskQueryOperations.SourceExcerpt, "invalid-end"));
        var request = new TaskQueryRequest(SnapshotId: snapshot, Generation: gen, Repo: repo, Commit: commit, Path: path, Start: s, End: e);
        return await Respond(service, TaskQueryOperations.SourceExcerpt, token => DispatchHttp(service, TaskQueryOperations.SourceExcerpt, request, token), ct);
    }

    private static bool TryParseLong(string? raw, out long? value)
    {
        value = null;
        if (string.IsNullOrEmpty(raw)) return true;
        if (!long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)) return false;
        value = parsed;
        return true;
    }

    private static bool TryParseInt(string? raw, out int? value)
    {
        value = null;
        if (string.IsNullOrEmpty(raw)) return true;
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)) return false;
        value = parsed;
        return true;
    }

    private static Task<TaskQueryEnvelope> DispatchHttp(TaskQueryService service, string operation, TaskQueryRequest request, CancellationToken token) =>
        operation switch
        {
            TaskQueryOperations.ModelsList => service.ModelsListAsync(request, token),
            TaskQueryOperations.DecisionReferences => service.DecisionReferencesAsync(request, token),
            TaskQueryOperations.ContractConsumers => service.ContractConsumersAsync(request, token),
            TaskQueryOperations.EvidenceList => service.EvidenceListAsync(request, token),
            TaskQueryOperations.SourceExcerpt => service.SourceExcerptAsync(request, token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private static TaskQueryEnvelope InvalidEnvelope(TaskQueryService service, string operation, string reason) =>
        new(operation, TaskQueryStatus.Invalid.Wire(), service.Mode, null, new TaskQueryCoverage([], "not-established"), null, null,
            [new TaskQueryDiagnostic(reason, $"invalid: {reason}")], reason);

    /// <summary>The one result shape every route returns: the envelope's own wire status drives the HTTP status
    /// code (<see cref="TaskQueryStatusCodes"/>), run under the same shared 10-second deadline MCP uses
    /// (<see cref="TaskQueryDeadline"/>). Caller cancellation during the handler propagates as an
    /// <see cref="OperationCanceledException"/> and is left to ASP.NET Core's own client-disconnect handling
    /// (no response body is ever written for it), never surfaced as a 504 envelope.</summary>
    private static async Task<IResult> Respond(TaskQueryService service, string operation,
        Func<CancellationToken, Task<TaskQueryEnvelope>> handler, CancellationToken ct)
    {
        var envelope = await TaskQueryDeadline.RunAsync(operation, service.Mode, handler, ct).ConfigureAwait(false);
        return Json(envelope);
    }

    private static BoundedJsonResult Json(TaskQueryEnvelope envelope) => new(
        envelope, TaskQueryStatusCodes.HttpStatus(envelope.Status), TaskQueryJson.MaxEnvelopeBytes,
        TaskQueryStatusCodes.HttpStatus(TaskQueryStatus.Limit.Wire()), TaskQueryJson.LimitEnvelope(envelope));
}
