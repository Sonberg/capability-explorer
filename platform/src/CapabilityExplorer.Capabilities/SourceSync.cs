using System.Diagnostics;

namespace CapabilityExplorer.Capabilities;

public sealed record ExportResult(int Files, int Skipped);

/// <summary>
/// Acquires capability sources read-only and resolves the revision to check. GitHub repositories become bare,
/// blob-less partial clones under the workspace: no checkout, no hooks, no submodules, no build. Local
/// repositories are only read. Nothing from a repository is executed.
/// </summary>
public sealed partial class SourceSync(string workspace, TimeSpan? timeout = null, GitHubAuth? auth = null, ExportLimits? exportLimits = null)
{
    private readonly ExportLimits _exportLimits = exportLimits ?? new ExportLimits();
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(3);

    public GitHubAuth Auth { get; } = auth ?? GitHubAuth.None;
    public bool ExecutionUncertain { get; private set; }

    /// <summary>The directory git commands for this repository run against.</summary>
    public string GitDirectory(RepositorySource source)
    {
        SourceParser.Validate(source);
        if (source.Kind == SourceKind.Local)
        {
            var local = Path.GetFullPath(ExpandHome(source.Location));
            SourceSafety.RejectLinks(local);
            return local;
        }

        var path = new Uri(source.Location).AbsolutePath.Trim('/'); // owner/repo
        var target = Path.Combine(Path.GetFullPath(workspace), "github", path + ".git");
        SourceSafety.RejectLinks(target);
        return target;
    }

    private static bool IsBareRepository(string path) =>
        File.Exists(Path.Combine(path, "HEAD")) &&
        Directory.Exists(Path.Combine(path, "objects")) &&
        Directory.Exists(Path.Combine(path, "refs")) &&
        !Directory.Exists(Path.Combine(path, ".git"));

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
        var bare = source.Kind == SourceKind.GitHub || IsBareRepository(dir);
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
            else if (!(await GitAsync(dir, ["rev-parse", "--git-dir"], cancellationToken, bare: bare)).Ok)
            {
                return new LockedRepository(source.Id, source.Ref, null, null, null, $"{dir} is not a git repository");
            }

            var (ok, sha) = await GitAsync(dir, ["rev-parse", "--verify", "--quiet", "--end-of-options", $"{source.Ref}^{{commit}}"], cancellationToken, bare: bare);
            if (!ok)
            {
                return new LockedRepository(source.Id, source.Ref, null, null, null, $"ref '{source.Ref}' not found");
            }

            sha = sha.Trim();
            var (_, info) = await GitAsync(dir, ["show", "-s", "--format=%ct%n%s", sha], cancellationToken, bare: bare);
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
            string[] fetchArgs = ["fetch", "--prune", "--tags", "--filter=blob:none", source.Location, "+refs/heads/*:refs/heads/*"];
            var fetch = await GitAsync(dir, fetchArgs, cancellationToken, bare: true);
            if (!fetch.Ok && Auth.Configured && GitHubAuth.IsAuthError(fetch.Output))
            {
                fetch = await GitAsync(dir, fetchArgs, cancellationToken, anonymous: true, bare: true);
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

    /// <summary>
    /// Writes the tree of one commit to <paramref name="destination"/> (like <c>git archive</c>): regular files and
    /// directories only, no symlinks, nothing outside the destination, oversized files skipped. Missing blobs of a
    /// blob-less clone are fetched first in one batch.
    /// </summary>
    public async Task<ExportResult> ExportAsync(RepositorySource source, string commitSha, string destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (!CommitPattern().IsMatch(commitSha))
        {
            throw new ArgumentException($"not a commit sha: {commitSha}", nameof(commitSha));
        }

        _exportLimits.Validate();
        var dir = GitDirectory(source);
        destination = SourceSafety.PrepareDestination(destination);
        if (source.Kind == SourceKind.GitHub)
        {
            await PrefetchAsync(dir, source.Location, commitSha, cancellationToken);
        }

        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        var bare = source.Kind == SourceKind.GitHub || IsBareRepository(dir);
        var start = Start(dir, ["archive", "--format=tar", commitSha], anonymous: false, bare: bare);
        start.Environment["GIT_NO_LAZY_FETCH"] = "1"; // everything needed was fetched above
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout * 4);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start git");
        var stderr = SourceSafety.ReadBoundedAsync(process.StandardError, process, 64 * 1024, timeout.Token);
        int files = 0, skipped = 0, entries = 0;
        long bytes = 0;
        try
        {
            var reader = new System.Formats.Tar.TarReader(process.StandardOutput.BaseStream);
            while (await reader.GetNextEntryAsync(copyData: false, timeout.Token) is { } entry)
            {
                if (++entries > _exportLimits.Entries || entry.Length < 0 || entry.Length > _exportLimits.Bytes - bytes)
                {
                    throw new InvalidOperationException("git archive exceeded total export entry or byte limits");
                }
                bytes += entry.Length;
                var target = Path.GetFullPath(Path.Combine(destination, entry.Name));
                if (!target.StartsWith(root, StringComparison.Ordinal))
                {
                    skipped++;
                    continue;
                }

                SourceSafety.RejectLinks(target);
                switch (entry.EntryType)
                {
                    case System.Formats.Tar.TarEntryType.GlobalExtendedAttributes:
                        break; // git archive's pax header (the commit id), not a file
                    case System.Formats.Tar.TarEntryType.Directory:
                        Directory.CreateDirectory(target);
                        break;
                    case System.Formats.Tar.TarEntryType.RegularFile or System.Formats.Tar.TarEntryType.V7RegularFile when entry.Length <= MaxExportedFileBytes:
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        await using (var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            if (entry.DataStream is { } data) // null for empty files
                            {
                                await data.CopyToAsync(file, timeout.Token);
                            }
                        }

                        files++;
                        break;
                    default:
                        skipped++; // symlinks, submodules (gitlinks are not archived), oversized files
                        break;
                }
            }

            // TarReader stops at the end marker before Git's block padding ends.
            // Drain the pipe before waiting, or a small pipe can block Git's exit.
            await process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            ExecutionUncertain = true;
            SourceSafety.Kill(process);
            throw new InvalidOperationException($"git archive timed out after {(_timeout * 4).TotalSeconds:0}s");
        }

        catch
        {
            ExecutionUncertain = true;
            SourceSafety.Kill(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git archive failed: {Last(await stderr)}");
        }

        await stderr; // propagate bounded stderr failures even when Git exits successfully
        return new ExportResult(files, skipped);
    }

    private const long MaxExportedFileBytes = 8L * 1024 * 1024;

    [System.Text.RegularExpressions.GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial System.Text.RegularExpressions.Regex CommitPattern();

    /// <summary>Fetches the blobs a commit's tree needs in one request, the way git's own lazy fetch does per object.</summary>
    private async Task PrefetchAsync(string dir, string location, string commitSha, CancellationToken cancellationToken)
    {
        var (ok, listing) = await GitAsync(dir, ["rev-list", "--objects", "--missing=print", commitSha], cancellationToken, bare: true);
        if (!ok)
        {
            throw new InvalidOperationException($"commit {commitSha[..12]} is not in the clone: {Last(listing)}");
        }

        var missing = listing.Split('\n').Where(l => l.StartsWith('?')).Select(l => l[1..].Trim()).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        string[] fetchArgs = ["-c", "fetch.negotiationAlgorithm=noop", "fetch", location, "--no-tags", "--no-write-fetch-head", "--recurse-submodules=no", "--filter=blob:none", "--stdin"];
        var input = string.Join('\n', missing) + "\n";
        var fetch = await GitAsync(dir, fetchArgs, cancellationToken, input: input, bare: true);
        if (!fetch.Ok && Auth.Configured && GitHubAuth.IsAuthError(fetch.Output))
        {
            fetch = await GitAsync(dir, fetchArgs, cancellationToken, anonymous: true, input: input, bare: true);
        }

        if (!fetch.Ok)
        {
            throw new InvalidOperationException($"fetching {missing.Count} file(s) of {commitSha[..12]} failed: {Auth.Explain(Last(fetch.Output))}");
        }
    }

    private ProcessStartInfo Start(string? directory, string[] args, bool anonymous, bool bare = false)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        // Never run repository-provided code or prompt for credentials.
        foreach (var a in new[] { "-c", "core.hooksPath=/dev/null", "-c", "protocol.file.allow=never", "-c", "protocol.ext.allow=never", "-c", "core.fsmonitor=false", "-c", "credential.helper=", "-c", "protocol.allow=never", "-c", "protocol.https.allow=always", "-c", "http.followRedirects=false", "-c", "http.sslVerify=true", "-c", "core.sshCommand=/bin/false" })
        {
            start.ArgumentList.Add(a);
        }

        if (directory is not null)
        {
            if (bare)
            {
                start.ArgumentList.Add("--git-dir");
                start.ArgumentList.Add(directory);
            }
            else
            {
                start.ArgumentList.Add("-C");
                start.ArgumentList.Add(directory);
            }
        }

        foreach (var a in args)
        {
            start.ArgumentList.Add(a);
        }

        start.Environment["GIT_SSL_NO_VERIFY"] = "false";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        start.Environment["GIT_NO_LAZY_FETCH"] = "1";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_ASKPASS"] = "/bin/false";
        if (!anonymous)
        {
            Auth.Apply(start);
        }

        return start;
    }

    private async Task<(bool Ok, string Output)> GitAsync(string? directory, string[] args, CancellationToken cancellationToken, bool anonymous = false, string? input = null, bool bare = false)
    {
        var start = Start(directory, args, anonymous, bare);
        start.RedirectStandardInput = input is not null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start git");
        var stdout = SourceSafety.ReadBoundedAsync(process.StandardOutput, process, 8 * 1024 * 1024, timeout.Token);
        var stderr = SourceSafety.ReadBoundedAsync(process.StandardError, process, 64 * 1024, timeout.Token);
        try
        {
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            var error = await stderr;
            return (process.ExitCode == 0, process.ExitCode == 0 ? output : error + output);
        }
        catch (OperationCanceledException)
        {
            ExecutionUncertain = true;
            SourceSafety.Kill(process);
            throw new InvalidOperationException($"git {args[0]} timed out after {_timeout.TotalSeconds:0}s");
        }
        catch
        {
            ExecutionUncertain = true;
            SourceSafety.Kill(process);
            throw;
        }
    }
}
