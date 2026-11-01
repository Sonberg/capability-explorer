using System.ComponentModel;
using CapabilityExplorer.Interpretation;
using Microsoft.Extensions.AI;

namespace CapabilityExplorer.Explorer;

/// <summary>The functions the model may call to look further. Each returns numbered items (see <see cref="ChatContext"/>) or a plain "not found".</summary>
internal static class ChatTools
{
    private const int MaxResult = 6000;

    public static List<AITool> Create(ChatContext ctx, Intelligence intelligence, CancellationToken ct)
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                ([Description("name fragment to look for")] string query, [Description("optional node kind, e.g. Method, Type, Endpoint")] string? kind = null) =>
                    Limit(Numbered(ctx, Queries.Search(ctx.View, query, kind, null, 10).Select(h => h.Key), "tool")),
                "search_symbols", "Find symbols (types, methods, endpoints, tables...) whose name or key contains the query. Returns numbered items."),
            AIFunctionFactory.Create(
                ([Description("symbol key, or exact name")] string symbol) => Limit(GetSymbol(ctx, symbol)),
                "get_symbol", "Get one symbol with what it calls, reads, writes, requests and what uses it."),
            AIFunctionFactory.Create(
                ([Description("symbol key, or exact name")] string symbol, [Description("1 = direct neighbours (default), 2 = their neighbours too")] int depth = 1) =>
                    Limit(GetConnectedCode(ctx, symbol, depth)),
                "get_connected_code", "Get the code connected to a symbol: what it calls, reads, writes and requests, and what uses it, each with its source. Use it on what search finds, to see how the code works."),
            AIFunctionFactory.Create(
                ([Description("a name, vendor, table or channel")] string from, [Description("a name, vendor, table or channel")] string to) => Limit(FindPath(ctx, from, to)),
                "find_path", "Find the shortest chain of relations between two things, each a name, a vendor (Klarna), a table or a channel. Answers 'how do A and B interact'."),
            AIFunctionFactory.Create(
                ([Description("optional vendor, e.g. Klarna")] string? vendor = null) => Limit(ListIntegrations(ctx, vendor)),
                "list_integrations", "List the third parties the code calls over HTTP and who calls them. With a vendor, lists that vendor's requests."),
        };

        if (intelligence.Store is { } store && intelligence.Embeddings is { } model)
        {
            tools.Add(AIFunctionFactory.Create(
                async ([Description("what the code does, in words")] string query) =>
                {
                    try
                    {
                        var vector = (await model.EmbedAsync([query.Length > 500 ? query[..500] : query], ct))[0];
                        return Limit(Numbered(ctx, ExplorerApi.InView(ctx.View, await store.NearestAsync(vector, model.Id, 30, ct), 8).Select(h => h.Key), "semantic"));
                    }
                    catch (Exception e) when (e is InterpretationUnavailableException or Npgsql.NpgsqlException)
                    {
                        return $"error: {e.Message}";
                    }
                },
                "semantic_search", "Find functions by meaning (embedding similarity), for when you do not know their names."));
        }

        return tools;
    }

    private static string Limit(string result) => result.Length <= MaxResult ? result : result[..MaxResult] + "…";

    private static string Numbered(ChatContext ctx, IEnumerable<string> keys, string found)
    {
        var lines = keys.Select(k => (Number: ctx.AddSymbol(ChatContext.TotalLimit, k, found), Item: ctx.Items.FirstOrDefault(i => i.Source.Key == k)))
            .Where(x => x.Number > 0 && x.Item.Source is not null).Select(x => $"[{x.Number}] {x.Item.Source.Kind} {x.Item.Source.Name} ({x.Item.Source.Repository})").ToList();
        return lines.Count == 0 ? "no matches" : string.Join('\n', lines);
    }

    private static string GetSymbol(ChatContext ctx, string symbol)
    {
        var view = ctx.View;
        var key = view.Node(symbol) is not null ? symbol : Queries.Search(view, symbol, null, null, 5).FirstOrDefault(h => string.Equals(h.Name, symbol, StringComparison.OrdinalIgnoreCase))?.Key;
        if (key is null || Queries.Symbol(view, key) is not { } d)
        {
            return "no such symbol; try search_symbols";
        }

        var n = ctx.AddSymbol(ChatContext.TotalLimit, key, "tool", edges: 20, chars: 1800, code: true);
        return n == 0 ? "item limit reached; answer from what you have" : $"[{n}] {ChatContext.Describe(d, 20, 1800)}{ctx.Snippet(d.Node)}";
    }

    private static string GetConnectedCode(ChatContext ctx, string symbol, int depth)
    {
        var view = ctx.View;
        var key = view.Node(symbol) is not null ? symbol : Queries.Search(view, symbol, null, null, 5).FirstOrDefault(h => string.Equals(h.Name, symbol, StringComparison.OrdinalIgnoreCase))?.Key;
        if (key is null || ctx.AddSymbol(ChatContext.TotalLimit, key, "tool", code: true) == 0)
        {
            return "no such symbol (or item limit reached); try search_symbols";
        }

        var connected = ctx.AddConnected(ChatContext.TotalLimit, key, Math.Clamp(depth, 1, 2), 8, code: true);
        var lines = connected.Select(c => $"[{c.N}] ({c.Relation}) {ctx.Items[c.N - 1].Text}").Prepend($"[{ctx.Items.First(i => i.Source.Key == key).Source.N}] {ctx.Items.First(i => i.Source.Key == key).Text}");
        return connected.Count == 0 ? string.Join('\n', lines) + "\nno connected code in the analysed revisions" : string.Join("\n\n", lines);
    }

    private static string FindPath(ChatContext ctx, string from, string to)
    {
        var a = ctx.Anchors(from);
        var b = ctx.Anchors(to);
        if (a.Count == 0 || b.Count == 0)
        {
            return $"nothing in the analysed code matches {(a.Count == 0 ? from : to)}";
        }

        if (ctx.AddPath(ChatContext.TotalLimit, from, a, to, b) is { } hops && ctx.Items.First(i => i.Source.Key == ChatContext.PathKey(from, to)) is { } item)
        {
            hops.Take(6).ToList().ForEach(k => ctx.AddSymbol(ChatContext.TotalLimit, k, "path", code: true));
            return $"[{item.Source.N}] {item.Text}";
        }

        return $"no chain of up to {GraphPaths.MaxHops} relations connects {from} and {to} (not observed)";
    }

    private static string ListIntegrations(ChatContext ctx, string? vendor)
    {
        var links = ctx.Integrations.Http.Where(l => vendor is not { Length: > 0 } || l.Group.Contains(vendor, StringComparison.OrdinalIgnoreCase)).ToList();
        if (vendor is not { Length: > 0 })
        {
            return string.Join('\n', links.GroupBy(l => l.Group, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Take(30)
                .Select(g => $"{g.Key}: {g.Count()} requests from {string.Join(", ", g.Select(l => l.Request.RepositoryId).Distinct(StringComparer.Ordinal))}"));
        }

        if (links.Count == 0)
        {
            return $"no HTTP calls to {vendor} observed";
        }

        var text = $"Outbound HTTP to {links[0].Group}: {links.Count} requests. " + string.Join("; ", links.Take(15).Select(l => $"{l.Request.From.Name} ({l.Request.RepositoryId}) {l.Request.To?.Name}"));
        var n = ctx.Add(ChatContext.TotalLimit, "integration:" + vendor.ToLowerInvariant(), "Integration", links[0].Group, string.Join(", ", links.Select(l => l.Request.RepositoryId).Distinct(StringComparer.Ordinal)), "tool", text);
        return n == 0 ? "item limit reached" : $"[{n}] {text}";
    }
}
