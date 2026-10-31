using System.Collections.Concurrent;
using CapabilityExplorer.Contracts;

namespace CapabilityExplorer.Explorer;

public sealed record LoadedArtifact(int Id, string Label, Artifact Artifact)
{
    public string RepositoryId => Artifact.Header.RepositoryId;

    public string CommitSha => Artifact.Header.CommitSha;

    public IReadOnlyDictionary<string, EvidenceRecord> EvidenceById { get; } =
        Artifact.Evidence.ToDictionary(e => e.Id, StringComparer.Ordinal);
}

public sealed record RevisionInfo(
    string RepositoryId,
    string CommitSha,
    IReadOnlyList<string> Tags,
    string? Subject,
    DateTimeOffset? CommitTime,
    IReadOnlyList<int> ArtifactIds)
{
    public string Label => Tags.Count > 0 ? Tags[0] : CommitSha[..12];
}

/// <summary>
/// In-memory index over validated artifacts. Everything the explorer shows is read from here; nothing is
/// inferred that the artifacts do not state, except name-match candidates which are labelled as such.
/// </summary>
public sealed class ExplorerIndex
{
    private readonly ConcurrentDictionary<string, ViewIndex> _views = new(StringComparer.Ordinal);

    private ExplorerIndex(IReadOnlyList<LoadedArtifact> artifacts, IReadOnlyList<RevisionInfo> revisions, IReadOnlyList<string> problems, GitSource? git)
    {
        Artifacts = artifacts;
        Revisions = revisions;
        Problems = problems;
        Git = git;
        DefaultSelection = revisions
            .GroupBy(r => r.RepositoryId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(r => r.CommitTime ?? DateTimeOffset.MinValue).ThenBy(r => r.Label, StringComparer.Ordinal).Last().CommitSha,
                StringComparer.Ordinal);
    }

    public IReadOnlyList<LoadedArtifact> Artifacts { get; }

    public IReadOnlyList<RevisionInfo> Revisions { get; }

    /// <summary>Files that failed validation; shown in the UI instead of being silently dropped.</summary>
    public IReadOnlyList<string> Problems { get; }

    public GitSource? Git { get; }

    /// <summary>Newest revision per repository (by commit time when git is available).</summary>
    public IReadOnlyDictionary<string, string> DefaultSelection { get; }

    public static ExplorerIndex Load(IEnumerable<string> directories, GitSource? git)
    {
        var artifacts = new List<LoadedArtifact>();
        var problems = new List<string>();
        foreach (var directory in directories)
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var label = Path.GetRelativePath(directory, file).Replace('\\', '/');
                var report = ArtifactValidator.Validate(File.ReadAllText(file));
                if (report.IsValid)
                {
                    artifacts.Add(new LoadedArtifact(artifacts.Count, label, report.Artifact!));
                }
                else
                {
                    problems.Add($"{label}: {string.Join("; ", report.Problems.Take(3))}");
                }
            }
        }

        var revisions = artifacts
            .GroupBy(a => (a.RepositoryId, a.CommitSha))
            .Select(g => new RevisionInfo(
                g.Key.RepositoryId,
                g.Key.CommitSha,
                git?.Tags(g.Key.RepositoryId, g.Key.CommitSha) ?? [],
                git?.Subject(g.Key.RepositoryId, g.Key.CommitSha),
                git?.CommitTime(g.Key.RepositoryId, g.Key.CommitSha),
                g.Select(a => a.Id).ToList()))
            .OrderBy(r => r.RepositoryId, StringComparer.Ordinal)
            .ThenBy(r => r.CommitTime ?? DateTimeOffset.MinValue)
            .ThenBy(r => r.Label, StringComparer.Ordinal)
            .ToList();
        return new ExplorerIndex(artifacts, revisions, problems, git);
    }

    public RevisionInfo? FindRevision(string repositoryId, string revision) =>
        Revisions.FirstOrDefault(r => r.RepositoryId == repositoryId &&
            (r.CommitSha == revision || r.Tags.Contains(revision) || (revision.Length >= 7 && r.CommitSha.StartsWith(revision, StringComparison.Ordinal))));

    /// <summary>
    /// A view is one chosen revision per repository, written "repo:rev,repo:rev". Repositories not named use
    /// the default. This is a selected combination of code revisions, not a deployment manifest.
    /// </summary>
    public ViewIndex View(string? spec)
    {
        var selection = new Dictionary<string, string>(DefaultSelection, StringComparer.Ordinal);
        foreach (var part in (spec ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = part.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            if (FindRevision(part[..colon], part[(colon + 1)..]) is { } revision)
            {
                selection[revision.RepositoryId] = revision.CommitSha;
            }
        }

        var key = string.Join(",", selection.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}:{kv.Value}"));
        return _views.GetOrAdd(key, _ => new ViewIndex(this, selection));
    }

    /// <summary>Source access is limited to files some artifact cites as evidence or as its unit.</summary>
    public bool IsCitedFile(string repositoryId, string commitSha, string path) =>
        Artifacts.Any(a => a.RepositoryId == repositoryId && a.CommitSha == commitSha &&
            (a.Artifact.Header.Unit.Path == path || a.Artifact.Evidence.Any(e => e.Path == path)));
}

public sealed record Occurrence<T>(LoadedArtifact Artifact, T Record);

public sealed class ViewIndex
{
    internal ViewIndex(ExplorerIndex index, IReadOnlyDictionary<string, string> selection)
    {
        Index = index;
        Selection = selection;
        Artifacts = index.Artifacts.Where(a => selection.TryGetValue(a.RepositoryId, out var sha) && sha == a.CommitSha).ToList();
        Nodes = Artifacts.SelectMany(a => a.Artifact.Nodes.Select(n => new Occurrence<NodeRecord>(a, n)))
            .GroupBy(o => o.Record.SymbolKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Occurrence<NodeRecord>>)g.ToList(), StringComparer.Ordinal);
        var edges = Artifacts.SelectMany(a => a.Artifact.Edges.Select(e => new Occurrence<EdgeRecord>(a, e))).ToList();
        Edges = edges;
        Outgoing = edges.ToLookup(e => e.Record.From, StringComparer.Ordinal);
        Incoming = edges.Where(e => e.Record.To is not null).ToLookup(e => e.Record.To!, StringComparer.Ordinal);
        Calculations = Artifacts.SelectMany(a => a.Artifact.Calculations.Select(c => new Occurrence<CalculationRecord>(a, c)))
            .GroupBy(c => c.Record.SymbolKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    public ExplorerIndex Index { get; }

    public IReadOnlyDictionary<string, string> Selection { get; }

    public IReadOnlyList<LoadedArtifact> Artifacts { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<Occurrence<NodeRecord>>> Nodes { get; }

    public IReadOnlyList<Occurrence<EdgeRecord>> Edges { get; }

    public ILookup<string, Occurrence<EdgeRecord>> Outgoing { get; }

    public ILookup<string, Occurrence<EdgeRecord>> Incoming { get; }

    public IReadOnlyDictionary<string, Occurrence<CalculationRecord>> Calculations { get; }

    public Occurrence<NodeRecord>? Node(string key) => Nodes.TryGetValue(key, out var list) ? list[0] : null;
}
