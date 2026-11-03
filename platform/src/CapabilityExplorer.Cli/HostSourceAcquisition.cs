using System.Diagnostics;
using System.Text;
using CapabilityExplorer.Capabilities;
using CapabilityExplorer.Capabilities.Analysis;

namespace CapabilityExplorer.Cli;

internal static class HostSourceAcquisition
{
    internal static async Task<GitHubAuth> ResolveAuth(string root, string mode, CancellationToken cancellationToken)
    {
        if (mode is not ("auto" or "git" or "none")) throw new ArgumentException("invalid GitHub authentication policy");
        var start = new ProcessStartInfo(Path.Combine(root, "scripts/github-token"))
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(mode);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("credential helper could not start");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        async Task<string> Bounded(StreamReader reader)
        {
            var result = new StringBuilder();
            var buffer = new char[1024];
            while (await reader.ReadAsync(buffer, deadline.Token) is var length && length > 0)
            {
                if (result.Length + length > 16384) throw new InvalidDataException("credential helper response exceeds bounds");
                result.Append(buffer, 0, length);
            }
            return result.ToString();
        }
        try
        {
            var output = Bounded(process.StandardOutput);
            var error = Bounded(process.StandardError);
            await process.WaitForExitAsync(deadline.Token);
            var token = await output;
            await error;
            if (process.ExitCode != 0) throw new InvalidOperationException("credential helper failed");
            return GitHubAuth.FromToken(token.Trim(), "host credential helper");
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }

    internal static async Task ExecuteCheck(string root, FileRunStore store, WorkerOwnership owner, SourceCheck check, CancellationToken stop)
    {
        var attempt = new AttemptIdentity(check.RequestId, check.AttemptId!);
        SourceSync? sync = null;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var polling = Poll();
        async Task Poll()
        {
            try
            {
                while (!cancel.IsCancellationRequested)
                {
                    if (store.ReadCheck(check.RequestId).CancellationRequested) { cancel.Cancel(); break; }
                    await Task.Delay(100, cancel.Token);
                }
            }
            catch (OperationCanceledException) { }
        }
        try
        {
            var auth = await ResolveAuth(root, check.GitHubAuth, cancel.Token);
            using var definitions = CapabilityStore.Open(check.ConfigurationPath, Path.Combine(root, ".sandbox-cache/sources/snapshots.json"), loadSnapshots: false);
            var definition = definitions.Find(check.CapabilityId) ?? throw new InvalidDataException("accepted capability is missing");
            sync = new SourceSync(Path.Combine(root, ".sandbox-cache/sources"), auth: auth);
            await AnalysisPreparation.SourceAcquisitionGate.WaitAsync(cancel.Token);
            CapabilitySnapshot snapshot;
            try { snapshot = await sync.CheckAsync(definition, cancel.Token); }
            finally { AnalysisPreparation.SourceAcquisitionGate.Release(); }
            cancel.Token.ThrowIfCancellationRequested();
            if (sync.ExecutionUncertain) throw new InvalidOperationException("source acquisition termination is uncertain");
            store.CompleteCheck(owner, attempt, snapshot);
        }
        catch
        {
            store.FailCheck(owner, attempt, sync is not null && !sync.ExecutionUncertain && !cancel.IsCancellationRequested);
            Console.Error.WriteLine("source check: acquisition did not complete");
        }
        finally { cancel.Cancel(); await polling; }
    }
}
