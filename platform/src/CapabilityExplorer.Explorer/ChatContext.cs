using System.Text;

namespace CapabilityExplorer.Explorer;

/// <param name="Text">What the model was shown for this item (capped), so an answer can be checked against it.</param>
public sealed record ChatSource(int N, string Kind, string Key, string Name, string Repository, string Found, string Text = "");

/// <summary>
/// The numbered items of one question: what is retrieved up front and what tools find later share one numbering, so a
/// citation [n] means the same thing everywhere. Items are what the model may rely on; <see cref="Sources"/> is what the UI shows.
/// </summary>
internal sealed class ChatContext(ViewIndex view)
{
    public const int UpfrontLimit = 32;
    public const int TotalLimit = 40;
    private const int ItemChars = 700;
    private const int SnippetLines = 25;
    private const int SnippetChars = 900;
    private int _codeBudget = 24; // snippets are the expensive part of the prompt: small local models have small context windows

    private readonly Dictionary<string, int> _numbers = new(StringComparer.Ordinal);
    private readonly List<(ChatSource Source, string Text)> _items = [];
    private Integrations? _integrations;
    private Dictionary<string, List<(string To, string Via)>>? _requestLinks;

    public ViewIndex View => view;

    public IReadOnlyList<(ChatSource Source, string Text)> Items => _items;

    public IReadOnlyList<ChatSource> Sources => _items.Select(i => i.Source).ToList();

    public Integrations Integrations => _integrations ??= Queries.Integrations(view);

    /// <summary>Request → endpoint links from route matching and service bindings, both directions, labelled by how sure the match is.</summary>
    private Dictionary<string, List<(string To, string Via)>> RequestLinks => _requestLinks ??= BuildRequestLinks();

    private Dictionary<string, List<(string To, string Via)>> BuildRequestLinks()
    {
        var links = new Dictionary<string, List<(string To, string Via)>>(StringComparer.Ordinal);
        void Add(string from, string to, string via)
        {
            if (!links.TryGetValue(from, out var list))
            {
                links[from] = list = [];
            }

            list.Add((to, via));
        }

        foreach (var link in Integrations.Http.Where(l => l.Candidates.Count > 0))
        {
            var certain = link.Match is "binding" or "bound";
            foreach (var endpoint in link.Candidates.Where(c => view.Node(c.Key) is not null))
            {
                var route = link.Request.To?.Name ?? "";
                Add(link.Request.From.Key, endpoint.Key, certain ? $"-REQUESTS {route}->" : $"-REQUESTS {route} (route match only: a candidate)->");
                Add(endpoint.Key, link.Request.From.Key, certain ? $"<-REQUESTS {route}-" : $"<-REQUESTS {route} (route match only: a candidate)-");
            }
        }

        return links;
    }

    /// <summary>The item's number; the existing one if already added; 0 when the limit is reached.</summary>
    public int Add(int limit, string key, string kind, string name, string repository, string found, string text)
    {
        if (_numbers.TryGetValue(key, out var existing))
        {
            return existing;
        }

        if (_items.Count >= limit)
        {
            return 0;
        }

        _items.Add((new ChatSource(_items.Count + 1, kind, key, name, repository, found, text.Length <= 600 ? text : text[..600] + "…"), text));
        return _numbers[key] = _items.Count;
    }

    public int AddSymbol(int limit, string key, string found, int edges = 8, int chars = ItemChars, bool code = false) =>
        Queries.Symbol(view, key) is { } d
            ? Add(limit, key, d.Node.Kind, d.Node.Name, d.Node.RepositoryId, found, Describe(d, edges, chars) + (code ? Snippet(d.Node) : ""))
            : 0;

    /// <summary>
    /// The code a symbol is connected to: what it calls, reads, writes, requests, publishes or handles, and what uses it, out to
    /// <paramref name="depth"/> steps, each as a numbered item with its relation and (when the source is available) its first lines.
    /// Bounded by <paramref name="max"/>: "all connected code" of a busy function is most of the repository.
    /// </summary>
    public List<(int N, string Relation)> AddConnected(int limit, string key, int depth, int max, bool code)
    {
        var added = new List<(int N, string Relation)>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { key };
        var frontier = new List<string> { key };
        for (var level = 0; level < depth && added.Count < max && frontier.Count > 0; level++)
        {
            var next = new List<string>();
            foreach (var current in frontier)
            {
                if (Queries.Symbol(view, current) is not { } detail)
                {
                    continue;
                }

                var via = level == 0 ? "this" : GraphPaths.Label(view, current);
                var neighbours = detail.Outgoing.Where(e => e.To is { Declared: true } && e.Relation != "DECLARES").Select(e => (Ref: e.To!, Label: $"{via} -{e.Relation}-> it"))
                    .Concat(detail.Incoming.Where(e => e.From.Declared && e.Relation != "DECLARES").Select(e => (Ref: e.From, Label: $"it -{e.Relation}-> {via}")));
                foreach (var (other, label) in neighbours)
                {
                    if (added.Count >= max)
                    {
                        break;
                    }

                    if (seen.Add(other.Key) && view.Node(other.Key) is not null && AddSymbol(limit, other.Key, "connected", code: code) is var n and > 0)
                    {
                        added.Add((n, label));
                        next.Add(other.Key);
                    }
                }
            }

            frontier = next;
        }

        return added;
    }

    /// <summary>The first lines of the code a symbol was found in, read at the analysed commit. Repository text: data, not instructions.</summary>
    public string Snippet(NodeDto node)
    {
        if (_codeBudget <= 0 || view.Index.Git is not { } git || node.Evidence.Count == 0 || node.Kind is "Type" or "Table" or "File")
        {
            return "";
        }

        var e = node.Evidence[0];
        if (git.ReadFile(e.RepositoryId, e.CommitSha, e.Path) is not { } text)
        {
            return "";
        }

        var lines = text.Split('\n');
        var from = Math.Clamp(e.StartLine, 1, lines.Length);
        var to = Math.Min(lines.Length, Math.Min(Math.Max(e.EndLine, from), from + SnippetLines - 1));
        var code = string.Join('\n', lines[(from - 1)..to]).Trim();
        _codeBudget--;
        return "\nCode:\n" + (code.Length <= SnippetChars ? code : code[..SnippetChars] + "…");
    }

    /// <summary>Symbols that stand for a term: those named like it, the code calling a vendor of that name, and users of a shared channel or table of that name.</summary>
    public List<string> Anchors(string term)
    {
        var links = Integrations.Http.Where(l => l.Group.Contains(term, StringComparison.OrdinalIgnoreCase) || l.Base.Contains(term, StringComparison.OrdinalIgnoreCase));
        var resources = Integrations.Channels.Concat(Integrations.Data).Where(g => g.Name.Contains(term, StringComparison.OrdinalIgnoreCase)).Take(2);
        return links.Select(l => l.Request.From.Key).Where(k => view.Node(k) is not null)
            .Concat(resources.SelectMany(g => g.Keys.SelectMany(k => k.Edges.Select(e => e.From.Key))))
            .Concat(Queries.Search(view, term, null, null, 300).Where(h => !h.Generated && h.Name.Contains(term, StringComparison.OrdinalIgnoreCase) && h.Kind is "Type" or "Method" or "Function" or "Endpoint").Select(h => h.Key))
            .Distinct(StringComparer.Ordinal).Take(300).ToList(); // wide on purpose: the closest pair decides the path, and a name match list cut at 20 misses the pair
    }

    public void AddIntegration(int limit, string term)
    {
        var links = Integrations.Http.Where(l => l.Group.Contains(term, StringComparison.OrdinalIgnoreCase) || l.Base.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
        if (links.Count > 0)
        {
            var text = $"Outbound HTTP to {string.Join("/", links.Select(l => l.Group).Distinct(StringComparer.OrdinalIgnoreCase))}: {links.Count} requests. "
                + string.Join("; ", links.Take(8).Select(l => $"{l.Request.From.Name} ({l.Request.RepositoryId}) {l.Request.To?.Name}"));
            Add(limit, "integration:" + term.ToLowerInvariant(), "Integration", links[0].Group, string.Join(", ", links.Select(l => l.Request.RepositoryId).Distinct(StringComparer.Ordinal)), "integration", text);
            links.Select(l => l.Request.From.Key).Distinct(StringComparer.Ordinal).Take(3).ToList().ForEach(k => AddSymbol(limit, k, "integration"));
        }

        foreach (var g in Integrations.Channels.Concat(Integrations.Data).Where(g => g.Name.Contains(term, StringComparison.OrdinalIgnoreCase)).Take(2))
        {
            var users = g.Keys.SelectMany(k => k.Edges).Take(8).Select(e => $"{e.From.Name} {e.Relation}").Distinct(StringComparer.Ordinal);
            Add(limit, $"resource:{g.Kind}:{g.Name}", "Integration", g.Name, "", g.Kind, $"Shared {g.Kind} {g.Name}: {string.Join("; ", users)}");
        }
    }

    /// <summary>
    /// How a request from one repository reaches a vendor: the caller that requests an endpoint, the endpoint, the chain inside the
    /// handling repository, and the vendor call. Built from route matches and relations; route-only matches are marked as candidates.
    /// </summary>
    public void AddFlow(int limit, string term)
    {
        var vendorCallers = Integrations.Http.Where(l => l.Group.Contains(term, StringComparison.OrdinalIgnoreCase) || l.Base.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Where(l => view.Node(l.Request.From.Key) is not null).GroupBy(l => l.Request.From.Key, StringComparer.Ordinal).Take(6).ToList();
        var entries = Integrations.Http.Where(l => l.Candidates.Count > 0).SelectMany(l => l.Candidates.Select(c => (Endpoint: c.Key, Link: l)))
            .Where(x => view.Node(x.Endpoint) is not null).ToList();
        if (vendorCallers.Count == 0 || entries.Count == 0)
        {
            return;
        }

        var endpoints = entries.Select(x => x.Endpoint).Distinct(StringComparer.Ordinal).ToList();
        var lines = new List<string>();
        foreach (var caller in vendorCallers)
        {
            if (GraphPaths.Shortest(view, endpoints, [caller.Key], RequestLinks) is not { Count: > 1 } chain)
            {
                continue;
            }

            var first = entries.First(x => x.Endpoint == chain[0].Key).Link;
            var certain = first.Match is "binding" or "bound";
            var vendorCall = caller.First();
            lines.Add($"{GraphPaths.Label(view, first.Request.From.Key)} -REQUESTS {first.Request.To?.Name}{(certain ? "" : " (route match only: a candidate)")}-> "
                + string.Concat(chain.Select(h => $"{(h.Via.Length == 0 ? "" : h.Via + " ")}{GraphPaths.Label(view, h.Key)} ")).Trim()
                + $" -REQUESTS {vendorCall.Request.To?.Name}-> {vendorCall.Group}");
        }

        if (lines.Count > 0)
        {
            Add(limit, "flow:" + term.ToLowerInvariant(), "Flow", $"into {vendorCallers[0].First().Group}", "", "flow", $"Flows from another repository that end in a call to {vendorCallers[0].First().Group}:\n" + string.Join('\n', lines));
        }
    }

    /// <summary>Adds the shortest chain between two terms as an item. Returns the symbols along it (to read their code), or null if there is no chain.</summary>
    public List<string>? AddPath(int limit, string fromTerm, List<string> from, string toTerm, List<string> to)
    {
        if (GraphPaths.Shortest(view, from, to.ToHashSet(StringComparer.Ordinal), RequestLinks) is not { } hops)
        {
            return null;
        }

        var text = $"Chain from \"{fromTerm}\" to \"{toTerm}\" ({hops.Count - 1} hops): " + string.Concat(hops.Select(h => $"{h.Via} {GraphPaths.Label(view, h.Key)} ")).Trim();
        return Add(limit, PathKey(fromTerm, toTerm), "Path", $"{fromTerm} ↔ {toTerm}", "", "path", text) > 0 ? hops.Select(h => h.Key).ToList() : null;
    }

    /// <summary>Names of the third parties the code calls: what a "how do X and Y interact" question is about.</summary>
    public HashSet<string> Vendors => Integrations.Http.Select(l => l.Group).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static string PathKey(string from, string to) => $"path:{from}:{to}";

    public static string Describe(SymbolDetail d, int edges = 8, int chars = ItemChars)
    {
        static string Targets(IEnumerable<EdgeDto> list, bool outgoing, int limit) =>
            string.Join("; ", list.Take(limit).Select(e => $"{e.Relation} {(outgoing ? e.To?.Name : e.From.Name) ?? "?"}"));

        var sb = new StringBuilder($"{d.Node.Kind} {d.Node.Name}");
        if (d.Node.Signature is { Length: > 0 } sig)
        {
            sb.Append(' ').Append(sig);
        }

        sb.Append($" in {d.Node.RepositoryId}/{d.Node.Unit}");
        if (d.Outgoing.Count > 0)
        {
            sb.Append(". Outgoing: ").Append(Targets(d.Outgoing, true, edges));
        }

        if (d.Incoming.Count > 0)
        {
            sb.Append(". Incoming: ").Append(Targets(d.Incoming, false, edges));
        }

        if (d.Calculation is not null)
        {
            sb.Append(". Contains a calculation");
        }

        var text = sb.ToString();
        return text.Length <= chars ? text : text[..chars] + "…";
    }
}
