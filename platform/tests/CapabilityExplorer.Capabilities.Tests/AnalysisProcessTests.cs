using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using CapabilityExplorer.Capabilities.Analysis;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class AnalysisProcessTests
{
    [Fact]
    public async Task Worker_lock_excludes_an_independent_process_and_process_death_blocks_reuse()
    {
        var childRoot = Environment.GetEnvironmentVariable("CE_ANALYSIS_LOCK_PROBE");
        if (childRoot is not null)
        {
            var childStore = new FileRunStore(childRoot);
            if (Environment.GetEnvironmentVariable("CE_ANALYSIS_LOCK_MODE") == "contend")
            {
                Assert.Throws<IOException>(() => childStore.AcquireWorker());
                return;
            }
            using var owned = childStore.AcquireWorker();
            Assert.NotNull(childStore.ClaimNext(owned));
            using var pipe = new NamedPipeClientStream(".", Environment.GetEnvironmentVariable("CE_ANALYSIS_LOCK_PIPE")!, PipeDirection.Out);
            await pipe.ConnectAsync(15000);
            pipe.WriteByte(1);
            pipe.Flush();
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return;
        }
        var directory = Path.Combine(Path.GetTempPath().Replace("/var/", "/private/var/", StringComparison.Ordinal), "ce-analysis-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var root = Path.Combine(directory, "requests");
            var store = new FileRunStore(root);
            var configuration = Path.Combine(directory, "capabilities.json");
            File.WriteAllText(configuration, """{"capabilities":[{"id":"sales"}]}""");
            store.Admit("sales", configuration, null, new());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using (var owner = store.AcquireWorker())
            {
                using var contender = Start(root, "contend", null);
                await contender.WaitForExitAsync(timeout.Token);
                Assert.Equal(0, contender.ExitCode);
                Assert.Equal("queued", store.ReadLatest("sales")!.State);
            }
            var pipeName = "ce" + Guid.NewGuid().ToString("N")[..12];
            using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            using (var doomed = Start(root, "own", pipeName))
            {
                try
                {
                    await server.WaitForConnectionAsync(timeout.Token);
                    var signal = new byte[1];
                    Assert.Equal(1, await server.ReadAsync(signal, timeout.Token));
                    Assert.Equal(1, signal[0]);
                    Assert.Equal("running", store.ReadLatest("sales")!.State);
                }
                finally
                {
                    if (!doomed.HasExited) doomed.Kill(true);
                    await doomed.WaitForExitAsync(timeout.Token);
                }
            }
            using var replacement = store.AcquireWorker();
            Assert.True(store.Recover(replacement).Blocked);
            Assert.Equal("unknown", store.ReadLatest("sales")!.State);
            Assert.Throws<InvalidOperationException>(() => store.ClaimNext(replacement));
        }
        finally { Directory.Delete(directory, true); }
    }
    private static Process Start(string root, string mode, string? pipe)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add(typeof(AnalysisProcessTests).FullName + ".Worker_lock_excludes_an_independent_process_and_process_death_blocks_reuse");
        start.Environment["CE_ANALYSIS_LOCK_PROBE"] = root;
        start.Environment["CE_ANALYSIS_LOCK_MODE"] = mode;
        start.Environment["CE_ANALYSIS_LOCK_PIPE"] = pipe;
        return Process.Start(start) ?? throw new InvalidOperationException("cannot start lifecycle proof process");
    }
}
