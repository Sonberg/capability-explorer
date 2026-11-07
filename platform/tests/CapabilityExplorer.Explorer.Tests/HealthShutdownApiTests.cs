using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class HealthShutdownApiTests
{
    [Fact]
    public async Task Ready_returns_503_and_live_returns_200_during_real_drain()
    {
        var barrier = new ShutdownBarrier();
        using var factory = new HealthApiFactory(services => services.AddSingleton<IHostedService>(barrier));
        using var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);
        using (var liveBefore = await client.GetAsync("/health/live"))
            Assert.Equal(HttpStatusCode.OK, liveBefore.StatusCode);
        using (var readyBefore = await client.GetAsync("/health/ready"))
            Assert.Equal(HttpStatusCode.OK, readyBefore.StatusCode);

        var lifetime = factory.Services.GetRequiredService<IHostApplicationLifetime>();
        try
        {
            lifetime.StopApplication();
            await barrier.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
            Assert.False(lifetime.ApplicationStopped.IsCancellationRequested);
            using var ready = await client.GetAsync("/health/ready");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            using var live = await client.GetAsync("/health/live");
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        }
        finally
        {
            barrier.Release.TrySetResult();
        }
    }

    // Hold the real host in its pre-StopAsync lifecycle phase so TestServer cannot
    // be disposed before the drain assertions, regardless of scheduler speed.
    private sealed class ShutdownBarrier : IHostedLifecycleService
    {
        public TaskCompletionSource Stopping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task StoppingAsync(CancellationToken cancellationToken)
        {
            Stopping.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
