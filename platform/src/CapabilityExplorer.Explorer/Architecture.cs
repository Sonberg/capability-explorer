using System.Text.Json;
using CapabilityExplorer.Contracts;

namespace CapabilityExplorer.Explorer;

/// <summary>Where double-clicking a node leads.</summary>
public sealed record Drill(string Level, string Focus);

public sealed record ArchNode(
    string Id,
    string Category,
    string Label,
    string? Sublabel,
    string? Group,
    string? SymbolKey,
    string? RepositoryId,
    Drill? Drill,
    IReadOnlyList<string> Badges,
    IReadOnlyList<ArchMember>? Members = null);

/// <summary>One node folded into a summary node: where it lives and who touches it, with the connecting edges.</summary>
public sealed record ArchMember(string Label, string? Detail, string Status, string? SymbolKey, IReadOnlyList<ArchLink> Connections);

/// <summary>A service that touches a folded member, and how (edge label such as "reads ×63").</summary>
public sealed record ArchLink(string Service, string Label, IReadOnlyList<string> Paths, IReadOnlyList<string> Bound);

/// <summary>One underlying fact behind an aggregated canvas edge.</summary>
public sealed record ArchFact(string Relation, SymbolRef From, SymbolRef? To, string Resolution, string? UnresolvedReason, string Derivation, string? Rule, IReadOnlyList<EvidenceDto> Evidence, string? Via = null);

/// <summary>
/// An aggregated edge. Certainty: "proven" (every fact resolved), "static" (static target only), "unbound" (target
/// known by a configuration name only), "bound" (named by the service map, endpoint found), "broken" (named by the service map, endpoint missing),
/// "candidate" (a match by route or name, or a candidate fact), "unresolved".
/// </summary>
public sealed record ArchEdge(string Id, string From, string To, string Kind, string Label, int Count, string Certainty, IReadOnlyList<ArchFact> Facts);

public sealed record Crumb(string Label, string Level, string? Focus);

public sealed record ArchitectureGraph(string Level, string? Focus, string Title, IReadOnlyList<Crumb> Breadcrumbs, IReadOnlyList<ArchNode> Nodes, IReadOnlyList<ArchEdge> Edges, IReadOnlyList<string> Notes, ArchitectureBudget? Budget = null, ArchitectureFilters? Filters = null, ArchitectureFacets? Facets = null, ArchitectureOmitted? Omitted = null);

/// <summary>
/// Builds the canvas at four levels of abstraction from the facts in a view:
/// landscape (capabilities), system (the repositories of one capability and shared resources), repository (components grouped by source file where available) and component
/// (members and their direct neighbours). Nothing is drawn that the artifacts do not state, except
/// cross-repository route matches and same-name resources, which are labelled candidates.
/// </summary>
public static class Architecture
{
    private const int MaxFactsPerEdge = 40;
    private static readonly HashSet<string> CodeRelations = ["CALLS", "REFERENCES", "IMPLEMENTS", "INHERITS", "REGISTERS"];
    private static readonly HashSet<string> DataRelations = ["READS", "WRITES", "MAPS_TO"];
    private static readonly HashSet<string> MessageRelations = ["PUBLISHES", "CONSUMES"];

    /// <param name="all">The unscoped view, used by the landscape (every capability).</param>
    /// <param name="capability">The capability the view is scoped to, if any: it names the level above the repositories.</param>
    public static ArchitectureGraph Build(ViewIndex view, string level, string? focus, ViewIndex all, IReadOnlyList<CapabilityStatus> capabilities, CapabilityStatus? capability, int? nodeBudget = null, int? edgeBudget = null, IReadOnlyList<string>? hideKinds = null, string? query = null, IReadOnlyList<string>? include = null)
    {
        IReadOnlyList<Crumb> top = capability is null
            ? [new Crumb("Landscape", "landscape", null), new Crumb("System", "system", null)]
            : [new Crumb("Landscape", "landscape", null), new Crumb(capability.Name, "system", capability.Id)];
        var graph = level switch
        {
            "landscape" => Landscape(all, capabilities),
            "repository" when focus is not null => Repository(view, focus, top),
            "component" when focus is not null => Component(view, focus, top),
            "requests" when focus is not null => Requests(view, focus, top),
            _ => System(view, top, capability?.Name ?? "System"),
        };
        // Filtering bounds browser layout input; construction above still builds the full graph.
        return ArchitectureBudgeting.Apply(graph, nodeBudget, edgeBudget, hideKinds, query, include);
    }

    // ------------------------------------------------------------------ landscape

    /// <summary>
    /// Capabilities as nodes. Lines are the system-level facts lifted to the capabilities that own each repository,
    /// plus "shares" links between capabilities that list the same repository. Resources stay only where they join capabilities.
    /// </summary>
    private static ArchitectureGraph Landscape(ViewIndex view, IReadOnlyList<CapabilityStatus> capabilities)
    {
        var b = new Builder(view);
        foreach (var c in capabilities)
        {
            b.Node(new ArchNode($"cap:{c.Id}", "capability", c.Name, c.Description, null, null, null, new Drill("system", c.Id),
                [$"{c.Repositories.Count} repositor{(c.Repositories.Count == 1 ? "y" : "ies")}", $"{c.Analysed} analysed"]));
        }

        var owners = capabilities.SelectMany(c => c.Repositories.Select(r => (Repo: r.Id, Cap: $"cap:{c.Id}")))
            .ToLookup(x => x.Repo, x => x.Cap, StringComparer.Ordinal);
        SystemEdges(view, b, repo => owners[repo]);

        for (var i = 0; i < capabilities.Count; i++)
        {
            for (var j = i + 1; j < capabilities.Count; j++)
            {
                var shared = capabilities[i].Repositories.Select(r => r.Id).Intersect(capabilities[j].Repositories.Select(r => r.Id), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                if (shared.Count > 0)
                {
                    b.Link($"cap:{capabilities[i].Id}", $"cap:{capabilities[j].Id}", "shares", $"shares {string.Join(", ", shared)}", "proven");
                }
            }
        }

        var graph = b.Graph("landscape", null, "Landscape", [new Crumb("Landscape", "landscape", null)],
            ["Capabilities are linked where they share a repository, or where a repository of one calls, writes or reads something of another.", "Data stores and channels are shown only where they join two capabilities."]);

        // A resource joins capabilities when at least two repositories, owned by at least two capabilities, touch it
        // (one repository listed by two capabilities does not count). ponytail: linear scans; fine for tens of capabilities.
        var byRepo = new Builder(view);
        SystemEdges(view, byRepo, repo => [$"repo:{repo}"]);
        var repoEdges = byRepo.Graph("system", null, "", [], []).Edges;
        HashSet<string> Touching(IEnumerable<ArchEdge> edges, string id, string prefix) =>
            edges.Where(e => e.From == id || e.To == id).Select(e => e.From == id ? e.To : e.From).Where(x => x.StartsWith(prefix, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        var joins = graph.Nodes.Where(n => n.Category != "capability" && Touching(graph.Edges, n.Id, "cap:").Count >= 2 && Touching(repoEdges, n.Id, "repo:").Count >= 2)
            .Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        bool Keep(string id) => id.StartsWith("cap:", StringComparison.Ordinal) || joins.Contains(id);
        return graph with { Nodes = graph.Nodes.Where(n => Keep(n.Id)).ToList(), Edges = graph.Edges.Where(e => Keep(e.From) && Keep(e.To)).ToList() };
    }

    // ------------------------------------------------------------------ system

    private static ArchitectureGraph System(ViewIndex view, IReadOnlyList<Crumb> crumbs, string title)
    {
        var b = new Builder(view);
        foreach (var repo in view.Artifacts.Select(a => a.RepositoryId).Distinct().Order(StringComparer.Ordinal))
        {
            b.Node(RepoNode(view, repo));
        }

        SystemEdges(view, b, repo => [$"repo:{repo}"]);
        return Collapse(b.Graph("system", null, title, crumbs,
            ["Repositories are connected through candidate HTTP route matches until base URLs are bound.",
             "Data stores, channels, external hosts and unresolved targets are summarised per kind once there are more than a few; click a connection to list every underlying fact."]));
    }

    private const int CollapseAbove = 4;

    /// <summary>
    /// Folds crowded sinks (data stores, channels, unresolved hosts) into one node per kind, so the system level stays
    /// a map of repositories. Edges merge per (source, summary): facts, count and the weakest certainty are kept.
    /// </summary>
    private static ArchitectureGraph Collapse(ArchitectureGraph g)
    {
        var titles = new Dictionary<string, string> { ["datastore"] = "Data stores", ["channel"] = "Channels", ["host"] = "External hosts", ["unmatched"] = "Unresolved hosts" };
        var folded = titles.Keys.Where(c => g.Nodes.Count(n => n.Category == c) > CollapseAbove).ToHashSet(StringComparer.Ordinal);
        if (folded.Count == 0)
        {
            return g;
        }

        string Map(string id) => g.Nodes.FirstOrDefault(n => n.Id == id) is { } n && folded.Contains(n.Category) ? $"fold:{n.Category}" : id;
        var nodes = g.Nodes.Where(n => !folded.Contains(n.Category)).ToList();
        foreach (var c in folded)
        {
            var members = g.Nodes.Where(n => n.Category == c).ToList();
            var unbound = members.Count(n => n.Badges.Contains("unbound") || n.Badges.Contains("unmatched"));
            nodes.Add(new ArchNode($"fold:{c}", c, $"{titles[c]} ({members.Count})", string.Join(", ", members.Take(3).Select(n => n.Label)) + (members.Count > 3 ? ", …" : ""),
                null, null, null, null, unbound > 0 ? [$"{unbound} unbound"] : [],
                members.Select(n => new ArchMember(n.Label, n.Sublabel, n.Badges.Count > 0 ? n.Badges[0] : "", n.SymbolKey,
                g.Edges.Where(e => e.Kind != "sameName" && (e.To == n.Id || e.From == n.Id))
                    .Select(e => new ArchLink(g.Nodes.First(x => x.Id == (e.To == n.Id ? e.From : e.To)).Label, e.Label, CalledPaths(e), BoundVia(e))).OrderBy(l => l.Service, StringComparer.Ordinal).ToList()))
                .OrderBy(m => m.Label, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Detail, StringComparer.OrdinalIgnoreCase).ToList()));
        }

        var order = new[] { "proven", "static", "unbound", "bound", "candidate", "unresolved", "broken" };
        var edges = g.Edges.Where(e => e.Kind != "sameName")
            .GroupBy(e => (From: Map(e.From), To: Map(e.To), e.Kind))
            .Select(x =>
            {
                var facts = x.SelectMany(e => e.Facts).Take(MaxFactsPerEdge).ToList();
                var count = x.Sum(e => e.Count);
                var worst = x.Select(e => e.Certainty).MaxBy(c => Array.IndexOf(order, c))!;
                var label = x.Select(e => e.Label.Split(" ×")[0]).Distinct().Count() == 1 ? x.First().Label.Split(" ×")[0] : x.Key.Kind;
                return new ArchEdge($"{x.Key.From}->{x.Key.To}:{x.Key.Kind}", x.Key.From, x.Key.To, x.Key.Kind, count > 1 ? $"{label} ×{count}" : label, count, worst, facts);
            }).OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
        return g with { Nodes = nodes.OrderBy(n => n.Id, StringComparer.Ordinal).ToList(), Edges = edges };
    }

    /// <summary>Repository-level facts: HTTP between repositories, data and messaging resources. <paramref name="owners"/> maps a repository to its canvas nodes.</summary>
    private static void SystemEdges(ViewIndex view, Builder b, Func<string, IEnumerable<string>> owners)
    {
        foreach (var e in view.Edges)
        {
            var r = e.Record;
            foreach (var from in owners(e.Artifact.RepositoryId))
            {
                if (r.Relation == "REQUESTS")
                {
                    foreach (var (target, certainty, missing) in HttpTargets(view, e))
                    {
                        if (missing is not null)
                        {
                            var route = Route(missing);
                            var id = $"broken:{missing}";
                            b.Node(new ArchNode(id, "unmatched", route, $"bound to {missing.Split('|')[1]}, but no such endpoint", "Broken", null, null, null, ["broken"]));
                            b.Edge(from, id, "http", route, "broken", e);
                        }
                        else if (target is null)
                        {
                            var sink = Unmatched(b, r.To);
                            b.Edge(from, sink, "http", r.To is null ? "dynamic request" : Route(r.To), sink.StartsWith("host:", StringComparison.Ordinal) ? "static" : "unresolved", e);
                        }
                        else if (target.Artifact.RepositoryId != e.Artifact.RepositoryId)
                        {
                            foreach (var to in owners(target.Artifact.RepositoryId))
                            {
                                b.Edge(from, to, "http", Route(target.Record.SymbolKey), certainty, e);
                            }
                        }
                    }
                }
                else if (r.To is not null && (DataRelations.Contains(r.Relation) || MessageRelations.Contains(r.Relation)) && ResourceKey(r.To) is { } res)
                {
                    b.Node(ResourceNode(view, res));
                    var consume = r.Relation is "CONSUMES" or "READS";
                    b.Edge(consume ? $"res:{res}" : from, consume ? from : $"res:{res}", KindOf(r.Relation), EdgeLabel(r.Relation), null, e);
                }
                else if (r.Relation == "DECLARES" && r.To is not null && view.Node(r.From)?.Record.NodeKind == "Project" && view.Node(r.To)?.Record.NodeKind is "Table" or "View")
                {
                    b.Node(ResourceNode(view, r.To));
                    b.Edge(from, $"res:{r.To}", "declares", "declares", null, e);
                }
            }
        }
    }

    private static ArchNode RepoNode(ViewIndex view, string repo)
    {
        var artifacts = view.Artifacts.Where(a => a.RepositoryId == repo).ToList();
        var services = artifacts.SelectMany(a => a.Artifact.Nodes).Where(n => n.NodeKind == "Service").Select(n => n.Name).ToList();
        var endpoints = artifacts.Sum(a => a.Artifact.Nodes.Count(n => n.NodeKind == "Endpoint"));
        var calcs = artifacts.Sum(a => a.Artifact.Calculations.Count);
        var partial = artifacts.Any(a => a.Artifact.Complete.Status == "partial");
        var badges = new List<string>();
        if (endpoints > 0)
        {
            badges.Add($"{endpoints} endpoints");
        }

        if (calcs > 0)
        {
            badges.Add($"{calcs} calculations");
        }

        if (partial)
        {
            badges.Add("partial");
        }

        var revision = view.Index.FindRevision(repo, view.Selection[repo]);
        return new ArchNode($"repo:{repo}", services.Count > 0 ? "service" : "application", repo,
            services.Count > 0 ? string.Join(", ", services) : string.Join(", ", artifacts.Select(a => a.Artifact.Header.Unit.Name)),
            null, null, repo, new Drill("repository", repo), [revision?.Label ?? "", .. badges]);
    }

    // ------------------------------------------------------------------ repository

    private static ArchitectureGraph Repository(ViewIndex view, string repo, IReadOnlyList<Crumb> top)
    {
        var b = new Builder(view);
        foreach (var a in view.Artifacts.Where(a => a.RepositoryId == repo))
        {
            foreach (var n in a.Artifact.Nodes)
            {
                if (n.NodeKind == "Endpoint")
                {
                    b.Node(new ArchNode($"ep:{n.SymbolKey}", "endpoint", n.Props?.GetProperty("method").GetString() is { } m ? $"{m} {n.Props.Value.GetProperty("route").GetString()}" : n.Name, null, a.Artifact.Header.Unit.Name, n.SymbolKey, repo, null, []));
                }
                else if (Component(view, new Occurrence<NodeRecord>(a, n)) is { } c)
                {
                    b.Node(c.Node);
                }
            }
        }

        foreach (var e in view.Edges)
        {
            var r = e.Record;
            var fromRepo = e.Artifact.RepositoryId;
            if (fromRepo != repo)
            {
                // Callers from other repositories, by route candidate, give context at the boundary.
                if (r.Relation == "REQUESTS")
                {
                    foreach (var (target, certainty, _) in HttpTargets(view, e).Where(t => t.Target?.Artifact.RepositoryId == repo))
                    {
                        b.Node(ExternalRepo(view, fromRepo));
                        b.Edge($"repo:{fromRepo}", $"ep:{target!.Record.SymbolKey}", "http", Route(target.Record.SymbolKey), certainty, e);
                    }
                }

                continue;
            }

            var from = ComponentOf(view, r.From);
            if (from is null)
            {
                continue;
            }

            if (r.Relation == "HANDLES" && r.To is not null)
            {
                b.Edge($"ep:{r.To}", from.Id, "handles", "handled by", null, e);
            }
            else if (r.Relation == "REQUESTS")
            {
                foreach (var (target, certainty, missing) in HttpTargets(view, e))
                {
                    if (missing is not null)
                    {
                        b.Node(new ArchNode($"broken:{missing}", "unmatched", Route(missing), $"bound to {missing.Split('|')[1]}, but no such endpoint", null, null, null, null, ["broken"]));
                        b.Edge(from.Id, $"broken:{missing}", "http", Route(missing), "broken", e);
                    }
                    else if (target is null)
                    {
                        var sink = Unmatched(b, r.To);
                        b.Edge(from.Id, sink, "http", r.To is null ? "dynamic request" : Route(r.To), sink.StartsWith("host:", StringComparison.Ordinal) ? "static" : "unresolved", e);
                    }
                    else if (target.Artifact.RepositoryId == repo)
                    {
                        b.Edge(from.Id, $"ep:{target.Record.SymbolKey}", "http", Route(target.Record.SymbolKey), certainty, e);
                    }
                    else
                    {
                        b.Node(ExternalRepo(view, target.Artifact.RepositoryId));
                        b.Edge(from.Id, $"repo:{target.Artifact.RepositoryId}", "http", Route(target.Record.SymbolKey), certainty, e);
                    }
                }
            }
            else if (r.To is not null && (DataRelations.Contains(r.Relation) || MessageRelations.Contains(r.Relation)) && ResourceKey(r.To) is { } res)
            {
                b.Node(ResourceNode(view, res));
                var consume = r.Relation is "CONSUMES" or "READS";
                b.Edge(consume ? $"res:{res}" : from.Id, consume ? from.Id : $"res:{res}", KindOf(r.Relation), EdgeLabel(r.Relation), null, e);
            }
            else if (r.To is not null && CodeRelations.Contains(r.Relation) && ComponentOf(view, r.To) is { } to && to.Id != from.Id && to.Node.RepositoryId == repo)
            {
                b.Edge(from.Id, to.Id, KindOf(r.Relation), EdgeLabel(r.Relation), null, e);
            }
        }

        b.SameNameLinks();
        return b.Graph("repository", repo, repo, [.. top, new Crumb(repo, "repository", repo)],
            ["C# types are grouped by declaration source file; TypeScript modules and SQL objects retain their unit grouping.", "Calls into external libraries are shown one level down."]);
    }

    private static ArchNode ExternalRepo(ViewIndex view, string repo) => RepoNode(view, repo) with { Category = "external", Group = "Other systems" };

    // ------------------------------------------------------------------ requests

    /// <summary>The routes requested from one unmatched base (see <see cref="BaseOf"/>), with the components that request them.</summary>
    private static ArchitectureGraph Requests(ViewIndex view, string focus, IReadOnlyList<Crumb> top)
    {
        var b = new Builder(view);
        foreach (var e in view.Edges.Where(e => e.Record.Relation == "REQUESTS" && e.Record.To is not null && BaseOf(e.Record.To) == focus))
        {
            if (HttpTargets(view, e).Any(t => t.Target is not null || t.Missing is not null))
            {
                continue;
            }

            var repo = e.Artifact.RepositoryId;
            var from = ComponentOf(view, e.Record.From)?.Node ?? RepoNode(view, repo);
            b.Node(from);
            var route = Route(e.Record.To!);
            b.Node(new ArchNode($"unmatched:{route}", "unmatched", route, "no endpoint in scope", focus, e.Record.To, null, null, ["unmatched"]));
            b.Edge(from.Id, $"unmatched:{route}", "http", route, "unresolved", e);
        }

        return b.Graph("requests", focus, focus, [.. top, new Crumb(focus, "requests", focus)],
            ["Requests whose base URL names no repository in scope, grouped by the host or configuration name they go through."]);
    }

    /// <summary>Canvas id for a request with no endpoint in scope: one node per base, or one for dynamic requests.</summary>
    private static string Unmatched(Builder b, string? to)
    {
        if (to is null)
        {
            b.Node(new ArchNode("unmatched:dynamic request", "unmatched", "dynamic request", "target not statically known", "Unmatched", null, null, null, ["unresolved"]));
            return "unmatched:dynamic request";
        }

        var (cluster, isHost) = BaseInfo(to);
        if (isHost)
        {
            b.Node(new ArchNode($"host:{cluster}", "host", cluster, "external host", "External", null, null, new Drill("requests", cluster), []));
            return $"host:{cluster}";
        }

        b.Node(new ArchNode($"base:{cluster}", "unmatched", cluster, "no endpoint in scope", "Unmatched", null, null, new Drill("requests", cluster), ["unmatched"]));
        return $"base:{cluster}";
    }

    /// <summary>
    /// What a request goes through, from the base segment of its key: the host of a base URL, else the configuration
    /// name (env var, config key, client type) it is known by, else the declared service.
    /// </summary>
    internal static string BaseOf(string key) => BaseInfo(key).Name;

    /// <summary><see cref="BaseOf"/>, plus whether the name is the host of a validated absolute base URL.</summary>
    internal static (string Name, bool IsHost) BaseInfo(string key)
    {
        var seg = key.Split('|');
        if (seg.Length < 3)
        {
            return ("unknown base", false);
        }

        var store = seg[1];
        if (!store.StartsWith("@unbound:", StringComparison.Ordinal))
        {
            return (store, false);
        }

        var colon = store.IndexOf(':', "@unbound:".Length);
        var hint = colon < 0 ? store["@unbound:".Length..] : store[(colon + 1)..];
        var eq = hint.IndexOf('=', StringComparison.Ordinal);
        var (kind, value) = eq < 0 ? ("", hint) : (hint[..eq], hint[(eq + 1)..]);
        return kind switch
        {
            "base" when Uri.TryCreate(value, UriKind.Absolute, out var uri) => (uri.Host, true),
            "host" when value.Contains('.', StringComparison.Ordinal) && !value.Contains(' ', StringComparison.Ordinal) => (value, true),
            "base" or "host" or "client" => (value, false),
            _ => ($"{kind} {value}", false),
        };
    }

    // ------------------------------------------------------------------ component

    private static ArchitectureGraph Component(ViewIndex view, string focus, IReadOnlyList<Crumb> top)
    {
        var b = new Builder(view);
        var focusNode = view.Node(focus);
        var focusInfo = focusNode is null ? null : Component(view, focusNode);
        var title = focusInfo?.Node.Label ?? Map.DisplayName(focus);
        var repo = focusInfo?.Node.RepositoryId ?? SymbolKey.OwningRepository(focus) ?? "";
        var members = view.Nodes.Values.Select(l => l[0]).Where(o => ComponentOf(view, o.Record.SymbolKey)?.Key == focus).ToList();

        foreach (var m in members)
        {
            b.Node(Member(m, title, focus));
        }

        var memberKeys = members.Select(m => m.Record.SymbolKey).ToHashSet(StringComparer.Ordinal);
        foreach (var e in view.Edges.Where(e => memberKeys.Contains(e.Record.From) || (e.Record.To is not null && memberKeys.Contains(e.Record.To))))
        {
            var r = e.Record;
            if (r.Relation is "DECLARES" or "EXPOSES")
            {
                continue;
            }

            var fromId = Endpoint(b, view, r.From, focus);
            if (r.To is null)
            {
                var id = $"dynamic:{r.From}:{r.UnresolvedReason}";
                b.Node(new ArchNode(id, "unmatched", $"⟨{r.UnresolvedReason}⟩", r.Props?.TryGetProperty("expression", out var x) == true ? x.GetString() : "target not statically known", "Unresolved", null, null, null, ["unresolved"]));
                b.Edge(fromId, id, KindOf(r.Relation), EdgeLabel(r.Relation), "unresolved", e);
                continue;
            }

            var toId = Endpoint(b, view, r.To, focus);
            if (r.Relation == "REQUESTS")
            {
                foreach (var (target, certainty, missing) in HttpTargets(view, e))
                {
                    if (missing is not null)
                    {
                        b.Node(new ArchNode($"broken:{missing}", "unmatched", Route(missing), $"bound to {missing.Split('|')[1]}, but no such endpoint", "Broken", null, null, null, ["broken"]));
                        b.Edge(toId, $"broken:{missing}", "http", "bound, endpoint missing", "broken", e);
                    }
                    else if (target is not null)
                    {
                        b.Node(new ArchNode($"ep:{target.Record.SymbolKey}", "endpoint", Route(target.Record.SymbolKey), target.Artifact.RepositoryId, target.Artifact.RepositoryId, target.Record.SymbolKey, target.Artifact.RepositoryId, null, [certainty == "bound" ? "bound" : "candidate"]));
                        b.Edge(toId, $"ep:{target.Record.SymbolKey}", "http", certainty == "bound" ? "bound by service map" : "route match", certainty, e);
                    }
                }
            }

            // Draw in the direction data or requests flow: resources feed readers, endpoints feed handlers.
            var reverse = r.Relation is "CONSUMES" or "READS" or "HANDLES";
            b.Edge(reverse ? toId : fromId, reverse ? fromId : toId, KindOf(r.Relation), EdgeLabel(r.Relation), null, e);
        }

        return b.Graph("component", focus, title,
            [.. top, new Crumb(repo, "repository", repo), new Crumb(title, "component", focus)],
            ["Members of this component and everything they touch directly. Open a member to see its source and evidence."]);
    }

    private static ArchNode Member(Occurrence<NodeRecord> m, string componentLabel, string focus) => new(
        $"mem:{m.Record.SymbolKey}",
        m.Record.SymbolKey == focus ? "component" : MemberCategory(m.Record.NodeKind),
        m.Record.SymbolKey == focus ? m.Record.Name : MemberLabel(m.Record),
        m.Record.NodeKind,
        componentLabel,
        m.Record.SymbolKey,
        m.Artifact.RepositoryId,
        null,
        m.Artifact.Artifact.Calculations.Any(c => c.SymbolKey == m.Record.SymbolKey) ? ["calculation"] : []);

    /// <summary>Canvas id for one end of an edge at component level, adding the node if needed.</summary>
    private static string Endpoint(Builder b, ViewIndex view, string key, string focus)
    {
        if (view.Node(key) is { } occ)
        {
            var comp = ComponentOf(view, key);
            if (comp?.Key == focus)
            {
                return $"mem:{key}";
            }

            if (occ.Record.NodeKind == "Endpoint")
            {
                b.Node(new ArchNode($"ep:{key}", "endpoint", Route(key), occ.Artifact.RepositoryId, occ.Artifact.RepositoryId, key, occ.Artifact.RepositoryId, null, []));
                return $"ep:{key}";
            }

            if (ResourceKey(key) is { } res && occ.Record.NodeKind is "Table" or "View" or "Column" or "Channel")
            {
                b.Node(ResourceNode(view, res));
                return $"res:{res}";
            }

            var group = comp?.Node.Label ?? occ.Artifact.Artifact.Header.Unit.Name;
            b.Node(new ArchNode($"mem:{key}", MemberCategory(occ.Record.NodeKind), MemberLabel(occ.Record), $"{occ.Record.NodeKind} · {occ.Artifact.RepositoryId}", group, key, occ.Artifact.RepositoryId,
                comp is null ? null : new Drill("component", comp.Key), []));
            return $"mem:{key}";
        }

        if (ResourceKey(key) is { } resource)
        {
            b.Node(ResourceNode(view, resource));
            return $"res:{resource}";
        }

        if (key.StartsWith("http|", StringComparison.Ordinal))
        {
            b.Node(new ArchNode($"ep:{key}", "endpoint", Route(key), key.Contains("@unbound:", StringComparison.Ordinal) ? "base URL unbound" : null, "Requests", key, null, null, key.Contains("@unbound:", StringComparison.Ordinal) ? ["unbound"] : []));
            return $"ep:{key}";
        }

        b.Node(new ArchNode($"ext:{key}", "external", Map.DisplayName(key), key.Split('|').ElementAtOrDefault(2), "External", null, null, null, []));
        return $"ext:{key}";
    }

    // ------------------------------------------------------------------ helpers

    private sealed record ComponentInfo(string Key, ArchNode Node)
    {
        public string Id => Node.Id;
    }

    /// <summary>The component a symbol belongs to: its C# type, TypeScript module or SQL object.</summary>
    private static ComponentInfo? ComponentOf(ViewIndex view, string key)
    {
        var occ = view.Node(key);
        return occ is null ? null : Component(view, occ);
    }

    private static ComponentInfo? Component(ViewIndex view, Occurrence<NodeRecord> occ)
    {
        var n = occ.Record;
        var repo = occ.Artifact.RepositoryId;
        var unit = occ.Artifact.Artifact.Header.Unit.Name;
        var seg = n.SymbolKey.Split('|');
        string? key = null;
        string? label = null;
        var category = "component";
        switch (seg[0])
        {
            case "cs" when n.NodeKind == "Type":
                key = n.SymbolKey;
                label = n.Name;
                category = n.Props?.TryGetProperty("typeKind", out var tk) == true && tk.GetString() == "interface" ? "interface" : "component";
                break;
            case "cs" when n.NodeKind is "Method" or "Property" or "Field" or "Variable" && seg.Length >= 4:
                var body = seg[3].Length > 2 && seg[3][1] == ':' ? seg[3][2..] : seg[3];
                var paren = body.IndexOf('(', StringComparison.Ordinal);
                var head = paren >= 0 ? body[..paren] : body;
                var dot = head.LastIndexOf('.');
                if (dot <= 0)
                {
                    return null;
                }

                var typeName = head[..dot];
                key = $"cs|{seg[1]}|{seg[2]}|T:{typeName}";
                label = typeName.Split('.')[^1].Split('`')[0];
                break;
            case "ts" when seg.Length >= 5 && n.NodeKind is not "Project" and not "Service" and not "Endpoint" and not "Channel":
                key = $"ts|{seg[1]}|{seg[2]}|{seg[3]}|(module)#namespace";
                label = seg[3].Split('/')[^1];
                category = "module";
                break;
            case "sql" when n.NodeKind is "Table" or "View" or "Routine" or "Column":
                key = n.SymbolKey.Contains('#', StringComparison.Ordinal) ? n.SymbolKey[..n.SymbolKey.IndexOf('#', StringComparison.Ordinal)] : n.SymbolKey;
                label = seg[^1].Split('#')[0];
                category = "datastore";
                break;
        }

        if (key is null)
        {
            return null;
        }

        var declared = view.Node(key);
        var calcs = view.Calculations.Keys.Count(k => ComponentKeyFast(k) == key);
        return new ComponentInfo(key, new ArchNode($"comp:{key}", category, declared?.Record.Name ?? label!, declared?.Record.NodeKind ?? (category == "module" ? "module" : "type"),
            SourcePathResolver.ForCSharpComponent(view, key) ?? unit, key, repo, new Drill("component", key), calcs > 0 ? [$"{calcs} calculation{(calcs == 1 ? "" : "s")}"] : []));
    }

    /// <summary>Component key from a symbol key alone (used for counting).</summary>
    private static string? ComponentKeyFast(string key)
    {
        var seg = key.Split('|');
        if (seg[0] == "cs" && seg.Length >= 4 && seg[3].Length > 2 && seg[3][1] == ':' && seg[3][0] != 'T')
        {
            var body = seg[3][2..];
            var head = body.IndexOf('(', StringComparison.Ordinal) is var p and >= 0 ? body[..p] : body;
            return head.LastIndexOf('.') is var d and > 0 ? $"cs|{seg[1]}|{seg[2]}|T:{head[..d]}" : null;
        }

        if (seg[0] == "ts" && seg.Length >= 5)
        {
            return $"ts|{seg[1]}|{seg[2]}|{seg[3]}|(module)#namespace";
        }

        return seg[0] == "sql" ? key.Split('#')[0] : key;
    }

    private static string MemberCategory(string kind) => kind switch
    {
        "Method" or "Function" or "Routine" => "member",
        "Type" => "component",
        "Table" or "View" or "Column" => "datastore",
        "Channel" => "channel",
        "Endpoint" => "endpoint",
        _ => "member",
    };

    private static string MemberLabel(NodeRecord n)
    {
        if (n.SymbolKey.StartsWith("cs|", StringComparison.Ordinal) && n.NodeKind is "Method" or "Property" or "Field")
        {
            var body = n.SymbolKey.Split('|')[3];
            var head = body.IndexOf('(', StringComparison.Ordinal) is var p and >= 0 ? body[..p] : body;
            var parts = head[2..].Split('.');
            return parts.Length >= 2 ? $"{parts[^2].Split('`')[0]}.{parts[^1]}" : n.Name;
        }

        if (n.SymbolKey.StartsWith("ts|", StringComparison.Ordinal))
        {
            return n.SymbolKey.Split('|')[^1].Split('#')[0];
        }

        return n.Name;
    }

    // Whether a store is read or written isn't what the map is for; any data access is just "uses".
    private static string EdgeLabel(string relation) => KindOf(relation) == "data" ? "uses" : relation.ToLowerInvariant().Replace('_', ' ');

    private static string KindOf(string relation) => relation switch
    {
        "READS" or "WRITES" or "MAPS_TO" => "data",
        "PUBLISHES" or "CONSUMES" => "message",
        "REQUESTS" => "http",
        "HANDLES" => "handles",
        "IMPLEMENTS" or "INHERITS" => "implements",
        "REGISTERS" => "registers",
        "DEPENDS_ON" => "depends",
        _ => "call",
    };

    /// <summary>Every distinct endpoint called over an http edge, e.g. "GET /v1/orders"; empty for other kinds and for dynamic requests.</summary>
    private static List<string> CalledPaths(ArchEdge e) =>
        e.Kind != "http" ? [] : e.Facts.Where(f => f.To is not null).Select(f => Route(f.To!.Key)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    /// <summary>How the facts behind a link reach the resource: access mechanism, then client or context, then connection, e.g. "efcore · ShopDb · conn Shop".</summary>
    private static List<string> BoundVia(ArchEdge e) => e.Facts.Select(f => f.Via).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    private static string? ViaOf(EdgeRecord r)
    {
        if (r.Rule is null || r.Props is not { ValueKind: JsonValueKind.Object } props)
        {
            return r.Rule?.Name;
        }

        string? Text(string name) => props.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var via = Text("client") ?? (Text("via") is { } key ? key[(key.LastIndexOf('|') + 1)..] is { Length: > 2 } tail ? tail[2..] : key : null);
        return string.Join(" · ", new[] { r.Rule.Name, via, Text("connectionName") is { } conn ? $"conn {conn}" : null }.OfType<string>());
    }

    private static string Route(string key) => key[(key.LastIndexOf('|') + 1)..];

    /// <summary>Resource identity for data stores and channels: tables (columns fold into their table) and channel keys.</summary>
    private static string? ResourceKey(string key) =>
        key.StartsWith("sql|", StringComparison.Ordinal) ? key.Split('#')[0]
        : key.StartsWith("chan|", StringComparison.Ordinal) ? key
        : null;

    private static ArchNode ResourceNode(ViewIndex view, string key)
    {
        var declared = view.Node(key);
        var isChannel = key.StartsWith("chan|", StringComparison.Ordinal);
        var name = isChannel ? Route(key)[(Route(key).IndexOf('/', StringComparison.Ordinal) + 1)..] : Route(key);
        var store = key.Split('|')[isChannel ? 2 : 1];
        var bound = !key.Contains("@unbound:", StringComparison.Ordinal);
        var hint = bound ? store : store[(store.LastIndexOf(':') + 1)..].Replace("conn=", "connection ", StringComparison.Ordinal).Replace("dbproject=", "db project ", StringComparison.Ordinal).Replace("config=", "config ", StringComparison.Ordinal).Replace("env=", "env ", StringComparison.Ordinal);
        var scope = bound ? null : store["@unbound:".Length..store.LastIndexOf(':')];
        return new ArchNode($"res:{key}", isChannel ? "channel" : "datastore", name,
            isChannel ? $"{Route(key).Split('/')[0]} · {(bound ? store : $"{scope}: {hint}")}" : bound ? store : $"{scope}: {hint}",
            isChannel ? "Messaging" : "Data", declared?.Record.SymbolKey ?? key, declared?.Artifact.RepositoryId, null, [bound ? "bound" : "unbound"]);
    }

    /// <summary>Endpoints a REQUESTS edge may reach: the exact key, else route candidates, else nothing (null).</summary>
    private static IEnumerable<(Occurrence<NodeRecord>? Target, string Certainty, string? Missing)> HttpTargets(ViewIndex view, Occurrence<EdgeRecord> e)
    {
        var to = e.Record.To;
        if (to is not null && view.BoundFor(e) is { } bound)
        {
            // The service map names the base URL: the link is exact, or provably broken (no such endpoint).
            if (view.Node(bound.Edge.To!) is { } hit)
            {
                yield return (hit, "bound", null);
            }
            else
            {
                yield return (null, "broken", bound.Edge.To);
            }

            yield break;
        }

        if (to is null)
        {
            yield return (null, "unresolved", null);
            yield break;
        }

        if (view.Node(to) is { } exact)
        {
            yield return (exact, e.Record.Resolution == "resolved" ? "proven" : "static", null);
            yield break;
        }

        var matches = view.Nodes.Values.Select(l => l[0]).Where(o => o.Record.NodeKind == "Endpoint" && Route(o.Record.SymbolKey) == Route(to)).ToList();
        if (matches.Count == 0)
        {
            yield return (null, "unresolved", null);
        }

        foreach (var m in matches)
        {
            yield return (m, "candidate", null);
        }
    }

    private sealed class Builder(ViewIndex view)
    {
        private readonly Dictionary<string, ArchNode> _nodes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (string From, string To, string Kind, string Label, string? Certainty, List<Occurrence<EdgeRecord>> Facts, HashSet<string> Labels)> _edges = new(StringComparer.Ordinal);

        public void Node(ArchNode node) => _nodes.TryAdd(node.Id, node);

        public void Edge(string from, string to, string kind, string label, string? certainty, Occurrence<EdgeRecord> fact)
        {
            if (from == to)
            {
                return;
            }

            var id = $"{from}->{to}:{kind}";
            if (!_edges.TryGetValue(id, out var edge))
            {
                edge = (from, to, kind, label, certainty, [], new HashSet<string>(StringComparer.Ordinal));
                _edges[id] = edge;
            }
            else if (certainty is not null && Rank(certainty) > Rank(edge.Certainty))
            {
                _edges[id] = edge with { Certainty = certainty };
            }

            edge.Labels.Add(label);
            if (!edge.Facts.Contains(fact))
            {
                edge.Facts.Add(fact);
            }
        }

        /// <summary>An edge backed by the definitions rather than by extracted facts.</summary>
        public void Link(string from, string to, string kind, string label, string certainty) =>
            _edges[$"{from}->{to}:{kind}"] = (from, to, kind, label, certainty, [], [label]);

        /// <summary>Dashed hint edges between resources that share a name but not a key.</summary>
        public void SameNameLinks()
        {
            foreach (var group in _nodes.Values.Where(n => n.Category is "datastore" or "channel").GroupBy(n => (n.Category, n.Label)).Where(g => g.Count() > 1))
            {
                var nodes = group.OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
                for (var i = 1; i < nodes.Count; i++)
                {
                    _edges[$"{nodes[i - 1].Id}~{nodes[i].Id}"] = (nodes[i - 1].Id, nodes[i].Id, "sameName", "same name, not proven same", "candidate", [], ["same name, not proven same"]);
                }
            }
        }

        public ArchitectureGraph Graph(string level, string? focus, string title, IReadOnlyList<Crumb> crumbs, IReadOnlyList<string> notes)
        {
            var edges = _edges.Where(kv => _nodes.ContainsKey(kv.Value.From) && _nodes.ContainsKey(kv.Value.To)).Select(kv =>
            {
                var (from, to, kind, _, certainty, facts, labels) = kv.Value;
                var derived = certainty ?? Certainty(facts);
                var label = labels.Count == 1 ? labels.First() : $"{facts.Count} {kind}";
                return new ArchEdge(kv.Key, from, to, kind, facts.Count > 1 && labels.Count == 1 ? $"{label} ×{facts.Count}" : label, Math.Max(1, facts.Count), derived,
                    facts.Take(MaxFactsPerEdge).Select(f => new ArchFact(f.Record.Relation, Map.Ref(view, f.Record.From), f.Record.To is null ? null : Map.Ref(view, f.Record.To),
                        f.Record.Resolution, f.Record.UnresolvedReason, f.Record.Derivation, f.Record.Rule is null ? null : $"{f.Record.Rule.Name} {f.Record.Rule.Version}", Map.Evidence(f.Artifact, f.Record.Evidence), ViaOf(f.Record))).ToList());
            }).OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
            return new ArchitectureGraph(level, focus, title, crumbs, _nodes.Values.OrderBy(n => n.Id, StringComparer.Ordinal).ToList(), edges, notes);
        }

        private static string Certainty(List<Occurrence<EdgeRecord>> facts) =>
            facts.Count == 0 ? "candidate"
            : facts.Any(f => f.Record.Resolution == "unresolved" && f.Record.UnresolvedReason != "configuration") ? "unresolved"
            : facts.Any(f => f.Record.Resolution == "candidate") ? "candidate"
            : facts.Any(f => f.Record.Resolution == "static-target") ? "static"
            : facts.Any(f => f.Record.Resolution == "unresolved") ? "unbound"
            : "proven";

        private static int Rank(string? c) => c switch { "broken" => 5, "unresolved" => 4, "candidate" => 3, "unbound" => 2, "static" => 1, _ => 0 };
    }
}
