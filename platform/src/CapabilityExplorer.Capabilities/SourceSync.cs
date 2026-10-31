using System.Diagnostics;

namespace CapabilityExplorer.Capabilities;

/// <summary>
/// Acquires capability sources read-only and resolves the revision to check. GitHub repositories become bare,
/// blob-less partial clones under the workspace: no checkout, no hooks, no submodules, no build. Local
/// repositories are only read. Nothing from a repository is executed.
/// </summary>
public sealed class SourceSync(string workspace, TimeSpan? timeout = null)
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(3);

    /// <summary>The directory git commands for this repository run against.</summary>
    public string GitDirectory(RepositorySource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind == SourceKind.Local)
        {
            return Path.GetFullPath(source.Location);
        }

        var path = new Uri(source.Location).AbsolutePath.Trim('/'); // owner/repo
        return Path.Combine(Path.GetFullPath(workspace), "github", path + ".git");
    }

    public async Task<CapabilitySnapshot> CheckAsync(CapabilityDefinition capability, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capability);
        var locked = new List<LockedRepository>();
        foreach (var source in capability.Repositories)
        {
            locked.Add(await LockAsync(source, cancellationToken));
        }

        return new CapabilitySnapshot(capability.Id, DateTimeOffset.UtcNow, locked);
    }

    public async Task<LockedRepository> LockAsync(RepositorySource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var dir = GitDirectory(source);
        try
        {
            if (source.Kind == SourceKind.GitHub)
            {
                await AcquireAsync(source, dir, cancellationToken);
            }
            else if (!(await GitAsync(dir, ["rev-parse", "--git-dir"], cancellationToken)).Ok)
            {
                return new LockedRepository(source.Id, source.Ref, null, null, null, $"{source.Location} is not a git repository (or is not mounted into the sandbox)");
            }

            var (ok, sha) = await GitAsync(dir, ["rev-parse", "--verify", "--quiet", $"{source.Ref}^{{commit}}"], cancellationToken);
            if (!ok)
            {
                return new LockedRepository(source.Id, source.Ref, null, null, null, $"ref '{source.Ref}' not found");
            }

            sha = sha.Trim();
            var (_, info) = await GitAsync(dir, ["show", "-s", "--format=%ct%n%s", sha], cancellationToken);
            var lines = info.Split('\n', 2);
            DateTimeOffset? time = long.TryParse(lines[0].Trim(), out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
            return new LockedRepository(source.Id, source.Ref, sha, lines.Length > 1 ? lines[1].Trim() : null, time, null);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            return new LockedRepository(source.Id, source.Ref, null, null, null, e.Message);
        }
    }

    private async Task AcquireAsync(RepositorySource source, string dir, CancellationToken cancellationToken)
    {
        if (Directory.Exists(dir))
        {
            var fetch = await GitAsync(dir, ["fetch", "--prune", "--tags", "--filter=blob:none", "origin", "+refs/heads/*:refs/heads/*"], cancellationToken);
            if (!fetch.Ok)
            {
                throw new InvalidOperationException($"fetch failed: {Last(fetch.Output)}");
            }

            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
        var clone = await GitAsync(null, ["clone", "--bare", "--filter=blob:none", "--no-recurse-submodules", source.Location, dir], cancellationToken);
        if (!clone.Ok)
        {
            throw new InvalidOperationException($"clone failed: {Last(clone.Output)}");
        }
    }

    private static string Last(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "unknown error";

    private async Task<(bool Ok, string Output)> GitAsync(string? directory, string[] args, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        // Never run repository-provided code or prompt for credentials.
        foreach (var a in new[] { "-c", "core.hooksPath=/dev/null", "-c", "protocol.file.allow=never", "-c", "protocol.ext.allow=never", "-c", "core.fsmonitor=false" })
        {
            start.ArgumentList.Add(a);
        }

        if (directory is not null)
        {
            start.ArgumentList.Add("-C");
            start.ArgumentList.Add(directory);
        }

        foreach (var a in args)
        {
            start.ArgumentList.Add(a);
        }

        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_ASKPASS"] = "/bin/false";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start git");
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"git {args[0]} timed out after {_timeout.TotalSeconds:0}s");
        }

        return (process.ExitCode == 0, process.ExitCode == 0 ? await stdout : await stderr + await stdout);
    }
}
