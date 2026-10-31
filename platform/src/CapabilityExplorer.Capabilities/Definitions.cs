using System.Text.RegularExpressions;

namespace CapabilityExplorer.Capabilities;

public enum SourceKind
{
    GitHub,
    Local,
}

/// <summary>One repository in a capability: a GitHub URL or a local git working copy, at a ref (default HEAD).</summary>
public sealed record RepositorySource(string Id, string Location, SourceKind Kind, string Ref = "HEAD");

public sealed record CapabilityDefinition(string Id, string Name, string? Description, IReadOnlyList<RepositorySource> Repositories);

/// <summary>The revision a repository resolved to when the capability was last checked.</summary>
public sealed record LockedRepository(string Id, string Ref, string? CommitSha, string? Subject, DateTimeOffset? CommitTime, string? Error);

/// <summary>
/// The exact revisions of every repository in a capability at one moment. A chosen combination of code
/// revisions, not evidence that these versions run together.
/// </summary>
public sealed record CapabilitySnapshot(string CapabilityId, DateTimeOffset CheckedAt, IReadOnlyList<LockedRepository> Repositories)
{
    public string Kind { get; init; } = "selected-code-revisions";
}

public sealed record ParseError(int Line, string Input, string Message);

public static partial class SourceParser
{
    [GeneratedRegex(@"^https://github\.com/(?<owner>[A-Za-z0-9](?:[A-Za-z0-9-]{0,38}))/(?<repo>[A-Za-z0-9._-]{1,100}?)(?:\.git)?(?:/tree/(?<ref>[^\s]+))?/?$")]
    private static partial Regex GitHubUrl();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,99}$")]
    private static partial Regex RepositoryId();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/-]{0,199}$")]
    private static partial Regex SafeRef();

    [GeneratedRegex(@"[^a-z0-9._-]+")]
    private static partial Regex Unsafe();

    /// <summary>
    /// One repository per line: <c>[id=]location [ref]</c>. Location is <c>https://github.com/owner/repo</c>
    /// (optionally <c>/tree/branch</c>) or a local path. Blank lines and lines starting with # are ignored.
    /// </summary>
    public static (IReadOnlyList<RepositorySource> Sources, IReadOnlyList<ParseError> Errors) Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var sources = new List<RepositorySource>();
        var errors = new List<ParseError>();
        var n = 0;
        foreach (var raw in lines)
        {
            n++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 2)
            {
                errors.Add(new(n, line, "expected '[id=]location [ref]'"));
                continue;
            }

            var location = parts[0];
            string? id = null;
            var eq = location.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && RepositoryId().IsMatch(location[..eq]))
            {
                id = location[..eq];
                location = location[(eq + 1)..];
            }

            var refName = parts.Length == 2 ? parts[1] : null;
            RepositorySource? source;
            if (location.StartsWith("https://", StringComparison.Ordinal) || location.StartsWith("http://", StringComparison.Ordinal) || location.StartsWith("git@", StringComparison.Ordinal))
            {
                var m = GitHubUrl().Match(location);
                if (!m.Success)
                {
                    errors.Add(new(n, line, "only https://github.com/<owner>/<repo> URLs are supported"));
                    continue;
                }

                refName ??= m.Groups["ref"].Success ? m.Groups["ref"].Value : null;
                source = new RepositorySource(
                    id ?? Normalise(m.Groups["repo"].Value),
                    $"https://github.com/{m.Groups["owner"].Value}/{m.Groups["repo"].Value}",
                    SourceKind.GitHub,
                    refName ?? "HEAD");
            }
            else
            {
                var name = Path.GetFileName(location.TrimEnd('/', '\\'));
                source = new RepositorySource(id ?? Normalise(name.EndsWith(".git", StringComparison.Ordinal) ? name[..^4] : name), location, SourceKind.Local, refName ?? "HEAD");
            }

            if (!RepositoryId().IsMatch(source.Id))
            {
                errors.Add(new(n, line, $"repository id '{source.Id}' must match [a-z0-9][a-z0-9._-]*; set one with id=location"));
                continue;
            }

            if (source.Ref != "HEAD" && (!SafeRef().IsMatch(source.Ref) || source.Ref.Contains("..", StringComparison.Ordinal)))
            {
                errors.Add(new(n, line, $"'{source.Ref}' is not an acceptable branch, tag or commit"));
                continue;
            }

            if (sources.Any(s => s.Id == source.Id))
            {
                errors.Add(new(n, line, $"repository id '{source.Id}' appears twice; set a distinct one with id=location"));
                continue;
            }

            sources.Add(source);
        }

        return (sources, errors);
    }

    public static string Normalise(string name) => Unsafe().Replace(name.ToLowerInvariant(), "-").Trim('-', '.');

    public static string CapabilityId(string name) => Normalise(name) is { Length: > 0 } id ? id : "capability";
}
