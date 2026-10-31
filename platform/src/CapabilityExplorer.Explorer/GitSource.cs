using System.Collections.Concurrent;
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

    private readonly ConcurrentDictionary<(string, string, string), string?> _files = new();

    public bool HasRepository(string repositoryId) => Locate(repositoryId) is not null;

    /// <summary>A git directory (working copy or bare) for the repository, or null.</summary>
    private string? Locate(string repositoryId) =>
        locate(repositoryId) is { } dir && (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, "HEAD"))) ? dir : null;

    public string? ReadFile(string repositoryId, string commitSha, string path) =>
        _files.GetOrAdd((repositoryId, commitSha, path), key =>
        {
            var (ok, output) = Git(key.Item1, "show", $"{key.Item2}:{key.Item3}");
            return ok ? output.Replace("\r\n", "\n", StringComparison.Ordinal) : null;
        });

    public DateTimeOffset? CommitTime(string repositoryId, string commitSha)
    {
        var (ok, output) = Git(repositoryId, "show", "-s", "--format=%ct", commitSha);
        return ok && long.TryParse(output.Trim(), out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
    }

    public IReadOnlyList<string> Tags(string repositoryId, string commitSha)
    {
        var (ok, output) = Git(repositoryId, "tag", "--points-at", commitSha);
        return ok ? output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order(StringComparer.Ordinal).ToList() : [];
    }

    public string? Subject(string repositoryId, string commitSha)
    {
        var (ok, output) = Git(repositoryId, "show", "-s", "--format=%s", commitSha);
        return ok ? output.Trim() : null;
    }

    internal static bool IsSafeSegment(string s) => s.Length > 0 && !s.Contains('/', StringComparison.Ordinal) && !s.Contains('\\', StringComparison.Ordinal) && s != "." && s != "..";

    private (bool Ok, string Output) Git(string repositoryId, params string[] args)
    {
        if (Locate(repositoryId) is not { } directory)
        {
            return (false, "");
        }

        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        // Blob-less clones fetch file contents on demand; private repositories need the same credentials.
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        auth?.Apply(start);
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

        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode == 0, output);
    }
}
