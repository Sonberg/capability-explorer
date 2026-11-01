using System.Diagnostics;

namespace CapabilityExplorer.Explorer;

/// <summary>Read-only access to git objects of the analysed repositories. Never checks out or runs code.</summary>
public sealed class GitSource(Func<string, string?> locate, CapabilityExplorer.Capabilities.GitHubAuth? auth = null)
{
    /// <summary>Repositories found as &lt;directory&gt;/&lt;id&gt; (the fixture layout).</summary>
    public GitSource(string repositoriesDirectory)
        : this(id => IsSafeSegment(id) ? Path.Combine(repositoriesDirectory, id) : null)
    {
    }

    private const int MaxCachedFiles = 16;
    private readonly Dictionary<(string, string, string), string?> _files = new();
    private readonly object _cacheLock = new();

    public bool HasRepository(string repositoryId) => Locate(repositoryId) is not null;

    /// <summary>A git directory (working copy or bare) for the repository, or null.</summary>
    private string? Locate(string repositoryId) =>
        IsSafeSegment(repositoryId) && locate(repositoryId) is { } dir && (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, "HEAD"))) ? dir : null;

    public string? ReadFile(string repositoryId, string commitSha, string path) =>
        ReadFileAsync(repositoryId, commitSha, path).GetAwaiter().GetResult();

    public async Task<string?> ReadFileAsync(string repositoryId, string commitSha, string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Commit(commitSha) || !SafePath(path)) return null;
        var key = (repositoryId, commitSha, path);
        lock (_cacheLock) { if (_files.TryGetValue(key, out var cached)) return cached; }
        var (ok, output) = await GitAsync(repositoryId, cancellationToken, "show", $"{commitSha}:{path}");
        var result = ok ? output.Replace("\r\n", "\n", StringComparison.Ordinal) : null;
        lock (_cacheLock)
        {
            if (_files.Count >= MaxCachedFiles) _files.Clear();
            _files[key] = result;
        }
        return result;
    }

    private static bool Commit(string value) => value.Length is 40 or 64 && value.All(char.IsAsciiHexDigit);
    private static bool SafePath(string value) => value.Length is > 0 and <= 4096 && !value.StartsWith('/')
        && !value.Contains('\\') && !value.Any(char.IsControl) && !value.Split('/').Any(p => p is ".." or "." or "");

    public DateTimeOffset? CommitTime(string repositoryId, string commitSha)
    {
        if (!Commit(commitSha)) return null;
        var (ok, output) = Git(repositoryId, "show", "-s", "--format=%ct", commitSha, "--");
        return ok && long.TryParse(output.Trim(), out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
    }

    public IReadOnlyList<string> Tags(string repositoryId, string commitSha)
    {
        if (!Commit(commitSha)) return [];
        var (ok, output) = Git(repositoryId, "tag", "--points-at", commitSha);
        return ok ? output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order(StringComparer.Ordinal).ToList() : [];
    }

    public string? Subject(string repositoryId, string commitSha)
    {
        if (!Commit(commitSha)) return null;
        var (ok, output) = Git(repositoryId, "show", "-s", "--format=%s", commitSha, "--");
        return ok ? output.Trim() : null;
    }

    internal static bool IsSafeSegment(string s) => s.Length is > 0 and <= 200 && !s.Any(char.IsControl) && !s.Contains('/', StringComparison.Ordinal) && !s.Contains('\\', StringComparison.Ordinal) && s != "." && s != "..";

    private (bool Ok, string Output) Git(string repositoryId, params string[] args) =>
        GitAsync(repositoryId, CancellationToken.None, args).GetAwaiter().GetResult();

    private async Task<(bool Ok, string Output)> GitAsync(string repositoryId, CancellationToken cancellationToken, params string[] args)
    {
        if (Locate(repositoryId) is not { } directory)
        {
            return (false, "");
        }

        _ = auth; // Retained constructor compatibility; source browsing never forwards these credentials.
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        // Source browsing is strictly offline. The authorized analysis/export step prefetches blobs.
        // Local repository configuration must not execute hooks, credential or transport helpers.
        // Provenance binds the original object ID, never local replacement refs.
        start.ArgumentList.Add("--no-replace-objects");
        foreach (var setting in new[] { "core.hooksPath=/dev/null", "core.fsmonitor=false", "credential.helper=",
            "protocol.allow=never", "protocol.file.allow=never", "protocol.ext.allow=never", "protocol.https.allow=never",
            "http.followRedirects=false", "http.sslVerify=true", "core.sshCommand=/bin/false" })
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(setting);
        }
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        start.Environment["GIT_NO_LAZY_FETCH"] = "1";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_ASKPASS"] = "/bin/false";
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(directory);
        foreach (var a in args)
        {
            start.ArgumentList.Add(a);
        }

        using var process = Process.Start(start);
        if (process is null)
        {
            return (false, "");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var output = ReadBounded(process.StandardOutput, timeout.Token);
            var error = ReadBounded(process.StandardError, timeout.Token);
            await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token));
            return (process.ExitCode == 0, await output);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            cancellationToken.ThrowIfCancellationRequested();
            return (false, "");
        }
    }
    private static async Task<string> ReadBounded(StreamReader reader, CancellationToken ct)
    {
        var result = new System.Text.StringBuilder();
        var buffer = new char[8192];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            if (result.Length + count > 2 * 1024 * 1024) throw new IOException("Git output exceeds source limit");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}
