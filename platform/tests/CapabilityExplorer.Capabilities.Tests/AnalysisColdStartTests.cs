using CapabilityExplorer.Capabilities.Analysis;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class AnalysisColdStartTests
{
    [Fact]
    public async Task Concurrent_cold_start_publishes_complete_domain_and_admits_exactly_once()
    {
        var directory = Path.Combine(Path.GetTempPath().Replace("/var/", "/private/var/", StringComparison.Ordinal), "ce-cold-start-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var configuration = Path.Combine(directory, "capabilities.json");
            File.WriteAllText(configuration, """{"capabilities":[{"id":"sales","repositories":[]}]}""");
            for (var round = 0; round < 5; round++)
            {
                var root = Path.Combine(directory, "requests-" + round.ToString(System.Globalization.CultureInfo.InvariantCulture));
                using var ready = new CountdownEvent(32);
                using var start = new ManualResetEventSlim();
                var callers = Enumerable.Range(0, 32).Select(_ => Task.Factory.StartNew(() =>
                {
                    ready.Signal();
                    Assert.True(start.Wait(TimeSpan.FromSeconds(15)), "cold start barrier was not released");
                    return new FileRunStore(root).Admit("sales", configuration, null, new());
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
                try { Assert.True(ready.Wait(TimeSpan.FromSeconds(15)), "cold start callers did not reach barrier"); }
                finally { start.Set(); }
                var results = await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Single(results, result => result.Accepted);
                Assert.Equal(31, results.Count(result => !result.Accepted));
                Assert.Single(results.Select(result => result.Run.RunId).Distinct(StringComparer.Ordinal));
                var marker = File.ReadAllText(Path.Combine(root, "coordination-domain"));
                var expectedDomain = OperatingSystem.IsMacOS() ? "macos-native" : "linux:" + File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
                Assert.Equal(expectedDomain, marker);
                Assert.Empty(Directory.EnumerateFiles(root, ".coordination-domain.*.tmp"));
                Assert.Null(new FileRunStore(root).ReadBlockReason());
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
