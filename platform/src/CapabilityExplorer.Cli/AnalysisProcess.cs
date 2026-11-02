using System.Diagnostics;
using CapabilityExplorer.Capabilities.Analysis;

namespace CapabilityExplorer.Cli;

internal sealed class AnalysisProcess(string root, FileRunStore store, AnalysisRun run, CancellationToken stop)
{
    public bool TerminationConfirmed { get; private set; } = true;
    public void CheckCancellation()
    {
        if (stop.IsCancellationRequested) store.Cancel(run.RunId);
        if (stop.IsCancellationRequested || store.ReadRun(run.RunId).CancellationRequested)
            throw new OperationCanceledException("analysis cancelled before publication");
    }

    public async Task<string> Run(string executable, IEnumerable<string> arguments, IReadOnlyDictionary<string,string>? environment = null, bool capture = false)
    {
        CheckCancellation();
        var start = new ProcessStartInfo(executable) { WorkingDirectory = root, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        // Host runtime selectors are required by the qualified Docker adapter, never forwarded into extraction.
        foreach (var name in new[] { "PATH", "HOME", "TMPDIR", "DOCKER_HOST", "DOCKER_CONTEXT", "DOCKER_CONFIG", "SANDBOX_EXTRA_CA_FILE", "SANDBOX_NETWORK" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment["CE_CONTAINER_ENGINE"] = "docker";
        start.Environment["SANDBOX_PASS_ENV"] = "";
        start.Environment["SANDBOX_SOURCES"] = "";
        start.Environment["SANDBOX_LABEL"] = "ce-analyze=" + AnalysisWorkerCommand.HashText(root)[..16] + "-" + run.RunId + "-" + run.AttemptId;
        if (environment is not null) foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        var logPath = store.LogPath(run.RunId);
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("analysis host requires Linux or macOS");
        if (!File.Exists(logPath))
        {
            using var created = new FileStream(logPath,new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
        }
        using var log = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read));
        CheckCancellation();
        // ClaimNext durably persisted attempt intent before this method can be reached.
        using var child = Process.Start(start) ?? throw new InvalidOperationException("could not start preparation child");
        TerminationConfirmed = false;
        var stdout = Pump(child.StandardOutput, capture ? null : log, capture);
        var stderr = Pump(child.StandardError, capture ? null : log, false);
        try
        {
            child.StandardInput.Close();
            while (!child.HasExited)
            {
                CheckCancellation();
                if (stdout.IsFaulted || stderr.IsFaulted) throw new IOException("child output capture failed");
                await Task.Delay(100);
            }
            await child.WaitForExitAsync();
            var output = await stdout;
            await stderr;
            TerminationConfirmed = executable != "scripts/sandbox" || child.ExitCode == 0;
            if (child.ExitCode != 0) throw new InvalidOperationException("preparation child failed with exit " + child.ExitCode);
            CheckCancellation();
            return output;
        }
        catch
        {
            // Killing the controller cannot prove Docker/container descendants terminated. Keep unknown.
            if (!child.HasExited) { try { child.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await child.WaitForExitAsync(cleanup.Token); await Task.WhenAll(stdout,stderr).WaitAsync(cleanup.Token); }
            catch (Exception cleanupError) when (cleanupError is OperationCanceledException or IOException or InvalidOperationException or ObjectDisposedException) { }
            throw;
        }
    }

    private static async Task<string> Pump(StreamReader reader, StreamWriter? log, bool capture)
    {
        var result = new System.Text.StringBuilder();
        var buffer = new char[4096];
        long count = 0;
        while (await reader.ReadAsync(buffer) is var read && read != 0)
        {
            count += read;
            if (capture && count > 65536) throw new IOException("credential helper output exceeded limit");
            if (capture) result.Append(buffer, 0, read);
            if (log is not null && count <= 16 * 1024 * 1024)
            { lock (log) { if (log.BaseStream.Length < 16 * 1024 * 1024) { log.Write(buffer, 0, read); log.Flush(); } } }
        }
        return result.ToString();
    }
}
