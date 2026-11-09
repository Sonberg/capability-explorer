using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using CapabilityExplorer.Explorer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class HealthApiFactory(Action<IServiceCollection>? configureServices = null, bool configureToken = true,
    ILoggerProvider? loggerProvider = null, IReadOnlyDictionary<string, string?>? settings = null)
    : WebApplicationFactory<Program>
{
    public const string Token = "health-test-token";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("artifacts", Fixtures.ExpectedDirectory);
        builder.UseSetting("repos", Path.Combine(Fixtures.RepositoryRoot, "fixtures", ".repos"));
        if (configureToken) builder.UseSetting("internalHealth:token", Token);
        if (loggerProvider is not null) builder.ConfigureLogging(logging => logging.AddProvider(loggerProvider));
        if (settings is not null)
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<SourceIpRateLimiter>();
            services.AddSingleton(new SourceIpRateLimiter(1000, 1000));
            configureServices?.Invoke(services);
        });
    }
}

/// <summary>A single captured log entry: the formatted message plus its structured key/value fields
/// (excluding the "{OriginalFormat}" template key), so tests can assert exact field values rather than
/// parsing the rendered message string.</summary>
public sealed record CapturedLogEntry(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Fields);

/// <summary>Captures only <see cref="HealthDiagnosticsService"/> log entries for assertion, never raw exceptions.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<CapturedLogEntry> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);
    public void Dispose() { }

    private sealed class CapturingLogger(CapturingLoggerProvider owner, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => categoryName == typeof(HealthDiagnosticsService).FullName;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var fields = (state as IReadOnlyList<KeyValuePair<string, object?>> ?? [])
                .Where(pair => pair.Key != "{OriginalFormat}")
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            owner.Entries.Enqueue(new CapturedLogEntry(logLevel, formatter(state, exception), fields));
        }
    }
}

public sealed class HealthApiTests
{
    [Fact]
    public void Startup_remains_started_after_shutdown_begins_while_readiness_goes_false()
    {
        using var lifetime = new TestApplicationLifetime();
        var state = new HealthApplicationLifecycle(lifetime);
        Assert.False(state.HasStarted);
        Assert.False(state.IsReady);

        lifetime.MarkStarted();
        Assert.True(state.HasStarted);
        Assert.True(state.IsReady);

        lifetime.StopApplication();
        Assert.True(state.HasStarted);
        Assert.False(state.IsReady);
    }

    [Theory]
    [InlineData("3", 3)]
    [InlineData("4", 4)]
    [InlineData("5", 5)]
    [InlineData("2", 4)]
    public void Internal_cache_ttl_is_configurable_only_within_the_supported_range(string configured, int expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["internalHealth:cacheSeconds"] = configured })
            .Build();
        Assert.Equal(TimeSpan.FromSeconds(expected), HealthEndpointOptions.From(configuration).CacheTtl);
    }

    [Fact]
    public async Task Public_health_routes_are_unauthenticated_bounded_and_non_cacheable()
    {
        using var factory = new HealthApiFactory();
        using var client = factory.CreateClient();
        foreach (var route in new[] { "/health", "/health/live", "/health/startup", "/health/ready" })
        {
            using var response = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            AssertNoStore(response.Headers.CacheControl?.ToString());
            Assert.True(Guid.TryParse(response.Headers.GetValues("X-Request-Id").Single(), out var id));
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.InRange(bytes.Length, 1, HealthEndpointOptions.MaximumResponseBytes);
            using var body = JsonDocument.Parse(bytes);
            var root = body.RootElement;
            Assert.Equal(new[] { "status", "timestamp", "uptime_seconds", "version" },
                root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
            Assert.Equal(TimeSpan.Zero, DateTimeOffset.Parse(root.GetProperty("timestamp").GetString()!, CultureInfo.InvariantCulture).Offset);
            Assert.True(root.GetProperty("uptime_seconds").TryGetInt64(out var uptimeSeconds) && uptimeSeconds >= 0);
            Assert.Matches(@"^\d+\.\d+\.\d+$", root.GetProperty("version").GetString()!);
            Assert.NotEqual(Guid.Empty, id);
        }
    }

    [Fact]
    public async Task Oversized_health_json_returns_the_contract_error()
    {
        using var factory = new HealthApiFactory();
        using var body = new MemoryStream();
        var context = new DefaultHttpContext
        {
            RequestServices = factory.Services,
        };
        context.Response.Body = body;

        await new BoundedJsonResult(new { value = new string('x', 32) }, maximumBytes: 1,
            oversizedStatusCode: StatusCodes.Status500InternalServerError,
            oversizedBody: new { error = "response_too_large" }).ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        body.Position = 0;
        using var json = await JsonDocument.ParseAsync(body);
        Assert.Equal("response_too_large", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Lightweight_health_does_not_run_dependency_probes()
    {
        var probes = 0;
        using var factory = new HealthApiFactory(services =>
        {
            services.RemoveAll<IHealthDependencyCheck>();
            services.AddSingleton<IHealthDependencyCheck>(new FakeHealthCheck("database", critical: true, _ =>
            {
                Interlocked.Increment(ref probes);
                return Task.CompletedTask;
            }));
        });
        using var client = factory.CreateClient();
        using (var warmup = await client.GetAsync("/health"))
            Assert.Equal(HttpStatusCode.OK, warmup.StatusCode);
        var timer = Stopwatch.StartNew();
        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(timer.Elapsed < TimeSpan.FromMilliseconds(50), $"lightweight health took {timer.Elapsed}");
        Assert.Equal(0, Volatile.Read(ref probes));
    }

    [Fact]
    public async Task Public_readiness_does_not_run_dependency_probes()
    {
        var probes = 0;
        using var factory = new HealthApiFactory(services =>
        {
            services.RemoveAll<IHealthDependencyCheck>();
            services.AddSingleton<IHealthDependencyCheck>(new FakeHealthCheck("database", critical: true, _ =>
            {
                Interlocked.Increment(ref probes);
                return Task.CompletedTask;
            }));
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, Volatile.Read(ref probes));
    }

    [Fact]
    public async Task Internal_routes_fail_closed_and_require_bearer_authentication()
    {
        using var unavailableFactory = new HealthApiFactory(configureToken: false);
        using var unavailableClient = unavailableFactory.CreateClient();
        using var disabled = await unavailableClient.GetAsync("/internal/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabled.StatusCode);
        using var disabledMetrics = await unavailableClient.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledMetrics.StatusCode);

        using var factory = new HealthApiFactory(services =>
        {
            services.RemoveAll<IHealthDependencyCheck>();
            services.AddSingleton<IHealthDependencyCheck>(new FakeHealthCheck("database", critical: true, _ => Task.CompletedTask));
        });
        using var client = factory.CreateClient();
        using var absent = await client.GetAsync("/internal/health");
        Assert.Equal(HttpStatusCode.Unauthorized, absent.StatusCode);
        using var wrong = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        wrong.Headers.Authorization = new("Bearer", "wrong-token");
        using var rejected = await client.SendAsync(wrong);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        AssertNoStore(rejected.Headers.CacheControl?.ToString());

        using var authorized = new HttpRequestMessage(HttpMethod.Get, "/internal/health");
        authorized.Headers.Authorization = new("Bearer", HealthApiFactory.Token);
        using var accepted = await client.SendAsync(authorized);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var acceptedJson = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        var body = acceptedJson.RootElement;
        Assert.True(body.TryGetProperty("failure_cause", out _));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("failure_cause").ValueKind);
        Assert.True(body.TryGetProperty("memory", out var memory));
        Assert.True(memory.TryGetProperty("heap_used_mb", out _));
        Assert.True(memory.TryGetProperty("heap_total_mb", out _));
        Assert.True(memory.TryGetProperty("rss_mb", out _));
        Assert.True(body.TryGetProperty("checks", out var checks));
        Assert.True(checks.TryGetProperty("database", out var database));
        Assert.Equal("critical", database.GetProperty("criticality").GetString());
        Assert.True(database.TryGetProperty("latency_ms", out _));
    }

    [Fact]
    public async Task Check_errors_are_sanitized_and_noncritical_failures_degrade_without_unready_status()
    {
        using var factory = new HealthApiFactory(services =>
        {
            services.RemoveAll<IHealthDependencyCheck>();
            services.AddSingleton<IHealthDependencyCheck>(new FakeHealthCheck("database", critical: false,
                _ => throw new InvalidOperationException("secret password and stack trace")));
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/internal/health");
        request.Headers.Authorization = new("Bearer", HealthApiFactory.Token);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret password", content, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", content, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(content);
        Assert.Equal("degraded", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("non-critical", json.RootElement.GetProperty("checks").GetProperty("database").GetProperty("criticality").GetString());
        Assert.Equal("unavailable", json.RootElement.GetProperty("checks").GetProperty("database").GetProperty("error").GetString());
        using var readiness = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
        using var publicJson = JsonDocument.Parse(await readiness.Content.ReadAsStringAsync());
        Assert.Equal("healthy", publicJson.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Dependency_checks_run_in_parallel_and_time_out_without_exposing_exceptions()
    {
        using var factory = new HealthApiFactory(services =>
        {
            services.RemoveAll<IHealthDependencyCheck>();
            services.AddSingleton<IHealthDependencyCheck>(new FakeHealthCheck("database", critical: true,
                async cancellationToken => await Task.Delay(Timeout.Infinite, cancellationToken)));
            services.AddSingleton<IHealthDependencyCheck>(new FakeHealthCheck("explorer", critical: true,
                _ => Task.CompletedTask));
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/internal/health");
        request.Headers.Authorization = new("Bearer", HealthApiFactory.Token);
        var timer = Stopwatch.StartNew();
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(4), $"checks did not run in parallel or exceed their bound: {timer.Elapsed}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("database", json.RootElement.GetProperty("failure_cause").GetString());
        Assert.Equal("unhealthy", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("timeout", json.RootElement.GetProperty("checks").GetProperty("database").GetProperty("error").GetString());
        Assert.Equal("healthy", json.RootElement.GetProperty("checks").GetProperty("explorer").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Health_check_count_and_serialized_result_size_are_bounded()
    {
        using var factory = new HealthApiFactory(services =>
        {
            services.RemoveAll<IHealthDependencyCheck>();
            for (var index = 0; index < 20; index++)
                services.AddSingleton<IHealthDependencyCheck>(new FakeHealthCheck($"check-{index}", critical: true,
                    _ => Task.CompletedTask));
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/internal/health");
        request.Headers.Authorization = new("Bearer", HealthApiFactory.Token);
        using var response = await client.SendAsync(request);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.InRange(bytes.Length, 1, HealthEndpointOptions.MaximumResponseBytes);
        using var json = JsonDocument.Parse(bytes);
        var checks = json.RootElement.GetProperty("checks");
        Assert.InRange(checks.EnumerateObject().Count(), 1, HealthEndpointOptions.MaximumChecks);
        Assert.True(checks.TryGetProperty("other", out _));
    }

    [Fact]
    public async Task Source_ip_limits_apply_to_public_and_internal_routes()
    {
        using var factory = new HealthApiFactory(services =>
        {
            services.RemoveAll<SourceIpRateLimiter>();
            services.AddSingleton(new SourceIpRateLimiter(1, 1));
        });
        using var client = factory.CreateClient();
        using var first = await client.GetAsync("/health/live");
        using var limited = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

        using var authorized = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        authorized.Headers.Authorization = new("Bearer", HealthApiFactory.Token);
        using var internalFirst = await client.SendAsync(authorized);
        using var internalSecond = new HttpRequestMessage(HttpMethod.Get, "/internal/health");
        internalSecond.Headers.Authorization = new("Bearer", HealthApiFactory.Token);
        using var internalLimited = await client.SendAsync(internalSecond);
        Assert.Equal(HttpStatusCode.OK, internalFirst.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, internalLimited.StatusCode);
    }

    [Fact]
    public async Task Metrics_use_prometheus_text_with_http_dependency_and_process_metrics()
    {
        using var factory = new HealthApiFactory();
        using var client = factory.CreateClient();
        using var health = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        request.Headers.Authorization = new("Bearer", HealthApiFactory.Token);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/plain", response.Content.Headers.ContentType?.ToString(), StringComparison.Ordinal);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("# TYPE http_requests_total counter", text, StringComparison.Ordinal);
        Assert.Contains("http_requests_total{method=\"GET\",status=\"200\",path=\"/health/ready\"}", text, StringComparison.Ordinal);
        Assert.Contains("http_request_duration_seconds_bucket{method=\"GET\",status=\"200\",path=\"/health/ready\"", text, StringComparison.Ordinal);
        Assert.Contains("dependency_check_duration_seconds_bucket{", text, StringComparison.Ordinal);
        Assert.Contains("process_uptime_seconds ", text, StringComparison.Ordinal);
        Assert.Contains("process_resident_memory_bytes ", text, StringComparison.Ordinal);
    }

    private static readonly IReadOnlyDictionary<string, string?> MinimumCacheTtlSettings =
        new Dictionary<string, string?> { ["internalHealth:cacheSeconds"] = "3" }; // 3s is the policy-defined minimum (HealthEndpointOptions.From), not a new TTL value

    [Fact]
    public async Task Unhealthy_transition_logs_warning_and_recovery_logs_information_once_per_change()
    {
        var attempt = 0;
        var capture = new CapturingLoggerProvider();
        using var factory = new HealthApiFactory(services =>
        {
            services.RemoveAll<IHealthDependencyCheck>();
            services.AddSingleton<IHealthDependencyCheck>(new FakeHealthCheck("database", critical: true, _ =>
                Interlocked.Increment(ref attempt) == 1
                    ? throw new InvalidOperationException("secret connection string and stack trace")
                    : Task.CompletedTask));
        }, loggerProvider: capture, settings: MinimumCacheTtlSettings);
        using var client = factory.CreateClient();

        async Task<HttpStatusCode> PollInternalHealthAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/internal/health");
            request.Headers.Authorization = new("Bearer", HealthApiFactory.Token);
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.ServiceUnavailable, await PollInternalHealthAsync()); // unknown -> unhealthy
        await Task.Delay(TimeSpan.FromSeconds(3.2)); // past the configured 3s cache TTL, forces a fresh probe
        Assert.Equal(HttpStatusCode.OK, await PollInternalHealthAsync()); // unhealthy -> healthy (recovery)
        await Task.Delay(TimeSpan.FromSeconds(3.2));
        Assert.Equal(HttpStatusCode.OK, await PollInternalHealthAsync()); // healthy -> healthy: no new log entry

        var entries = capture.Entries.ToArray();
        var warnings = entries.Where(entry => entry.Level == LogLevel.Warning).ToArray();
        var infos = entries.Where(entry => entry.Level == LogLevel.Information).ToArray();
        var warning = Assert.Single(warnings);
        Assert.Equal("database", warning.Fields["CheckName"]);
        Assert.Equal("unknown", warning.Fields["PreviousState"]);
        Assert.Equal("unhealthy", warning.Fields["NewState"]);
        Assert.Equal("database", warning.Fields["FailureCause"]);
        Assert.True(warning.Fields["LatencyMs"] is double);
        Assert.True(warning.Fields["Timestamp"] is DateTimeOffset);
        Assert.DoesNotContain("InvalidOperationException", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret connection string", warning.Message, StringComparison.Ordinal);
        var info = Assert.Single(infos);
        Assert.Equal("database", info.Fields["CheckName"]);
        Assert.Equal("unhealthy", info.Fields["PreviousState"]);
        Assert.Equal("healthy", info.Fields["NewState"]);
    }

    [Fact]
    public async Task Each_unhealthy_check_logs_its_own_name_as_failure_cause_not_a_different_checks()
    {
        var databaseAttempt = 0;
        var capture = new CapturingLoggerProvider();
        using var factory = new HealthApiFactory(services =>
        {
            services.RemoveAll<IHealthDependencyCheck>();
            services.AddSingleton<IHealthDependencyCheck>(new FakeHealthCheck("database", critical: true, _ =>
                Interlocked.Increment(ref databaseAttempt) == 1
                    ? throw new InvalidOperationException("db unreachable")
                    : Task.CompletedTask));
            services.AddSingleton<IHealthDependencyCheck>(new FakeHealthCheck("explorer", critical: true,
                _ => throw new InvalidOperationException("explorer unreachable")));
        }, loggerProvider: capture, settings: MinimumCacheTtlSettings);
        using var client = factory.CreateClient();

        async Task<HttpStatusCode> PollInternalHealthAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/internal/health");
            request.Headers.Authorization = new("Bearer", HealthApiFactory.Token);
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.ServiceUnavailable, await PollInternalHealthAsync()); // both database and explorer unhealthy
        await Task.Delay(TimeSpan.FromSeconds(3.2));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await PollInternalHealthAsync()); // database recovers, explorer stays unhealthy

        var warnings = capture.Entries.Where(entry => entry.Level == LogLevel.Warning).ToArray();
        Assert.Equal(2, warnings.Length);
        var databaseWarning = Assert.Single(warnings, entry => Equals(entry.Fields["CheckName"], "database"));
        Assert.Equal("database", databaseWarning.Fields["FailureCause"]);
        var explorerWarning = Assert.Single(warnings, entry => Equals(entry.Fields["CheckName"], "explorer"));
        Assert.Equal("explorer", explorerWarning.Fields["FailureCause"]); // must not be "database" (the snapshot's own alphabetical FailureCause)

        var infos = capture.Entries.Where(entry => entry.Level == LogLevel.Information).ToArray();
        var info = Assert.Single(infos);
        Assert.Equal("database", info.Fields["CheckName"]); // only the recovered check logs, not explorer (still unhealthy)
    }

    private sealed class FakeHealthCheck(string name, bool critical, Func<CancellationToken, Task> check) : IHealthDependencyCheck
    {
        public string Name { get; } = name;
        public bool Critical { get; } = critical;
        public Task CheckAsync(CancellationToken cancellationToken) => check(cancellationToken);
    }

    private sealed class TestApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();
        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;
        public void MarkStarted() => _started.Cancel();
        public void StopApplication() => _stopping.Cancel();
        public void Dispose()
        {
            _started.Dispose();
            _stopping.Dispose();
            _stopped.Dispose();
        }
    }

    private static void AssertNoStore(string? value)
    {
        Assert.NotNull(value);
        Assert.Contains("no-store", value, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-cache", value, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("must-revalidate", value, StringComparison.OrdinalIgnoreCase);
    }
}
