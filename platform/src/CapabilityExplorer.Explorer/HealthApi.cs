using System.Diagnostics;
using System.Text.Json;

namespace CapabilityExplorer.Explorer;

internal static class HealthApi
{
    public static void MapHealthApi(this WebApplication app)
    {
        app.MapGet("/health", (HttpContext http, HealthDiagnosticsService health, SourceIpRateLimiter limits) =>
        {
            if (!limits.TryAcquire(http, internalRoute: false)) return RateLimited(http, health);
            return PublicHealth(http, health, "healthy");
        });
        app.MapGet("/health/live", (HttpContext http, HealthDiagnosticsService health, SourceIpRateLimiter limits) =>
        {
            if (!limits.TryAcquire(http, internalRoute: false)) return RateLimited(http, health);
            return PublicHealth(http, health, "healthy");
        });
        app.MapGet("/health/startup", (HttpContext http, HealthDiagnosticsService health, HealthApplicationLifecycle lifecycle, SourceIpRateLimiter limits) =>
        {
            if (!limits.TryAcquire(http, internalRoute: false)) return RateLimited(http, health);
            var started = lifecycle.HasStarted;
            return PublicHealth(http, health, started ? "healthy" : "unhealthy", started ? 200 : 503);
        });
        app.MapGet("/health/ready", (HttpContext http, HealthDiagnosticsService health, HealthApplicationLifecycle lifecycle, SourceIpRateLimiter limits) =>
        {
            if (!limits.TryAcquire(http, internalRoute: false)) return RateLimited(http, health);
            return PublicHealth(http, health, lifecycle.IsReady ? "healthy" : "unhealthy", lifecycle.IsReady ? 200 : 503);
        });
        app.MapGet("/internal/health", async (HttpContext http, HealthDiagnosticsService health, HealthEndpointOptions options,
            SourceIpRateLimiter limits) =>
        {
            if (!limits.TryAcquire(http, internalRoute: true)) return RateLimited(http, health);
            if (string.IsNullOrEmpty(options.InternalToken))
                return Json(http, health, new { status = "unavailable", error = "internal health access is not configured" }, 503);
            if (!HealthToken.IsAuthorized(http.Request, options))
                return Json(http, health, new { status = "unauthorized", error = "authorization required" }, 401);
            var snapshot = await health.CheckAsync(http.RequestAborted);
            if (snapshot.FailureCause == "timeout") return TimeoutResponse();
            return InternalHealth(http, health, snapshot, snapshot.Ready ? 200 : 503);
        });
        app.MapGet("/metrics", (HttpContext http, HealthDiagnosticsService health, HealthEndpointOptions options,
            SourceIpRateLimiter limits) =>
        {
            if (!limits.TryAcquire(http, internalRoute: true)) return RateLimited(http, health);
            if (string.IsNullOrEmpty(options.InternalToken))
                return Json(http, health, new { status = "unavailable", error = "internal health access is not configured" }, 503);
            if (!HealthToken.IsAuthorized(http.Request, options))
                return Json(http, health, new { status = "unauthorized", error = "authorization required" }, 401);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Text(HealthMetrics.Export(health.UptimeSeconds), "text/plain; version=0.0.4; charset=utf-8");
        });
    }

    public static void UseHealthRequestMetadata(this WebApplication app)
    {
        app.Use(async (http, next) =>
        {
            var requestId = Guid.NewGuid().ToString("D");
            http.Response.Headers["X-Request-Id"] = requestId;
            var path = http.Request.Path;
            if (path.StartsWithSegments("/health") || path.StartsWithSegments("/internal/health") || path == "/metrics")
            {
                http.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                http.Response.Headers.Pragma = "no-cache";
                http.Response.Headers.Expires = "0";
            }

            var timer = Stopwatch.StartNew();
            try
            {
                await next(http);
            }
            finally
            {
                HealthMetrics.RecordRequest(http, timer.Elapsed.TotalSeconds);
            }
        });
    }

    private static BoundedJsonResult PublicHealth(HttpContext http, HealthDiagnosticsService health, string status, int statusCode = 200)
    {
        var result = new Dictionary<string, object?>
        {
            ["status"] = status,
            ["version"] = HealthDiagnosticsService.Version,
            ["uptime_seconds"] = Math.Round(health.UptimeSeconds, 3),
            ["timestamp"] = DateTimeOffset.UtcNow,
        };
        return new BoundedJsonResult(result, statusCode, HealthEndpointOptions.MaximumResponseBytes);
    }

    private static BoundedJsonResult InternalHealth(HttpContext http, HealthDiagnosticsService health,
        HealthCheckSnapshot snapshot, int statusCode)
    {
        using var process = Process.GetCurrentProcess();
        var memory = GC.GetGCMemoryInfo();
        var result = new Dictionary<string, object?>
        {
            ["status"] = snapshot.Status,
            ["version"] = HealthDiagnosticsService.Version,
            ["uptime_seconds"] = Math.Round(health.UptimeSeconds, 3),
            ["timestamp"] = DateTimeOffset.UtcNow,
            ["failure_cause"] = JsonSerializer.SerializeToElement(snapshot.FailureCause),
            ["memory"] = new
            {
                heap_used_mb = Math.Round(GC.GetTotalMemory(false) / 1048576d, 2),
                heap_total_mb = Math.Round(memory.TotalAvailableMemoryBytes / 1048576d, 2),
                rss_mb = Math.Round(process.WorkingSet64 / 1048576d, 2),
            },
            ["checks"] = snapshot.Checks,
        };
        return new BoundedJsonResult(result, statusCode, HealthEndpointOptions.MaximumResponseBytes);
    }

    private static IResult TimeoutResponse() =>
        Results.Json(new { status = "unhealthy", reason = "timeout" }, statusCode: 503);

    private static BoundedJsonResult RateLimited(HttpContext http, HealthDiagnosticsService health) =>
        PublicHealth(http, health, "unhealthy", 429);

    private static BoundedJsonResult Json(HttpContext http, HealthDiagnosticsService health, object fields, int statusCode = 200)
    {
        var result = new Dictionary<string, object?>
        {
            ["status"] = "unhealthy",
            ["version"] = HealthDiagnosticsService.Version,
            ["uptime_seconds"] = Math.Round(health.UptimeSeconds, 3),
            ["timestamp"] = DateTimeOffset.UtcNow,
        };
        foreach (var property in fields.GetType().GetProperties())
            result[property.Name] = property.GetValue(fields);
        return new BoundedJsonResult(result, statusCode, HealthEndpointOptions.MaximumResponseBytes);
    }
}

internal sealed class HealthApplicationLifecycle(IHostApplicationLifetime lifetime)
{
    public bool HasStarted => lifetime.ApplicationStarted.IsCancellationRequested;
    public bool IsReady => HasStarted && !lifetime.ApplicationStopping.IsCancellationRequested;
}
