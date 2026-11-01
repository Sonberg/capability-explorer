namespace CapabilityExplorer.Extractors.CSharp;

internal sealed record CachedGraphResult(IReadOnlyList<LockedPackage> Selected, IReadOnlyList<LockedPackage> Inspected,
    IReadOnlyList<PackageRef> Missing, IReadOnlyList<ResolutionIssue> Issues);

/// <summary>
/// Bounded cached constraint convergence, not NuGet restore: rebuild the reachable graph whenever a selected
/// package version changes. Invalid requests block their package; no stale first-choice assets survive conflicts.
/// </summary>
internal sealed class CachedPackageGraph(string? packagesRoot, string? target, IReadOnlyDictionary<string, string>? frameworkPackages)
{
    private const int MaxRounds = 32;
    private const int MaxPackages = 600;
    private sealed record Request(string Id, string? Range, string? Parent, PackageRef? Root);

    public CachedGraphResult Resolve(IEnumerable<PackageRef> packageRoots)
    {
        var roots = packageRoots.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id, StringComparer.Ordinal)
            .ThenBy(p => p.Version, StringComparer.Ordinal).ThenBy(p => p.Line).ToList();
        var selected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var history = new HashSet<string>(StringComparer.Ordinal);
        var inspected = new Dictionary<string, LockedPackage>(StringComparer.OrdinalIgnoreCase);
        string? priorState = null;
        for (var round = 0; round < MaxRounds; round++)
        {
            var requests = new Dictionary<string, List<Request>>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            var exceeded = false;
            foreach (var root in roots) Add(new(root.Id, root.Version, null, root));
            if (exceeded) return Failure("Package graph exceeds the 600-package static resolution bound");
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (queue.TryDequeue(out var id))
            {
                if (!visited.Add(id)) continue;
                if (visited.Count > MaxPackages) return Failure("Package graph exceeds the 600-package static resolution bound");
                var constraints = requests[id];
                if (constraints.Any(r => PackageVersionSelection.Parse(r.Range) is null) || Supplied(constraints)) continue;
                var version = selected.GetValueOrDefault(id) ?? (!known.Contains(id) ? Pick(id, constraints) : null);
                if (version is null) continue;
                var name = Name(constraints);
                var package = new LockedPackage(name, version);
                inspected[$"{id}@{version}"] = package;
                var dir = Path.Combine(packagesRoot!, id.ToLowerInvariant(), version.ToLowerInvariant());
                foreach (var dependency in FrameworkSelection.DependencyElements(dir, target)
                    .OrderBy(d => (string?)d.Attribute("id"), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(d => (string?)d.Attribute("version"), StringComparer.Ordinal))
                {
                    if ((string?)dependency.Attribute("id") is { } depId)
                        Add(new(depId, (string?)dependency.Attribute("version"), name, null));
                }
                if (exceeded) return Failure("Package graph exceeds the 600-package static resolution bound");
            }

            var next = requests.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => (p.Key, Version: Supplied(p.Value) ? null : Pick(p.Key, p.Value)))
                .Where(p => p.Version is not null).ToDictionary(p => p.Key, p => p.Version!, StringComparer.OrdinalIgnoreCase);
            var state = string.Join("\n", requests.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p =>
                $"{p.Key.ToLowerInvariant()}@{next.GetValueOrDefault(p.Key) ?? "-"}:{string.Join(";", p.Value.Select(r => $"{r.Parent ?? "root"}|{r.Range}|{r.Root?.Line}").Order(StringComparer.Ordinal))}"));
            if (state == priorState) return Complete(requests, next);
            if (!history.Add(state)) return Failure("Package constraints oscillate between cached versions; static resolution did not converge");
            priorState = state;
            selected = next;
            known = requests.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

            void Add(Request request)
            {
                if (!requests.TryGetValue(request.Id, out var list))
                {
                    if (requests.Count >= MaxPackages) { exceeded = true; return; }
                    requests[request.Id] = list = [];
                    queue.Enqueue(request.Id);
                }
                if (!list.Contains(request)) list.Add(request);
            }
        }
        return Failure("Package graph did not converge within 32 static resolution rounds");

        CachedGraphResult Failure(string message) => new([], inspected.Values.ToList(), [], [new("PACKAGE_GRAPH_NON_CONVERGENCE", message + "; package compile assets are omitted")]);

        CachedGraphResult Complete(Dictionary<string, List<Request>> requests, Dictionary<string, string> versions)
        {
            var missing = new List<PackageRef>();
            var issues = new List<ResolutionIssue>();
            foreach (var (id, constraints) in requests.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (versions.ContainsKey(id) || Supplied(constraints)) continue;
                var invalid = constraints.Where(r => PackageVersionSelection.Parse(r.Range) is null).ToList();
                if (invalid.Count > 0)
                    issues.Add(new("UNSUPPORTED_PACKAGE_VERSION_RANGE", $"Package {Name(constraints)} has an invalid, missing or floating request ({string.Join(", ", invalid.Select(r => r.Range ?? "none").Distinct().Order(StringComparer.Ordinal))}); its compile assets are omitted; floating requests require an exact package lock"));
                else if (constraints.Count > 1 && constraints.All(r => Pick(id, [r]) is not null))
                    issues.Add(new("PACKAGE_VERSION_CONFLICT", $"Package {Name(constraints)} has no cached version satisfying all requests: {string.Join(", ", constraints.Select(r => r.Range).Distinct().Order(StringComparer.Ordinal))}; its compile assets are omitted"));
                foreach (var request in constraints)
                {
                    if (request.Root is { } root) missing.Add(root);
                    else issues.Add(new("UNRESOLVED_TRANSITIVE_PACKAGE", $"Package {request.Parent} requires {request.Id} {request.Range}, which has no compatible cached selection"));
                }
            }
            return new(versions.Select(p => new LockedPackage(Name(requests[p.Key]), p.Value)).ToList(), inspected.Values.ToList(), missing, issues);
        }
    }

    private string? Pick(string id, IReadOnlyList<Request> requests)
    {
        var ranges = requests.Select(r => PackageVersionSelection.Parse(r.Range)).ToList();
        if (packagesRoot is null || ranges.Any(r => r is null)) return null;
        var dir = Path.Combine(packagesRoot, id.ToLowerInvariant());
        return Directory.Exists(dir) ? PackageVersionSelection.Pick(dir, ranges.Select(r => r!).ToList()) : null;
    }

    private bool Supplied(IEnumerable<Request> requests) => requests.All(r => FrameworkPackageCoverage.Satisfies(frameworkPackages, r.Id, r.Range));
    private static string Name(IEnumerable<Request> requests) => requests.Select(r => r.Id).Order(StringComparer.Ordinal).First();
}
