using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using System.Text.Json.Serialization;

namespace CapabilityExplorer.Explorer;

public sealed record HealthEndpointOptions(string? InternalToken, TimeSpan CacheTtl)
{
    public const int MaximumResponseBytes = 16 * 1024;
    public const int MaximumChecks = 8;
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(5);

    public static HealthEndpointOptions From(IConfiguration configuration)
    {
        var token = configuration["internalHealth:token"] ?? Environment.GetEnvironmentVariable("EXPLORER_INTERNAL_HEALTH_TOKEN");
        var seconds = double.TryParse(configuration["internalHealth:cacheSeconds"], NumberStyles.Float,
            CultureInfo.InvariantCulture, out var configured) && double.IsFinite(configured) && configured is >= 3 and <= 5
            ? configured : 4;
        return new(token, TimeSpan.FromSeconds(seconds));
    }
}

public interface IHealthDependencyCheck
{
    string Name { get; }
    bool Critical { get; }
    Task CheckAsync(CancellationToken cancellationToken);
}

internal sealed class FileExplorerHealthCheck(ExplorerState state) : IHealthDependencyCheck
{
    public string Name => "explorer";
    public bool Critical => true;
    public Task CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = state.Current;
        return Task.CompletedTask;
    }
}

internal sealed class DatabaseHealthCheck(NpgsqlDataSource dataSource, bool critical) : IHealthDependencyCheck
{
    public string Name => "database";
    public bool Critical => critical;
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT 1", connection);
        _ = await command.ExecuteScalarAsync(cancellationToken);
    }
}

public sealed record HealthDependencyResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("criticality")] string Criticality,
    [property: JsonPropertyName("latency_ms")] double LatencyMs,
    [property: JsonPropertyName("error")] string? Error);
public sealed record HealthCheckSnapshot(DateTimeOffset CheckedAt, string Status, bool Ready,
    IReadOnlyDictionary<string, HealthDependencyResult> Checks, string? FailureCause, bool OverallTimedOut = false);

internal sealed class HealthDiagnosticsService(
    IEnumerable<IHealthDependencyCheck> checks,
    HealthEndpointOptions options) : IDisposable
{
    private static readonly string[] DependencyNames = ["database", "explorer", "other"];
    private readonly IHealthDependencyCheck[] _checks = checks.Take(HealthEndpointOptions.MaximumChecks).ToArray();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly DateTimeOffset _processStarted = ReadProcessStart();
    private readonly object _cacheLock = new();
    private HealthCheckSnapshot? _cached;
    private long _cachedAtTimestamp;

    public double UptimeSeconds => Math.Max(0, (DateTimeOffset.UtcNow - _processStarted).TotalSeconds);
    public static string Version => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public async Task<HealthCheckSnapshot> CheckAsync(CancellationToken cancellationToken)
    {
        lock (_cacheLock)
        {
            if (_cached is { } cached && CacheIsFresh())
                return cached;
        }

        using var totalTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        totalTimeout.CancelAfter(HealthEndpointOptions.TotalTimeout);
        try
        {
            await _refreshLock.WaitAsync(totalTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return TimedOutSnapshot();
        }

        try
        {
            lock (_cacheLock)
            {
                if (_cached is { } cached && CacheIsFresh())
                    return cached;
            }

            var tasks = _checks.Select(check => Task.Run(() => RunCheck(check, totalTimeout.Token), CancellationToken.None)).ToArray();
            HealthCheckRun[] checkRuns;
            var overallTimedOut = false;
            try
            {
                checkRuns = await Task.WhenAll(tasks).WaitAsync(totalTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                overallTimedOut = true;
                checkRuns = await Task.WhenAll(tasks.Select((task, index) => task.IsCompleted
                    ? task : Task.FromResult(new HealthCheckRun(_checks[index].Name,
                        new HealthDependencyResult("unhealthy", _checks[index].Critical ? "critical" : "non-critical",
                            HealthEndpointOptions.TotalTimeout.TotalMilliseconds, "timeout")))));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var checksByDependency = checkRuns.GroupBy(pair => SafeName(pair.Name), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last().Result, StringComparer.Ordinal);
            var ready = checksByDependency.Values.Where(result => result.Criticality == "critical")
                .All(result => result.Status == "healthy");
            var status = checksByDependency.Values.All(result => result.Status == "healthy") ? "healthy" : ready ? "degraded" : "unhealthy";
            var failureCause = checksByDependency
                .Where(pair => pair.Value.Status == "unhealthy")
                .Select(pair => pair.Key)
                .Order(StringComparer.Ordinal)
                .FirstOrDefault();
            var snapshot = new HealthCheckSnapshot(DateTimeOffset.UtcNow, status, ready, checksByDependency, failureCause, overallTimedOut);
            if (!overallTimedOut)
            {
                lock (_cacheLock)
                {
                    _cached = snapshot;
                    _cachedAtTimestamp = Stopwatch.GetTimestamp();
                }
            }
            return snapshot;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static HealthCheckSnapshot TimedOutSnapshot() =>
        new(DateTimeOffset.UtcNow, "unhealthy", false,
            new Dictionary<string, HealthDependencyResult>(StringComparer.Ordinal), null, OverallTimedOut: true);

    public void Dispose() => _refreshLock.Dispose();

    private static async Task<HealthCheckRun> RunCheck(IHealthDependencyCheck check, CancellationToken totalToken)
    {
        var timer = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(totalToken);
        timeout.CancelAfter(HealthEndpointOptions.CheckTimeout);
        try
        {
            var task = check.CheckAsync(timeout.Token);
            await task.WaitAsync(timeout.Token);
            var elapsed = timer.Elapsed.TotalMilliseconds;
            HealthMetrics.RecordDependency(SafeName(check.Name), elapsed);
            return new(check.Name, new HealthDependencyResult("healthy", check.Critical ? "critical" : "non-critical", elapsed, null));
        }
        catch (OperationCanceledException)
        {
            var elapsed = timer.Elapsed.TotalMilliseconds;
            HealthMetrics.RecordDependency(SafeName(check.Name), elapsed);
            return new(check.Name, new HealthDependencyResult("unhealthy", check.Critical ? "critical" : "non-critical", elapsed, "timeout"));
        }
        catch (Exception)
        {
            var elapsed = timer.Elapsed.TotalMilliseconds;
            HealthMetrics.RecordDependency(SafeName(check.Name), elapsed);
            return new(check.Name, new HealthDependencyResult("unhealthy", check.Critical ? "critical" : "non-critical", elapsed, "unavailable"));
        }
    }

    private static string SafeName(string name) => DependencyNames.Contains(name, StringComparer.Ordinal) ? name : "other";

    private bool CacheIsFresh() => Stopwatch.GetElapsedTime(_cachedAtTimestamp) < options.CacheTtl;

    private static DateTimeOffset ReadProcessStart()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return new DateTimeOffset(process.StartTime.ToUniversalTime());
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return DateTimeOffset.UtcNow;
        }
    }
}

internal sealed class SourceIpRateLimiter(int publicLimit = 60, int internalLimit = 30)
{
    private const int MaximumTrackedSources = 4096;
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);

    public bool TryAcquire(HttpContext context, bool internalRoute)
    {
        var address = context.Connection.RemoteIpAddress;
        var group = internalRoute ? "internal" : "public";
        var key = $"{address?.MapToIPv6().ToString() ?? "unknown"}:{group}";
        if (!_windows.TryGetValue(key, out var window))
        {
            if (_windows.Count >= MaximumTrackedSources) key = $"overflow:{group}";
            window = _windows.GetOrAdd(key, static _ => new Window());
        }
        return window.TryAcquire(Stopwatch.GetTimestamp(), internalRoute ? internalLimit : publicLimit);
    }

    private sealed class Window
    {
        private readonly object _gate = new();
        private readonly Queue<long> _requests = new();

        public bool TryAcquire(long now, int limit)
        {
            lock (_gate)
            {
                var cutoff = now - Stopwatch.Frequency;
                while (_requests.TryPeek(out var oldest) && oldest <= cutoff) _requests.Dequeue();
                if (_requests.Count >= limit) return false;
                _requests.Enqueue(now);
                return true;
            }
        }
    }
}

internal sealed class HealthMetrics
{
    private static readonly double[] Buckets = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10];
    private static readonly ConcurrentDictionary<string, Histogram> Requests = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Histogram> Dependencies = new(StringComparer.Ordinal);

    public static void RecordRequest(HttpContext context, double durationSeconds)
    {
        var method = context.Request.Method is "GET" or "POST" or "PUT" or "DELETE" ? context.Request.Method : "OTHER";
        var path = context.Request.Path.Value is "/health" or "/health/live" or "/health/ready" or "/health/startup"
            or "/internal/health" or "/metrics" ? context.Request.Path.Value
            : context.Request.Path.StartsWithSegments("/api") ? "/api" : "/other";
        var status = Math.Clamp(context.Response.StatusCode, 100, 599).ToString(CultureInfo.InvariantCulture);
        Requests.GetOrAdd($"{method}\n{status}\n{path}", static _ => new Histogram()).Observe(durationSeconds);
    }

    public static void RecordDependency(string name, double durationMs)
    {
        var boundedName = name is "database" or "explorer" ? name : "other";
        Dependencies.GetOrAdd(boundedName, static _ => new Histogram()).Observe(durationMs / 1000);
    }

    public static string Export(double uptimeSeconds)
    {
        var builder = new StringBuilder(4096);
        builder.AppendLine("# HELP http_requests_total Total Explorer HTTP requests.")
            .AppendLine("# TYPE http_requests_total counter")
            .AppendLine("# HELP http_request_duration_seconds Explorer HTTP request duration.")
            .AppendLine("# TYPE http_request_duration_seconds histogram");
        AppendHistograms(builder, "http_request_duration_seconds", Requests, "method", "status", "path");
        builder.AppendLine("# HELP dependency_check_duration_seconds Dependency health-check duration.")
            .AppendLine("# TYPE dependency_check_duration_seconds histogram");
        AppendHistograms(builder, "dependency_check_duration_seconds", Dependencies, "dependency");
        using var process = Process.GetCurrentProcess();
        builder.AppendLine("# HELP process_uptime_seconds Explorer process uptime.")
            .AppendLine("# TYPE process_uptime_seconds gauge")
            .Append("process_uptime_seconds ").AppendLine(Fmt(uptimeSeconds));
        builder.AppendLine("# HELP process_resident_memory_bytes Explorer resident memory.")
            .AppendLine("# TYPE process_resident_memory_bytes gauge")
            .Append("process_resident_memory_bytes ").AppendLine(process.WorkingSet64.ToString(CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    private static void AppendHistograms(StringBuilder builder, string metric, ConcurrentDictionary<string, Histogram> values, params string[] labels)
    {
        foreach (var (key, histogram) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var fields = key.Split('\n');
            var suffix = string.Join(",", labels.Select((label, index) => $"{label}=\"{Escape(fields[index])}\""));
            var snapshot = histogram.Snapshot();
            foreach (var (bound, count) in snapshot.Buckets)
                builder.Append(metric).Append("_bucket{").Append(suffix).Append(",le=\"").Append(bound).Append("\"} ").AppendLine(count.ToString(CultureInfo.InvariantCulture));
            builder.Append(metric).Append("_bucket{").Append(suffix).Append(",le=\"+Inf\"} ").AppendLine(snapshot.Count.ToString(CultureInfo.InvariantCulture));
            builder.Append(metric).Append("_sum{").Append(suffix).Append("} ").AppendLine(Fmt(snapshot.Sum));
            builder.Append(metric).Append("_count{").Append(suffix).Append("} ").AppendLine(snapshot.Count.ToString(CultureInfo.InvariantCulture));
            if (metric == "http_request_duration_seconds")
                builder.Append("http_requests_total{").Append(suffix).Append("} ").AppendLine(snapshot.Count.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static string Fmt(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    private sealed class Histogram
    {
        private readonly object _gate = new();
        private readonly long[] _buckets = new long[Buckets.Length];
        public long Count { get; private set; }
        public double Sum { get; private set; }

        public void Observe(double value)
        {
            lock (_gate)
            {
                Count++;
                Sum += value;
                for (var index = 0; index < Buckets.Length; index++)
                    if (value <= Buckets[index]) _buckets[index]++;
            }
        }

        public HistogramSnapshot Snapshot()
        {
            lock (_gate)
                return new(Buckets.Select((bound, index) => (Fmt(bound), _buckets[index])).ToArray(), Count, Sum);
        }
    }

    private sealed record HistogramSnapshot((string Bound, long Count)[] Buckets, long Count, double Sum);
}

internal sealed record HealthCheckRun(string Name, HealthDependencyResult Result);

internal static class HealthToken
{
    public static bool IsAuthorized(HttpRequest request, HealthEndpointOptions options)
    {
        if (string.IsNullOrEmpty(options.InternalToken) || options.InternalToken.Length > 512) return false;
        var authorization = request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal) || authorization.Length > 519) return false;
        var supplied = Encoding.UTF8.GetBytes(authorization[7..]);
        var expected = Encoding.UTF8.GetBytes(options.InternalToken);
        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
    }
}
