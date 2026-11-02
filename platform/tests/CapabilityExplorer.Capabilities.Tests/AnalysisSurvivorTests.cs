using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using CapabilityExplorer.Capabilities.Analysis;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class AnalysisSurvivorTests
{
    private sealed record SurvivorEvidence(string RunId, string AttemptId, int ProcessId, long StartTicks, string Sentinel);

    [Fact]
    public async Task Owner_death_preserves_live_child_and_staging_and_blocks_execution()
    {
        var fixture = Environment.GetEnvironmentVariable("CE_ANALYSIS_SURVIVOR_ROOT");
        if (fixture is not null)
        {
            await OwnAndSpawn(fixture);
            return;
        }
        var directory = Path.Combine(Path.GetTempPath().Replace("/var/", "/private/var/", StringComparison.Ordinal), "ce-survivor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        SurvivorEvidence? survivor = null;
        Process? controller = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var root = Path.Combine(directory, "requests");
            var configuration = Path.Combine(directory, "capabilities.json");
            File.WriteAllText(configuration, """{"capabilities":[{"id":"sales"}]}""");
            var store = new FileRunStore(root);
            var admitted = store.Admit("sales", configuration, null, new()).Run;
            var nonce = Guid.NewGuid().ToString("N");
            var pipeName = "cs" + nonce[..12];
            using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            controller = StartController(root, pipeName, nonce);
            await server.WaitForConnectionAsync(timeout.Token);
            using var reader = new StreamReader(server);
            survivor = JsonSerializer.Deserialize<SurvivorEvidence>(await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("missing survivor evidence"))!;
            Assert.Equal(admitted.RunId, survivor.RunId);
            var attempt = new AttemptIdentity(survivor.RunId, survivor.AttemptId);
            var sentinel = Path.Combine(store.AttemptDirectory(attempt), "survivor-staging.txt");
            Assert.Equal(sentinel, survivor.Sentinel);
            Assert.Equal(nonce, await File.ReadAllTextAsync(sentinel, timeout.Token));
            using (var live = RequireOwnedSurvivor(survivor)) Assert.False(live.HasExited);
            Assert.Equal("running", store.ReadRun(admitted.RunId).State);
            // Deliberately kill only the owning controller. Its separately spawned process must survive.
            controller.Kill(entireProcessTree: false);
            await controller.WaitForExitAsync(timeout.Token);
            using (var live = RequireOwnedSurvivor(survivor)) Assert.False(live.HasExited);
            using var replacement = store.AcquireWorker();
            Assert.True(store.Recover(replacement).Blocked);
            Assert.Equal("unknown", store.ReadRun(admitted.RunId).State);
            Assert.Throws<InvalidOperationException>(() => store.ClaimNext(replacement));
            Assert.Equal(nonce, await File.ReadAllTextAsync(sentinel, timeout.Token));
            using (var live = RequireOwnedSurvivor(survivor)) Assert.False(live.HasExited);
        }
        finally
        {
            // Only clean processes whose identities came from this fixture's private pipe.
            if (controller is not null)
            {
                if (!controller.HasExited) controller.Kill(entireProcessTree: true);
                await controller.WaitForExitAsync();
                controller.Dispose();
            }
            if (survivor is not null)
            {
                try
                {
                    using var live = RequireOwnedSurvivor(survivor);
                    if (!live.HasExited) live.Kill(entireProcessTree: false);
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await live.WaitForExitAsync(cleanup.Token);
                }
                catch (ArgumentException) { /* The short-lived controlled process is already gone. */ }
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Process RequireOwnedSurvivor(SurvivorEvidence evidence)
    {
        var process = Process.GetProcessById(evidence.ProcessId);
        if (StartIdentity(process) != evidence.StartTicks)
        {
            process.Dispose();
            throw new InvalidOperationException("test survivor identity changed; refusing unrelated process cleanup");
        }
        return process;
    }

    private static long StartIdentity(Process process)
    {
        if (!OperatingSystem.IsLinux()) return process.StartTime.ToUniversalTime().Ticks;
        // Use raw kernel start ticks: independent .NET processes can estimate boot time differently.
        var stat = File.ReadAllText("/proc/" + process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/stat");
        var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
        return long.Parse(fields[19], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task OwnAndSpawn(string root)
    {
        var store = new FileRunStore(root);
        using var owner = store.AcquireWorker();
        var run = store.ClaimNext(owner) ?? throw new InvalidOperationException("missing test request");
        var sentinel = Path.Combine(store.AttemptDirectory(new(run.RunId, run.AttemptId!)), "survivor-staging.txt");
        await File.WriteAllTextAsync(sentinel, Environment.GetEnvironmentVariable("CE_ANALYSIS_SURVIVOR_NONCE"));
        // No repository code executes: the sole descendant is the OS sleep command controlled by this test.
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("exec sleep 120");
        using var survivor = Process.Start(start) ?? throw new InvalidOperationException("cannot start controlled survivor");
        var evidence = new SurvivorEvidence(run.RunId, run.AttemptId!, survivor.Id, StartIdentity(survivor), sentinel);
        using var pipe = new NamedPipeClientStream(".", Environment.GetEnvironmentVariable("CE_ANALYSIS_SURVIVOR_PIPE")!, PipeDirection.Out);
        await pipe.ConnectAsync(15000);
        using var writer = new StreamWriter(pipe) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(evidence));
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    private static Process StartController(string root, string pipe, string nonce)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add(typeof(AnalysisSurvivorTests).FullName + ".Owner_death_preserves_live_child_and_staging_and_blocks_execution");
        start.Environment["CE_ANALYSIS_SURVIVOR_ROOT"] = root;
        start.Environment["CE_ANALYSIS_SURVIVOR_PIPE"] = pipe;
        start.Environment["CE_ANALYSIS_SURVIVOR_NONCE"] = nonce;
        return Process.Start(start) ?? throw new InvalidOperationException("cannot start controlled owner");
    }
}
