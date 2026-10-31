using System.Collections.Concurrent;
using CapabilityExplorer.Binding;
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

    private ExplorerIndex(IReadOnlyList<LoadedArtifact> artifacts, IReadOnlyList<RevisionInfo> revisions, IReadOnlyList<string> problems, GitSource? git, BindingFile bindings)
    {
        Bindings = bindings;
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

    /// <summary>The human-maintained service map. Empty when none is configured; binding is then a no-op.</summary>
    public BindingFile Bindings { get; }

    /// <summary>Newest revision per repository (by commit time when git is available).</summary>
    public IReadOnlyDictionary<string, string> DefaultSelection { get; }

    public static ExplorerIndex Load(IEnumerable<string> directories, GitSource? git, BindingFile? bindings = null, string? bindingsProblem = null)
    {
        // One artifact per (repository, commit, unit): a later directory replaces an earlier one, so analysed
        // output listed after the golden set takes its place instead of doubling every node.
        var byUnit = new Dictionary<(string, string, string), (string Label, Artifact Artifact)>();
        var problems = new List<string>();
        if (bindingsProblem is not null)
        {
            problems.Add(bindingsProblem);
        }

        foreach (var directory in directories.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var label = Path.GetRelativePath(directory, file).Replace('\\', '/');
                string text;
                try
                {
                    text = File.ReadAllText(file);
                }
                catch (IOException)
                {
                    continue; // removed while listing (an analysis swapping its output in)
                }

                var report = ArtifactValidator.Validate(text);
                if (report.IsValid)
                {
                    var h = report.Artifact!.Header;
                    byUnit[(h.RepositoryId, h.CommitSha, h.Unit.Path)] = (label, report.Artifact!);
                }
                else
                {
                    problems.Add($"{label}: {string.Join("; ", report.Problems.Take(3))}");
                }
            }
        }

        var artifacts = new List<LoadedArtifact>();
        foreach (var (label, artifact) in byUnit.Values.OrderBy(v => v.Label, StringComparer.Ordinal))
        {
            artifacts.Add(new LoadedArtifact(artifacts.Count, label, artifact));
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
        return new ExplorerIndex(artifacts, revisions, problems, git, bindings ?? BindingFile.Empty);
    }

    public RevisionInfo? FindRevision(string repositoryId, string revision) =>
        Revisions.FirstOrDefault(r => r.RepositoryId == repositoryId &&
            (r.CommitSha == revision || r.Tags.Contains(revision) || (revision.Length >= 7 && r.CommitSha.StartsWith(revision, StringComparison.Ordinal))));

    /// <summary>
    /// A view is one chosen revision per repository, written "repo:rev,repo:rev". Repositories not named use
    /// the default. This is a selected combination of code revisions, not a deployment manifest.
    /// </summary>
    public ViewIndex View(string? spec, ViewScope? scope = null)
    {
        var selection = new Dictionary<string, string>(DefaultSelection, StringComparer.Ordinal);
        if (scope is not null)
        {
            foreach (var repo in selection.Keys.Where(r => !scope.Repositories.Contains(r)).ToList())
            {
                selection.Remove(repo);
            }

            // A checked capability starts from its locked revisions where those were analysed.
            foreach (var (repo, sha) in scope.Preferred)
            {
                if (FindRevision(repo, sha) is { } locked)
                {
                    selection[repo] = locked.CommitSha;
                }
            }
        }

        foreach (var part in (spec ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = part.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || (scope is not null && !scope.Repositories.Contains(part[..colon])))
            {
                continue;
            }

            if (FindRevision(part[..colon], part[(colon + 1)..]) is { } revision)
            {
                selection[revision.RepositoryId] = revision.CommitSha;
            }
        }

        var key = $"{scope?.Id ?? "*"}|" + string.Join(",", selection.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}:{kv.Value}"));
        return _views.GetOrAdd(key, _ => new ViewIndex(this, selection));
    }

    /// <summary>Source access is limited to files some artifact cites as evidence or as its unit.</summary>
    public bool IsCitedFile(string repositoryId, string commitSha, string path) =>
        Artifacts.Any(a => a.RepositoryId == repositoryId && a.CommitSha == commitSha &&
            (a.Artifact.Header.Unit.Path == path || a.Artifact.Evidence.Any(e => e.Path == path)));
}

public sealed record Occurrence<T>(LoadedArtifact Artifact, T Record);

/// <summary>Restricts a view to the repositories of one capability, preferring its locked revisions.</summary>
public sealed record ViewScope(string Id, IReadOnlySet<string> Repositories, IReadOnlyDictionary<string, string> Preferred);

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
        // Binding runs on the selected revisions only: a route that exists in one revision and not in another is
        // bound in one view and broken in the other. The extractor edges above are untouched.
        Bound = Binder.Bind(Artifacts.Select(a => (new LoadedFrom(a.RepositoryId, a.CommitSha, a.Artifact.Header.Unit.Path, a), a.Artifact)), index.Bindings);
        BoundByOriginal = Bound.Edges.ToLookup(b => (((LoadedArtifact)b.Artifact.Handle!).Id, b.Original.Id));
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

    /// <summary>New human-derived edges from the service map (bound or broken). Never mixed into <see cref="Edges"/>.</summary>
    public BindingResult Bound { get; }

    private ILookup<(int ArtifactId, string EdgeId), BoundEdge> BoundByOriginal { get; }

    /// <summary>The binding derived from one extractor edge, if the service map names its unbound hint.</summary>
    public BoundEdge? BoundFor(Occurrence<EdgeRecord> edge) => BoundByOriginal[(edge.Artifact.Id, edge.Record.Id)].FirstOrDefault();

    public IReadOnlyDictionary<string, Occurrence<CalculationRecord>> Calculations { get; }

    public Occurrence<NodeRecord>? Node(string key) => Nodes.TryGetValue(key, out var list) ? list[0] : null;
}
