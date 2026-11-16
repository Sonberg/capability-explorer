using Microsoft.Extensions.DependencyInjection;

namespace CapabilityExplorer.Explorer;

/// <summary>HTTP front end for the one read-only <c>ce.process-landscape.v1</c> files-mode operation
/// (<c>GET /api/process-landscape/v1/snapshot</c>). No mutation route exists: the snapshot is produced offline by
/// the TypeScript importer and this API only serves the configured, validated result.</summary>
public static class ProcessLandscapeApi
{
    public static IServiceCollection AddProcessLandscape(this IServiceCollection services)
    {
        services.AddSingleton(provider => provider.GetRequiredService<IConfiguration>()
            .GetSection("ProcessLandscape").Get<ProcessLandscapeOptions>() ?? new());
        services.AddSingleton<ProcessLandscapeService>();
        return services;
    }

    public static void MapProcessLandscapeApi(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/process-landscape/v1/snapshot", async (HttpContext context, ProcessLandscapeService service) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var (response, status) = await service.GetAsync(context.RequestAborted).ConfigureAwait(false);
            return new BoundedJsonResult(response, status, ProcessLandscapeService.MaxResponseBytes,
                StatusCodes.Status413PayloadTooLarge, new ProcessLandscapeResponse("limit", Reason: "process-landscape-response-limit"));
        });
}

public sealed class ProcessLandscapeService(ProcessLandscapeOptions options)
{
    public const int MaxResponseBytes = ProcessLandscapeSizeLimits.MaxResponseBytes;

    public async Task<(ProcessLandscapeResponse Response, int HttpStatus)> GetAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var snapshot = await ProcessLandscapeLoader.LoadAsync(options, deadline.Token).ConfigureAwait(false);
            var identity = ProcessLandscapeIdentity.Verify(snapshot);
            return (new ProcessLandscapeResponse("ok", snapshot, identity), StatusCodes.Status200OK);
        }
        catch (ProcessLandscapeRejectedException ex)
        {
            return (new ProcessLandscapeResponse(ex.Status, Reason: ex.Reason), TaskQueryStatusCodes.HttpStatus(ex.Status));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (new ProcessLandscapeResponse("unavailable", Reason: "process-landscape-timeout"), StatusCodes.Status503ServiceUnavailable);
        }
    }
}
