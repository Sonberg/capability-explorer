using System.Text.Json;
using CapabilityExplorer.Explorer.LandscapeAnalysis;

namespace CapabilityExplorer.Explorer;

internal static class LandscapeAnalysisApi
{
    internal static void MapLandscapeAnalysis(this IEndpointRouteBuilder api)
    {
        api.MapGet("/landscape-analysis", (ExplorerOptions options) => Respond(() =>
            new LandscapeAnalysisStore(Path.GetDirectoryName(Path.GetFullPath(options.CapabilitiesFile))!).Read() is { } snapshot
                ? Results.Ok(snapshot)
                : Results.NotFound(new { code = "no-snapshot" })));
        api.MapPost("/landscape-analysis", (ExplorerOptions options, ExplorerState state,
            LandscapeAnalysisRequest? request, HttpRequest httpRequest) => Analyze(options, state, request, httpRequest));
    }

    private static IResult Analyze(
        ExplorerOptions options,
        ExplorerState state,
        LandscapeAnalysisRequest? request,
        HttpRequest httpRequest)
    {
        if (!LocalRequestGuard.Allowed(httpRequest)
            || !httpRequest.HasJsonContentType()
            || httpRequest.Headers["X-CE-Landscape-Analysis"] != "1")
        {
            return Results.Json(new { error = "Landscape analysis requires a trusted local same-origin JSON request." }, statusCode: 403);
        }

        try
        {
            state.Refresh();
            var current = state.Current;
            var result = LandscapeAnalysisEngine.Build(
                current.Index,
                current.Capabilities.List(),
                options.CapabilitiesFile,
                request);
            if (result.Error is { } error)
            {
                var status = error.Code == "inputs-changed" ? StatusCodes.Status409Conflict : StatusCodes.Status422UnprocessableEntity;
                return Results.Json(error, statusCode: status);
            }

            var snapshot = result.Snapshot ?? throw new InvalidOperationException("Landscape analysis returned neither a snapshot nor an error.");
            new LandscapeAnalysisStore(Path.GetDirectoryName(Path.GetFullPath(options.CapabilitiesFile))!).Write(snapshot);
            return Results.Ok(snapshot);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            return Results.Json(new { code = "analysis-unavailable", error = "Landscape analysis failed; the prior snapshot was preserved." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static IResult Respond(Func<IResult> action)
    {
        try { return action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            return Results.Json(new { code = "snapshot-unavailable", error = "Landscape snapshot is unavailable or invalid." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
