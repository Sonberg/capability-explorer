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
            Assert.Equal(id.ToString("D"), root.GetProperty("requestId").GetString());
            Assert.Equal(TimeSpan.Zero, DateTimeOffset.Parse(root.GetProperty("timestamp").GetString()!, CultureInfo.InvariantCulture).Offset);
            Assert.True(root.GetProperty("uptimeSeconds").GetDouble() >= 0);
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("version").GetString()));
        }
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
        Assert.True(json.RootElement.GetProperty("ready").GetBoolean());
        Assert.Equal("unavailable", json.RootElement.GetProperty("results")[0].GetProperty("error").GetString());
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
        Assert.Equal("timeout", json.RootElement.GetProperty("results")[0].GetProperty("error").GetString());
        Assert.Equal("healthy", json.RootElement.GetProperty("results")[1].GetProperty("status").GetString());
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
        Assert.Equal(HealthEndpointOptions.MaximumChecks, json.RootElement.GetProperty("results").GetArrayLength());
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
        Assert.Contains("# TYPE explorer_http_requests_total counter", text, StringComparison.Ordinal);
        Assert.Contains("explorer_http_request_duration_seconds_bucket{", text, StringComparison.Ordinal);
        Assert.Contains("explorer_dependency_check_duration_seconds_bucket{", text, StringComparison.Ordinal);
        Assert.Contains("explorer_process_uptime_seconds ", text, StringComparison.Ordinal);
        Assert.Contains("explorer_process_resident_memory_bytes ", text, StringComparison.Ordinal);
    }

    private sealed class FakeHealthCheck(string name, bool critical, Func<CancellationToken, Task> check) : IHealthDependencyCheck
    {
        public string Name { get; } = name;
        public bool Critical { get; } = critical;
        public Task CheckAsync(CancellationToken cancellationToken) => check(cancellationToken);
    }

    private static void AssertNoStore(string? value)
    {
        Assert.NotNull(value);
        Assert.Contains("no-store", value, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-cache", value, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("must-revalidate", value, StringComparison.OrdinalIgnoreCase);
    }
}
