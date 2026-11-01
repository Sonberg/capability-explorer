namespace CapabilityExplorer.Explorer;

/// <summary>Chains of relations between code symbols, for questions about how two things connect.</summary>
internal static class GraphPaths
{
    public const int MaxHops = 6;
    private const int MaxVisited = 20000;

    /// <summary>Name and repository, so a chain reads as a story across repositories.</summary>
    public static string Label(ViewIndex view, string key) =>
        view.Node(key) is { } n ? $"{n.Record.Name} ({n.Artifact.RepositoryId})" : Map.DisplayName(key);

    /// <summary>
    /// Shortest chain of relations (either direction) from any of <paramref name="from"/> to any of <paramref name="to"/>.
    /// DECLARES is skipped (every type "connects" to its members), and neither undeclared external symbols nor heavily shared
    /// symbols are walked through, or everything would connect through the standard library. Shared channels and tables are.
    /// </summary>
    public static List<(string Key, string Via)>? Shortest(
        ViewIndex view, IReadOnlyList<string> from, HashSet<string> to, IReadOnlyDictionary<string, List<(string To, string Via)>>? links = null)
    {
        var parent = new Dictionary<string, (string Prev, string Via)>(StringComparer.Ordinal);
        var queue = new Queue<(string Key, int Depth)>();
        foreach (var start in from.Where(k => !to.Contains(k)))
        {
            parent[start] = ("", "");
            queue.Enqueue((start, 0));
        }

        static bool Walkable(ViewIndex v, string k) =>
            k.StartsWith("chan|", StringComparison.Ordinal) || k.StartsWith("sql|", StringComparison.Ordinal) || (v.Node(k) is not null && v.Incoming[k].Count() <= 60);

        while (queue.Count > 0 && parent.Count < MaxVisited)
        {
            var (key, depth) = queue.Dequeue();
            if (to.Contains(key))
            {
                var hops = new List<(string, string)>();
                for (var k = key; k != ""; k = parent[k].Prev)
                {
                    hops.Add((k, parent[k].Via));
                }

                hops.Reverse();
                return hops;
            }

            if (depth >= MaxHops || (depth > 0 && !Walkable(view, key)))
            {
                continue;
            }

            foreach (var e in view.Outgoing[key].Where(e => e.Record.Relation != "DECLARES" && e.Record.To is not null))
            {
                if (parent.TryAdd(e.Record.To!, (key, $"-{e.Record.Relation}->")))
                {
                    queue.Enqueue((e.Record.To!, depth + 1));
                }
            }

            foreach (var e in view.Incoming[key].Where(e => e.Record.Relation != "DECLARES"))
            {
                if (parent.TryAdd(e.Record.From, (key, $"<-{e.Record.Relation}-")))
                {
                    queue.Enqueue((e.Record.From, depth + 1));
                }
            }

            // Across repositories a request reaches its endpoint only through the route match, which is not an edge in the artifacts.
            foreach (var (next, via) in links is not null && links.TryGetValue(key, out var extra) ? extra : [])
            {
                if (parent.TryAdd(next, (key, via)))
                {
                    queue.Enqueue((next, depth + 1));
                }
            }
        }

        return null;
    }
}
