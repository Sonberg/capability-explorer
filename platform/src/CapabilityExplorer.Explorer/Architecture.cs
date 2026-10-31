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
    IReadOnlyList<string> Badges);

/// <summary>One underlying fact behind an aggregated canvas edge.</summary>
public sealed record ArchFact(string Relation, SymbolRef From, SymbolRef? To, string Resolution, string? UnresolvedReason, string Derivation, string? Rule, IReadOnlyList<EvidenceDto> Evidence);

/// <summary>
/// An aggregated edge. Certainty: "proven" (every fact resolved), "static" (static target only), "unbound" (target
/// known by a configuration name only), "bound" (named by the service map, endpoint found), "broken" (named by the service map, endpoint missing),
/// "candidate" (a match by route or name, or a candidate fact), "unresolved".
/// </summary>
public sealed record ArchEdge(string Id, string From, string To, string Kind, string Label, int Count, string Certainty, IReadOnlyList<ArchFact> Facts);

public sealed record Crumb(string Label, string Level, string? Focus);

public sealed record ArchitectureGraph(string Level, string? Focus, string Title, IReadOnlyList<Crumb> Breadcrumbs, IReadOnlyList<ArchNode> Nodes, IReadOnlyList<ArchEdge> Edges, IReadOnlyList<string> Notes);

/// <summary>
/// Builds the canvas at three levels of abstraction from the facts in a view:
/// system (repositories and shared resources), repository (components grouped by unit) and component
/// (members and their direct neighbours). Nothing is drawn that the artifacts do not state, except
/// cross-repository route matches and same-name resources, which are labelled candidates.
/// </summary>
public static class Architecture
{
    private const int MaxFactsPerEdge = 40;
    private static readonly HashSet<string> CodeRelations = ["CALLS", "REFERENCES", "IMPLEMENTS", "INHERITS", "REGISTERS"];
    private static readonly HashSet<string> DataRelations = ["READS", "WRITES", "MAPS_TO"];
    private static readonly HashSet<string> MessageRelations = ["PUBLISHES", "CONSUMES"];

    public static ArchitectureGraph Build(ViewIndex view, string level, string? focus) => level switch
    {
        "repository" when focus is not null => Repository(view, focus),
        "component" when focus is not null => Component(view, focus),
        _ => System(view),
    };

    // ------------------------------------------------------------------ system

    private static ArchitectureGraph System(ViewIndex view)
    {
        var b = new Builder(view);
        foreach (var repo in view.Artifacts.Select(a => a.RepositoryId).Distinct().Order(StringComparer.Ordinal))
        {
            b.Node(RepoNode(view, repo));
        }

        foreach (var e in view.Edges)
        {
            var fromRepo = e.Artifact.RepositoryId;
            var r = e.Record;
            if (r.Relation == "REQUESTS")
            {
                foreach (var (target, certainty, missing) in HttpTargets(view, e))
                {
                    if (missing is not null)
                    {
                        var route = Route(missing);
                        var id = $"broken:{missing}";
                        b.Node(new ArchNode(id, "unmatched", route, $"bound to {missing.Split('|')[1]}, but no such endpoint", "Broken", null, null, null, ["broken"]));
                        b.Edge($"repo:{fromRepo}", id, "http", route, "broken", e);
                    }
                    else if (target is null)
                    {
                        var route = r.To is null ? "dynamic request" : Route(r.To);
                        b.Node(new ArchNode($"unmatched:{route}", "unmatched", route, "no endpoint in scope", "Unmatched", null, null, null, ["unmatched"]));
                        b.Edge($"repo:{fromRepo}", $"unmatched:{route}", "http", route, "unresolved", e);
                    }
                    else
                    {
                        b.Edge($"repo:{fromRepo}", $"repo:{target.Artifact.RepositoryId}", "http", Route(target.Record.SymbolKey), certainty, e);
                    }
                }
            }
            else if (r.To is not null && (DataRelations.Contains(r.Relation) || MessageRelations.Contains(r.Relation)) && ResourceKey(r.To) is { } res)
            {
                b.Node(ResourceNode(view, res));
                var consume = r.Relation is "CONSUMES" or "READS";
                b.Edge(consume ? $"res:{res}" : $"repo:{fromRepo}", consume ? $"repo:{fromRepo}" : $"res:{res}", KindOf(r.Relation), r.Relation.ToLowerInvariant().Replace('_', ' '), null, e);
            }
            else if (r.Relation == "DECLARES" && r.To is not null && view.Node(r.From)?.Record.NodeKind == "Project" && view.Node(r.To)?.Record.NodeKind is "Table" or "View")
            {
                b.Node(ResourceNode(view, r.To));
                b.Edge($"repo:{fromRepo}", $"res:{r.To}", "declares", "declares", null, e);
            }
        }

        b.SameNameLinks();
        return b.Graph("system", null, "System", [new Crumb("System", "system", null)],
            ["Repositories are connected through candidate HTTP route matches until base URLs are bound.", "Resources with the same name but different keys are separate until a binding proves otherwise."]);
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

    private static ArchitectureGraph Repository(ViewIndex view, string repo)
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
                    foreach (var (target, certainty) in HttpTargets(view, e).Where(t => t.Target?.Artifact.RepositoryId == repo))
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
                foreach (var (target, certainty) in HttpTargets(view, e))
                {
                    if (target is null)
                    {
                        var route = r.To is null ? "dynamic request" : Route(r.To);
                        b.Node(new ArchNode($"unmatched:{route}", "unmatched", route, "no endpoint in scope", null, null, null, null, ["unmatched"]));
                        b.Edge(from.Id, $"unmatched:{route}", "http", route, "unresolved", e);
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
                b.Edge(consume ? $"res:{res}" : from.Id, consume ? from.Id : $"res:{res}", KindOf(r.Relation), r.Relation.ToLowerInvariant().Replace('_', ' '), null, e);
            }
            else if (r.To is not null && CodeRelations.Contains(r.Relation) && ComponentOf(view, r.To) is { } to && to.Id != from.Id && to.Node.RepositoryId == repo)
            {
                b.Edge(from.Id, to.Id, KindOf(r.Relation), r.Relation.ToLowerInvariant(), null, e);
            }
        }

        b.SameNameLinks();
        return b.Graph("repository", repo, repo, [new Crumb("System", "system", null), new Crumb(repo, "repository", repo)],
            ["Components are C# types, TypeScript modules and SQL objects, grouped by unit.", "Calls into external libraries are shown one level down."]);
    }

    private static ArchNode ExternalRepo(ViewIndex view, string repo) => RepoNode(view, repo) with { Category = "external", Group = "Other systems" };

    // ------------------------------------------------------------------ component

    private static ArchitectureGraph Component(ViewIndex view, string focus)
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
                b.Edge(fromId, id, KindOf(r.Relation), r.Relation.ToLowerInvariant(), "unresolved", e);
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
            b.Edge(reverse ? toId : fromId, reverse ? fromId : toId, KindOf(r.Relation), r.Relation.ToLowerInvariant().Replace('_', ' '), null, e);
        }

        return b.Graph("component", focus, title,
            [new Crumb("System", "system", null), new Crumb(repo, "repository", repo), new Crumb(title, "component", focus)],
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
            unit, key, repo, new Drill("component", key), calcs > 0 ? [$"{calcs} calculation{(calcs == 1 ? "" : "s")}"] : []));
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
                        f.Record.Resolution, f.Record.UnresolvedReason, f.Record.Derivation, f.Record.Rule is null ? null : $"{f.Record.Rule.Name} {f.Record.Rule.Version}", Map.Evidence(f.Artifact, f.Record.Evidence))).ToList());
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
