using System.Diagnostics;
using System.Text;

namespace CapabilityExplorer.Capabilities;

/// <summary>
/// Optional GitHub credentials for private repositories. The token is handed to git per process through
/// GIT_CONFIG_* environment variables as an HTTP header scoped to https://github.com/ — it never appears in a
/// clone URL, a repository's config file or a command line, and it is never sent to other hosts.
/// </summary>
public sealed class GitHubAuth
{
    private readonly string? _header;

    private GitHubAuth(string? token, string source)
    {
        Source = source;
        _header = string.IsNullOrWhiteSpace(token)
            ? null
            : "AUTHORIZATION: basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token.Trim()}"));
    }

    public static GitHubAuth None { get; } = new(null, "none");

    /// <summary>Where the token came from (variable name), for display. Never the token.</summary>
    public string Source { get; }

    public bool Configured => _header is not null;

    /// <summary>Reads GITHUB_TOKEN, then GH_TOKEN, from the environment.</summary>
    public static GitHubAuth FromEnvironment()
    {
        foreach (var name in new[] { "GITHUB_TOKEN", "GH_TOKEN" })
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } token)
            {
                return new GitHubAuth(token, name);
            }
        }

        return None;
    }

    public static GitHubAuth FromToken(string token, string source) => new(token, source);

    /// <summary>Adds the header for github.com to a git process, preserving any GIT_CONFIG_* entries already set.</summary>
    public void Apply(ProcessStartInfo start)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (_header is null)
        {
            return;
        }

        // ProcessStartInfo.Environment starts as a copy of this process's environment, so existing entries
        // (e.g. the sandbox's safe.directory) are kept; ours is appended after them.
        var count = start.Environment.TryGetValue("GIT_CONFIG_COUNT", out var c) && int.TryParse(c, out var n) ? n : 0;
        start.Environment[$"GIT_CONFIG_KEY_{count}"] = "http.https://github.com/.extraheader";
        start.Environment[$"GIT_CONFIG_VALUE_{count}"] = _header;
        start.Environment["GIT_CONFIG_COUNT"] = (count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static bool IsAuthError(string gitOutput)
    {
        ArgumentNullException.ThrowIfNull(gitOutput);
        return gitOutput.Contains("could not read Username", StringComparison.Ordinal)
            || gitOutput.Contains("Authentication failed", StringComparison.Ordinal)
            || gitOutput.Contains("Repository not found", StringComparison.Ordinal)
            || gitOutput.Contains("Invalid username or token", StringComparison.Ordinal)
            || gitOutput.Contains("403", StringComparison.Ordinal);
    }

    /// <summary>Turns git's credential errors into an actionable message.</summary>
    public string Explain(string gitError)
    {
        ArgumentNullException.ThrowIfNull(gitError);
        var authProblem = IsAuthError(gitError);
        if (!authProblem)
        {
            return gitError;
        }

        return Configured
            ? $"GitHub refused the token from {Source} (no access to this repository, expired, or missing the Contents: read permission)"
            : "repository is private or does not exist: start the explorer with GitHub credentials (GITHUB_TOKEN, or `gh auth login` on your machine; see README → Capabilities)";
    }
}
