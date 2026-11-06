using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using CapabilityExplorer.Capabilities;
using CapabilityExplorer.Capabilities.Analysis;

namespace CapabilityExplorer.Cli;

internal sealed class AnalysisProcess(string root, FileRunStore store, AnalysisRun run, CancellationToken stop, IAnalysisContainerRuntime? containerRuntime = null) : IDisposable
{
    private readonly IAnalysisContainerRuntime _runtime = containerRuntime ?? new AnalysisEngineRuntime(root);
    private readonly SemaphoreSlim _runtimeGate = new(1,1);
    private readonly object _logGate = new();
    private readonly CancellationTokenSource _abort = new();
    private int _unconfirmedSpawns;
    public bool TerminationConfirmed => Volatile.Read(ref _unconfirmedSpawns) == 0;
    internal void MarkUncertainAcquisition() => Interlocked.Increment(ref _unconfirmedSpawns);
    public void Abort() => _abort.Cancel();
    public void Dispose() { _abort.Dispose(); _runtimeGate.Dispose(); }
    public async Task CleanupAfterAbort() => await _runtime.CleanupOwned(Label);
    private string Label => "ce-analyze=" + AnalysisWorkerCommand.HashText(root)[..16] + "-" + run.RunId + "-" + run.AttemptId;
    internal AnalysisTelemetryStage Measure(string stage, string? repositoryId = null, string? language = null) =>
        new(WriteTelemetry,run.RunId,run.AttemptId!,stage,repositoryId,language);

    internal void LogFailure(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        LogFailure(error.GetType().Name + ": " + error.Message);
    }

    internal void LogFailure(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var detail = message;
        foreach (var name in new[] { "GITHUB_TOKEN", "GH_TOKEN" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } secret)
                detail = detail.Replace(secret, "[redacted]", StringComparison.Ordinal);
        detail = Regex.Replace(detail, @"(?i)(https?://)[^/\s@]+@", "$1[redacted]@");
        detail = Regex.Replace(detail, @"(?i)(authorization:\s*(?:basic|bearer)\s+)\S+", "$1[redacted]");
        detail = Regex.Replace(detail, @"\b(?:gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,})\b", "[redacted]");
        detail = string.Concat(detail.Where(character => !char.IsControl(character) || character == ' '));
        if (detail.Length > 2048) detail = detail[..2048] + "...";
        WriteTelemetry("-- failure: " + detail + "\n");
    }

    private void WriteTelemetry(string line)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("analysis host requires Linux or macOS");
        var path = store.LogPath(run.RunId);
        var bytes = Encoding.UTF8.GetBytes(line);
        lock (_logGate)
        {
            AnalysisPreparation.RejectLinks(path);
            using var log = new FileStream(path,new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate, Access = FileAccess.Write, Share = FileShare.Read,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            });
            if (log.Length + bytes.Length > 16 * 1024 * 1024) return;
            log.Seek(0,SeekOrigin.End);
            log.Write(bytes);
        }
    }

    public void CheckCancellation()
    {
        if (stop.IsCancellationRequested) store.Cancel(run.RunId);
        if (stop.IsCancellationRequested || store.ReadRun(run.RunId).CancellationRequested)
            throw new OperationCanceledException("analysis cancelled before publication");
        if (_abort.IsCancellationRequested) throw new InvalidOperationException("a sibling preparation job failed; attempt aborted");
    }

    internal async Task<T> AcquireSource<T>(SourceSync sync, Func<CancellationToken, Task<T>> operation)
    {
        CheckCancellation();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop, _abort.Token);
        var monitoring = Monitor();
        async Task Monitor()
        {
            try
            {
                while (!cancel.IsCancellationRequested)
                {
                    CheckCancellation();
                    await Task.Delay(100, cancel.Token);
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            catch { cancel.Cancel(); throw; }
        }
        try
        {
            var result = await operation(cancel.Token);
            CheckCancellation();
            return result;
        }
        finally
        {
            cancel.Cancel();
            if (sync.ExecutionUncertain) MarkUncertainAcquisition();
            await monitoring;
        }
    }

    public async Task<string> Run(string executable, IEnumerable<string> arguments, IReadOnlyDictionary<string,string>? environment = null, bool capture = false, int captureLimit = 65536, Action<string>? captureError = null)
    {
        if (captureLimit is < 1 or > 1048576) throw new ArgumentOutOfRangeException(nameof(captureLimit));
        CheckCancellation();
        var argumentList = arguments.ToArray();
        var sandbox = executable == "scripts/sandbox";
        if (sandbox)
        {
            await _runtimeGate.WaitAsync();
            try { await _runtime.EnsureFrozen(); } finally { _runtimeGate.Release(); }
        }
        var resolvedExecutable = executable.Contains(Path.DirectorySeparatorChar) ? Path.GetFullPath(Path.Combine(root,executable)) : executable;
        var start = new ProcessStartInfo(resolvedExecutable) { WorkingDirectory = root, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var argument in argumentList) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "HOME", "TMPDIR", "DOCKER_HOST", "DOCKER_CONTEXT", "DOCKER_CONFIG", "SANDBOX_EXTRA_CA_FILE", "SANDBOX_NETWORK" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment["CE_CONTAINER_ENGINE"] = Environment.GetEnvironmentVariable("CE_CONTAINER_ENGINE") ?? "auto";
        start.Environment["SANDBOX_PASS_ENV"] = "";
        start.Environment["SANDBOX_SOURCES"] = "";
        var sourceIndex = Array.IndexOf(argumentList,"--source");
        if (sandbox && argumentList.Contains("--offline",StringComparer.Ordinal) && sourceIndex >= 0 && sourceIndex + 1 < argumentList.Length &&
            argumentList[sourceIndex + 1].StartsWith("/work/",StringComparison.Ordinal))
        {
            var source = Path.GetFullPath(Path.Combine(root,argumentList[sourceIndex + 1][6..]));
            var relative = Path.GetRelativePath(root,source);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../",StringComparison.Ordinal))
                throw new IOException("offline source escapes workspace");
            AnalysisPreparation.RejectLinks(source);
            start.Environment["SANDBOX_SOURCES"] = source;
        }
        start.Environment["SANDBOX_LABEL"] = Label;
        if (environment is not null) foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        if (sandbox) _runtime.ApplyEnvironment(start);
        var logPath = store.LogPath(run.RunId);
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("analysis host requires Linux or macOS");
        lock (_logGate)
        {
            AnalysisPreparation.RejectLinks(logPath);
            if (!File.Exists(logPath))
            {
                using var created = new FileStream(logPath,new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
            }
        }
        CheckCancellation();
        // Counts are monotonic for uncertain launches: sibling success can never clear another child's uncertainty.
        Interlocked.Increment(ref _unconfirmedSpawns);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("could not start preparation child");
        using var drain = new CancellationTokenSource();
        var stdout = Pump(child.StandardOutput, capture ? null : logPath, capture, captureLimit, drain.Token);
        var stderr = Pump(child.StandardError, capture ? null : logPath, captureError is not null, captureLimit, drain.Token);
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
            if (child.ExitCode != 0) throw new InvalidOperationException("preparation child failed with exit " + child.ExitCode);
            using var drainTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var output = await stdout.WaitAsync(drainTimeout.Token);
            var errors = await stderr.WaitAsync(drainTimeout.Token);
            captureError?.Invoke(errors);
            Interlocked.Decrement(ref _unconfirmedSpawns);
            CheckCancellation();
            return output;
        }
        catch
        {
            Abort();
            if (!child.HasExited) { try { child.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await child.WaitForExitAsync(cleanup.Token); await Task.WhenAll(stdout,stderr).WaitAsync(cleanup.Token); }
            catch (Exception cleanupError) when (cleanupError is OperationCanceledException or IOException or InvalidOperationException or ObjectDisposedException) { }
            if (sandbox) await _runtime.CleanupOwned(Label);
            throw;
        }
        finally { drain.Cancel(); }
    }

    private async Task<string> Pump(StreamReader reader, string? logPath, bool capture, int captureLimit, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        long count = 0;
        while (await reader.ReadAsync(buffer.AsMemory(),token) is var read && read != 0)
        {
            count += read;
            if (capture && count > captureLimit) throw new IOException("captured helper output exceeded limit");
            if (capture) result.Append(buffer, 0, read);
            if (logPath is not null)
            {
                var bytes = Encoding.UTF8.GetBytes(buffer,0,read);
                lock (_logGate)
                {
                    AnalysisPreparation.RejectLinks(logPath);
                    using var log = new FileStream(logPath,FileMode.Append,FileAccess.Write,FileShare.Read);
                    var remaining = 16 * 1024 * 1024 - log.Length;
                    if (remaining > 0) log.Write(bytes,0,(int)Math.Min(bytes.Length,remaining));
                }
            }
        }
        return result.ToString();
    }
}
