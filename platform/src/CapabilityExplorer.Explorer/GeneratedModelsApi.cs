using System.Text.Json;
using CapabilityExplorer.Explorer.GeneratedModels;
using Npgsql;

namespace CapabilityExplorer.Explorer;

public static class GeneratedModelsApi
{
    public static void MapGeneratedModelsApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/generated-models");
        api.MapGet("/settings", (GeneratedModelAnalysisService service) => Results.Ok(service.AuthorizationStatus()));
        api.MapGet("/status", GetStatus);
        api.MapGet("", GetLatestAsync);
        api.MapPost("", RunAsync);
        api.MapDelete("/runs/{runId}", (string runId, GeneratedModelAnalysisService service) =>
            service.Cancel(runId)
                ? Results.Accepted($"/api/generated-models/status", new { runId, status = "cancelling" })
                : Results.NotFound(new { code = "run-not-active" }));
    }

    private static IResult GetStatus(GeneratedModelAnalysisService service)
    {
        try { return Results.Ok(service.Status().LatestRun); }
        catch (GeneratedModelRequestException error)
        {
            return Results.Json(new { code = error.Code, message = error.Message }, statusCode: error.StatusCode);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or NpgsqlException)
        {
            return Results.Json(new { code = "status-unavailable", message = "Generated model status is unavailable or invalid." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> GetLatestAsync(
        GeneratedModelAnalysisService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var latest = await service.LatestAsync(cancellationToken);
            return latest is null ? Results.NotFound(new { code = "no-successful-result" }) : Results.Ok(latest);
        }
        catch (GeneratedModelRequestException error)
        {
            return Results.Json(new { code = error.Code, message = error.Message }, statusCode: error.StatusCode);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or NpgsqlException)
        {
            return Results.Json(new { code = "result-unavailable", message = "Generated model result is unavailable or invalid." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> RunAsync(
        GeneratedModelRequest request,
        GeneratedModelAnalysisService service,
        HttpRequest httpRequest,
        CancellationToken cancellationToken)
    {
        if (!LocalRequestGuard.Allowed(httpRequest)
            || !httpRequest.HasJsonContentType()
            || httpRequest.Headers["X-CE-Generated-Model-Analysis"] != "1")
            return Results.Json(new { code = "untrusted-request", message = "Generated model analysis requires a trusted local same-origin JSON request." },
                statusCode: StatusCodes.Status403Forbidden);

        try
        {
            return Results.Ok(await service.RunAsync(request, cancellationToken));
        }
        catch (GeneratedModelRequestException error)
        {
            return Results.Json(new { code = error.Code, message = error.Message }, statusCode: error.StatusCode);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            return Results.Json(new { code = "analysis-unavailable", message = "Generated model analysis failed; the latest successful result was preserved." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
