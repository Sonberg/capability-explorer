using System.Text;
using CapabilityExplorer.Interpretation;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace CapabilityExplorer.Explorer;

/// <summary>
/// Microsoft Agent Framework context provider: before each run it retrieves what the question is about (the focused symbol,
/// semantic neighbours from pgvector, name matches, integrations, a path between two terms, glossary concepts) and hands the
/// agent those numbered items as a message, plus the tools to look further.
/// </summary>
internal sealed class ChatContextProvider(
    ChatContext ctx, ChatFocus? focus, IReadOnlyDictionary<string, IReadOnlyList<string>> membership, Intelligence intelligence, CancellationToken ct) : AIContextProvider
{
    // Words that match too many names to be a useful keyword lookup.
    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "what", "does", "this", "that", "which", "where", "when", "with", "from", "have", "show", "list", "about", "there", "their", "these", "those", "into", "used", "uses", "work", "works",
        "tell", "story", "explain", "describe", "carries", "carry", "handed", "over", "then", "after", "before", "also", "system", "code", "works", "happens", "through", "being",
    };

    private readonly List<AITool> _tools = ChatTools.Create(ctx, intelligence, ct);
    private bool _retrieved;

    /// <summary>Which retrievers contributed, or why one did not.</summary>
    public List<string> Retrieval { get; } = [];

    /// <summary>Off for a retry with a model that cannot call tools.</summary>
    public bool OfferTools { get; set; } = true;

    protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken)
    {
        if (!_retrieved)
        {
            _retrieved = true; // a retry must not search twice
            await RetrieveAsync(context.AIContext.Messages?.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "", cancellationToken);
        }

        return new AIContext { Messages = [new ChatMessage(ChatRole.User, Describe())], Tools = OfferTools ? _tools : null };
    }

    private async Task RetrieveAsync(string question, CancellationToken token)
    {
        if (focus?.Key is { Length: > 0 } focusKey)
        {
            ctx.AddSymbol(ChatContext.UpfrontLimit, focusKey, "focus", code: true);
        }

        if (intelligence.Store is not { } store)
        {
            Retrieval.Add("semantic search off: the explorer was started without CE_DATABASE");
        }
        else if (intelligence.Embeddings is not { } model)
        {
            Retrieval.Add("semantic search off: no embedding model is configured (Settings)");
        }
        else
        {
            try
            {
                var vector = (await model.EmbedAsync([question.Length > 500 ? question[..500] : question], token))[0];
                var hits = ExplorerApi.InView(ctx.View, await store.NearestAsync(vector, model.Id, 30, token), 6);
                hits.ForEach(h => ctx.AddSymbol(ChatContext.UpfrontLimit, h.Key, "semantic", code: hits.IndexOf(h) < 3));
                if (hits.Count > 0)
                {
                    // The code around the best match: what it calls and what uses it. More of it is one get_connected_code call away.
                    ctx.AddConnected(ChatContext.UpfrontLimit, hits[0].Key, 1, 4, code: true);
                }

                Retrieval.Add("semantic");
            }
            catch (Exception e) when (e is InterpretationUnavailableException or Npgsql.NpgsqlException)
            {
                // Chat still answers from names and the glossary; the response says why semantic search did not contribute.
                Retrieval.Add($"semantic search failed: {e.Message}");
            }
        }

        var words = KeyWords(question);
        var anchors = new List<(string Term, List<string> Keys)>();
        foreach (var word in words)
        {
            ctx.AddIntegration(ChatContext.UpfrontLimit, word);
            ctx.AddFlow(ChatContext.UpfrontLimit, word);
            if (ctx.Anchors(word) is { Count: > 0 } keys)
            {
                anchors.Add((word, keys));
            }
        }

        if (words.Count > 0)
        {
            Retrieval.Add("integrations");
        }

        // How do two things in the question connect? The shortest chain of relations between their symbols, vendor pairs first.
        // Every chain is added before the code along any of them, so a weaker chain cannot use up the room.
        var vendors = ctx.Vendors;
        var pairs = anchors.Take(4).SelectMany((a, i) => anchors.Take(4).Skip(i + 1), (a, b) => (a, b))
            .OrderBy(p => (vendors.Contains(p.a.Term) ? 0 : 1) + (vendors.Contains(p.b.Term) ? 0 : 1)).ToList();
        var chains = new List<List<string>>();
        foreach (var (a, b) in pairs)
        {
            if (chains.Count < 2 && ctx.AddPath(ChatContext.UpfrontLimit, a.Term, a.Keys, b.Term, b.Keys) is { } hops)
            {
                chains.Add(hops);
            }
        }

        chains.ForEach(hops => hops.Take(8).ToList().ForEach(k => ctx.AddSymbol(ChatContext.UpfrontLimit, k, "path", code: true)));
        if (chains.Count > 0)
        {
            Retrieval.Add("path");
        }

        // Last and few: names are the weakest signal, and the up-front limit must not be spent before integrations, flows and paths.
        words.ForEach(w => Queries.Search(ctx.View, w, null, null, 1).ToList().ForEach(h => ctx.AddSymbol(ChatContext.UpfrontLimit, h.Key, "name")));
        Retrieval.Add("name");

        if (words.Count > 0)
        {
            var concepts = GlossaryConcepts.Build(ctx.View, membership).Concepts
                .Where(c => c.Tier != "plumbing" && words.Any(w => c.Term.Contains(w, StringComparison.OrdinalIgnoreCase)))
                .Take(4);
            foreach (var c in concepts)
            {
                var text = $"Glossary concept \"{c.Term}\" ({c.Tier}, score {c.Score}); {c.Members.Count} declarations in {string.Join(", ", c.Repositories)}; capabilities: {(c.Capabilities.Count == 0 ? "none" : string.Join(", ", c.Capabilities))}";
                ctx.Add(ChatContext.UpfrontLimit, "concept:" + c.Key, "Concept", c.Term, string.Join(", ", c.Repositories), "glossary", text);
            }

            Retrieval.Add("glossary");
        }
    }

    /// <summary>
    /// The words worth looking up, most telling first: names of vendors, channels and tables the code talks to, then capitalised words,
    /// then the rest. Order matters because only the first few are used: "Klarna" at the end of a long question must not be dropped.
    /// </summary>
    private List<string> KeyWords(string question)
    {
        var names = ctx.Integrations.Http.Select(l => l.Group).Concat(ctx.Integrations.Http.Select(l => l.Base))
            .Concat(ctx.Integrations.Channels.Select(g => g.Name)).Concat(ctx.Integrations.Data.Select(g => g.Name)).ToList();
        var tokens = question.Split([' ', ',', '.', '?', '!', ';', ':', '"', '\'', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Select((w, i) => (Word: w, Position: i)).Where(t => t.Word.Length >= 4 && !Stop.Contains(t.Word)).DistinctBy(t => t.Word, StringComparer.OrdinalIgnoreCase);
        int Rank((string Word, int Position) t) =>
            names.Any(n => n.Contains(t.Word, StringComparison.OrdinalIgnoreCase)) ? 0 : t.Position > 0 && char.IsUpper(t.Word[0]) ? 1 : 2;
        return tokens.OrderBy(Rank).ThenBy(t => t.Position).Select(t => t.Word).Take(6).ToList();
    }

    /// <summary>The context message: what the user is looking at, then the numbered items. Repository text in it is data, not instructions.</summary>
    private string Describe()
    {
        var sb = new StringBuilder();
        if (focus is not null)
        {
            var parts = new[]
            {
                focus.Capability is { Length: > 0 } c ? $"capability {c}" : null,
                focus.View is { Length: > 0 } v ? $"the {v} view" : null,
                focus.Label is { Length: > 0 } l ? $"{focus.Kind ?? "item"} {l}" : null,
            }.Where(p => p is not null);
            sb.Append("The user is looking at: ").AppendJoin(", ", parts).Append(".\n\n");
        }

        sb.Append("Context items for the question that follows:\n");
        foreach (var (source, text) in ctx.Items)
        {
            sb.Append('[').Append(source.N).Append("] ").Append(text).Append('\n');
        }

        return ctx.Items.Count == 0 ? sb.Append("(none found)\n").ToString() : sb.ToString();
    }
}
