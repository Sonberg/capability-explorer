using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CapabilityExplorer.Explorer;

public static class OwnershipAlignmentApi
{
    public static IServiceCollection AddOwnershipAlignment(this IServiceCollection services)
    {
        services.AddSingleton(provider => provider.GetRequiredService<IConfiguration>()
            .GetSection("OwnershipAlignment").Get<OwnershipAlignmentOptions>() ?? new());
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<OwnershipAlignmentService>();
        return services;
    }

    public static void MapOwnershipAlignmentApi(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/ownership-alignment", async (HttpContext context, OwnershipAlignmentService service) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var report = await service.ReportAsync(context.Request.Query, context.RequestAborted).ConfigureAwait(false);
            return new BoundedJsonResult(report, TaskQueryStatusCodes.HttpStatus(report.Status), 4 * 1024 * 1024,
                StatusCodes.Status413PayloadTooLarge, service.Error(report.Audience, "limit", "ownership-response-limit"));
        });
}

public sealed class OwnershipAlignmentService(
    OwnershipAlignmentOptions options, TimeProvider clock) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<OwnershipReport> ReportAsync(IQueryCollection query, CancellationToken ct)
    {
        var audience = query["audience"].ToString();
        if (audience is not ("engineering" or "architecture")) audience = "engineering";
        if (!options.Enabled || options.Repositories is not { Length: > 0 })
            return Error(audience, "denied", "ownership-alignment-not-configured");
        if (options.Repositories.Length > 100 || !options.Repositories.All(OwnershipInputValidation.IsRepository) ||
            options.Repositories.Distinct(StringComparer.Ordinal).Count() != options.Repositories.Length ||
            options.Root is not { Length: > 0 } || !Path.IsPathFullyQualified(options.Root) || options.Snapshots is null ||
            options.Snapshots.Keys.Any(system => !OwnershipSystems.All.Contains(system, StringComparer.Ordinal)))
            return Error(audience, "denied", "ownership-alignment-invalid-configuration");
        if (query.Keys.Any(key => key is not ("audience" or "repository")) ||
            query.Any(pair => pair.Value.Count != 1) ||
            query.ContainsKey("audience") && query["audience"].ToString() is not ("engineering" or "architecture"))
            return Error(audience, "invalid", "invalid-ownership-request");
        var repository = query["repository"].ToString();
        if (query.ContainsKey("repository") && !options.Repositories.Contains(repository, StringComparer.Ordinal))
            return Error(audience, "denied", "ownership-repository-not-granted");
        string[] repositories = query.ContainsKey("repository") ? [repository] : options.Repositories;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var acquired = false;
        try
        {
            await gate.WaitAsync(deadline.Token).ConfigureAwait(false);
            acquired = true;
            var now = clock.GetUtcNow();
            var inputs = await OwnershipInputLoader.LoadAsync(options, repositories, now, deadline.Token).ConfigureAwait(false);
            return await Task.Run(() => OwnershipAlignmentEngine.Evaluate(inputs, now, audience, deadline.Token), deadline.Token).ConfigureAwait(false);
        }
        catch (OwnershipEvaluationLimitException)
        {
            return Error(audience, "limit", "ownership-evaluation-limit");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Error(audience, "cancelled", "ownership-evaluation-timeout");
        }
        finally
        {
            if (acquired) gate.Release();
        }
    }

    public OwnershipReport Error(string audience, string status, string reason) => new(
        status, "not-evaluable", audience, "", clock.GetUtcNow().ToString("O"), new([]), [], [],
        status == "denied" ? [] : [new("manual-trust", "No ownership alignment is established; configured input review is declared, not authenticated.")],
        reason, status == "denied"
            ? "Have an authorized operator configure OwnershipAlignment.Enabled, Root, exact Repositories, Snapshots, Crosswalk and Rules; request only a granted repository."
            : "Correct the request or narrow it to one granted repository and retry; no partial report is presented as complete.");

    public void Dispose() => gate.Dispose();
}
