using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using CapabilityExplorer.Explorer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class HealthApiFactory(Action<IServiceCollection>? configureServices = null, bool configureToken = true)
    : WebApplicationFactory<Program>
{
    public const string Token = "health-test-token";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("artifacts", Fixtures.ExpectedDirectory);
        builder.UseSetting("repos", Path.Combine(Fixtures.RepositoryRoot, "fixtures", ".repos"));
        if (configureToken) builder.UseSetting("internalHealth:token", Token);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<SourceIpRateLimiter>();
            services.AddSingleton(new SourceIpRateLimiter(1000, 1000));
            configureServices?.Invoke(services);
        });
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
            Assert.True(root.GetProperty("uptime_seconds").GetDouble() >= 0);
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("version").GetString()));
            Assert.NotEqual(Guid.Empty, id);
        }
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
    public async Task Internal_routes_fail_closed_and_require_bearer_authentication()
    {
        using var unavailableFactory = new HealthApiFactory(configureToken: false);
        using var unavailableClient = unavailableFactory.CreateClient();
        using var disabled = await unavailableClient.GetAsync("/internal/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabled.StatusCode);
        using var disabledMetrics = await unavailableClient.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledMetrics.StatusCode);

        using var factory = new HealthApiFactory();
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
        Assert.True(body.TryGetProperty("memory", out var memory));
        Assert.True(memory.TryGetProperty("heap_used_mb", out _));
        Assert.True(memory.TryGetProperty("heap_total_mb", out _));
        Assert.True(memory.TryGetProperty("rss_mb", out _));
        Assert.True(body.TryGetProperty("checks", out var checks));
        Assert.True(checks.TryGetProperty("explorer", out var explorer));
        Assert.Equal("critical", explorer.GetProperty("criticality").GetString());
        Assert.True(explorer.TryGetProperty("latency_ms", out _));
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
        Assert.Equal("noncritical", json.RootElement.GetProperty("checks").GetProperty("database").GetProperty("criticality").GetString());
        Assert.Equal("unavailable", json.RootElement.GetProperty("checks").GetProperty("database").GetProperty("error").GetString());
        using var readiness = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
        using var publicJson = JsonDocument.Parse(await readiness.Content.ReadAsStringAsync());
        Assert.Equal("degraded", publicJson.RootElement.GetProperty("status").GetString());
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
        Assert.Equal(JsonValueKind.Object, json.RootElement.ValueKind);
        Assert.Equal(new[] { "reason", "status" }, json.RootElement.EnumerateObject()
            .Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("timeout", json.RootElement.GetProperty("reason").GetString());
        Assert.Equal("unhealthy", json.RootElement.GetProperty("status").GetString());
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
