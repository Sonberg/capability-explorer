using System.Collections.Concurrent;
using System.Diagnostics;

namespace CapabilityExplorer.Explorer;

/// <summary>Read-only access to git objects of the analysed repositories. Never checks out or runs code.</summary>
public sealed class GitSource(string repositoriesDirectory)
{
    private readonly ConcurrentDictionary<(string, string, string), string?> _files = new();

    public bool HasRepository(string repositoryId) =>
        IsSafeSegment(repositoryId) && Directory.Exists(Path.Combine(repositoriesDirectory, repositoryId, ".git"));

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

    private static bool IsSafeSegment(string s) => s.Length > 0 && !s.Contains('/', StringComparison.Ordinal) && !s.Contains('\\', StringComparison.Ordinal) && s != "." && s != "..";

    private (bool Ok, string Output) Git(string repositoryId, params string[] args)
    {
        if (!HasRepository(repositoryId))
        {
            return (false, "");
        }

        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(Path.Combine(repositoriesDirectory, repositoryId));
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
