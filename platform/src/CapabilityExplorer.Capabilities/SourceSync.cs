using System.Diagnostics;

namespace CapabilityExplorer.Capabilities;

/// <summary>
/// Acquires capability sources read-only and resolves the revision to check. GitHub repositories become bare,
/// blob-less partial clones under the workspace: no checkout, no hooks, no submodules, no build. Local
/// repositories are only read. Nothing from a repository is executed.
/// </summary>
public sealed class SourceSync(string workspace, TimeSpan? timeout = null, GitHubAuth? auth = null)
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(3);

    public GitHubAuth Auth { get; } = auth ?? GitHubAuth.None;

    /// <summary>The directory git commands for this repository run against.</summary>
    public string GitDirectory(RepositorySource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind == SourceKind.Local)
        {
            return Path.GetFullPath(ExpandHome(source.Location));
        }

        var path = new Uri(source.Location).AbsolutePath.Trim('/'); // owner/repo
        return Path.Combine(Path.GetFullPath(workspace), "github", path + ".git");
    }

    /// <summary>
    /// Expands a leading ~ to the user's home on the machine that owns the paths. In the sandbox that is the host's
    /// home (CE_HOST_HOME, forwarded by `make explore`), not the container's.
    /// </summary>
    public static string ExpandHome(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path != "~" && !path.StartsWith("~/", StringComparison.Ordinal))
        {
            return path;
        }

        var home = Environment.GetEnvironmentVariable("CE_HOST_HOME") is { Length: > 0 } host ? host : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path == "~" ? home : Path.Combine(home, path[2..]);
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
            else if (!Directory.Exists(dir))
            {
                return new LockedRepository(source.Id, source.Ref, null, null, null,
                    $"{dir} is not visible to the explorer. Local repositories in capabilities.json are mounted read-only when `make explore` starts: restart it (or add SOURCES={Path.GetDirectoryName(dir)})");
            }
            else if (!(await GitAsync(dir, ["rev-parse", "--git-dir"], cancellationToken)).Ok)
            {
                return new LockedRepository(source.Id, source.Ref, null, null, null, $"{dir} is not a git repository");
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
            string[] fetchArgs = ["fetch", "--prune", "--tags", "--filter=blob:none", "origin", "+refs/heads/*:refs/heads/*"];
            var fetch = await GitAsync(dir, fetchArgs, cancellationToken);
            if (!fetch.Ok && Auth.Configured && GitHubAuth.IsAuthError(fetch.Output))
            {
                fetch = await GitAsync(dir, fetchArgs, cancellationToken, anonymous: true);
            }

            if (!fetch.Ok)
            {
                throw new InvalidOperationException($"fetch failed: {Auth.Explain(Last(fetch.Output))}");
            }

            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
        string[] cloneArgs = ["clone", "--bare", "--filter=blob:none", "--no-recurse-submodules", source.Location, dir];
        var clone = await GitAsync(null, cloneArgs, cancellationToken);
        if (!clone.Ok && Auth.Configured && GitHubAuth.IsAuthError(clone.Output))
        {
            // The token was refused (wrong account, expired, not granted this repository). Public repositories
            // must still work, so try once without it before reporting.
            clone = await GitAsync(null, cloneArgs, cancellationToken, anonymous: true);
            if (!clone.Ok && GitHubAuth.IsAuthError(clone.Output))
            {
                throw new InvalidOperationException(
                    $"clone failed: not readable with the token from {Auth.Source} nor anonymously — the repository does not exist, or it is private and that token has no Contents: read access to it");
            }
        }

        if (!clone.Ok)
        {
            throw new InvalidOperationException($"clone failed: {Auth.Explain(Last(clone.Output))}");
        }
    }

    private static string Last(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "unknown error";

    private async Task<(bool Ok, string Output)> GitAsync(string? directory, string[] args, CancellationToken cancellationToken, bool anonymous = false)
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
        if (!anonymous)
        {
            Auth.Apply(start);
        }

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
