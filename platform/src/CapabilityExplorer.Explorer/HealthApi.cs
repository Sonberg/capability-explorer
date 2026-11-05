using System.Diagnostics;

namespace CapabilityExplorer.Explorer;

internal static class HealthApi
{
    public static void MapHealthApi(this WebApplication app)
    {
        app.MapGet("/health", (HttpContext http, HealthDiagnosticsService health, SourceIpRateLimiter limits) =>
        {
            if (!limits.TryAcquire(http, internalRoute: false)) return RateLimited(http, health);
            return Json(http, health, new { status = "healthy", scope = "process" });
        });
        app.MapGet("/health/live", (HttpContext http, HealthDiagnosticsService health, SourceIpRateLimiter limits) =>
        {
            if (!limits.TryAcquire(http, internalRoute: false)) return RateLimited(http, health);
            return Json(http, health, new { status = "healthy", live = true });
        });
        app.MapGet("/health/startup", (HttpContext http, HealthDiagnosticsService health, IHostApplicationLifetime lifetime, SourceIpRateLimiter limits) =>
        {
            if (!limits.TryAcquire(http, internalRoute: false)) return RateLimited(http, health);
            var started = lifetime.ApplicationStarted.IsCancellationRequested;
            return Json(http, health, new { status = started ? "healthy" : "unhealthy", started }, started ? 200 : 503);
        });
        app.MapGet("/health/ready", async (HttpContext http, HealthDiagnosticsService health, SourceIpRateLimiter limits) =>
        {
            if (!limits.TryAcquire(http, internalRoute: false)) return RateLimited(http, health);
            var snapshot = await health.CheckAsync(http.RequestAborted);
            return Json(http, health, new { status = snapshot.Status, ready = snapshot.Ready }, snapshot.Ready ? 200 : 503);
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
            return Json(http, health, new
            {
                status = snapshot.Status,
                ready = snapshot.Ready,
                checkedAt = snapshot.CheckedAt,
                results = snapshot.Results
            }, snapshot.Ready ? 200 : 503);
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

    private static BoundedJsonResult Json(HttpContext http, HealthDiagnosticsService health, object fields, int statusCode = 200)
    {
        var result = new Dictionary<string, object?>
        {
            ["timestamp"] = DateTimeOffset.UtcNow,
            ["uptimeSeconds"] = Math.Round(health.UptimeSeconds, 3),
            ["version"] = HealthDiagnosticsService.Version,
            ["requestId"] = http.Response.Headers["X-Request-Id"].ToString(),
        };
        foreach (var property in fields.GetType().GetProperties())
            result[property.Name] = property.GetValue(fields);
        return new BoundedJsonResult(result, statusCode, HealthEndpointOptions.MaximumResponseBytes);
    }

    private static BoundedJsonResult RateLimited(HttpContext http, HealthDiagnosticsService health) =>
        Json(http, health, new { status = "rate_limited", error = "request rate exceeded" }, 429);
}
