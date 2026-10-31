using System.Text.Json;
using CapabilityExplorer.Contracts;

namespace CapabilityExplorer.Explorer;

public sealed record ContinuityCandidate(NodeDto From, NodeDto To, IReadOnlyList<string> Reasons, bool Ambiguous);

public sealed record AttributeChange(string Name, string? From, string? To);

public sealed record CalculationChange(string FromKey, string ToKey, string Name, bool LogicChanged, IReadOnlyList<AttributeChange> Attributes, IReadOnlyList<SymbolRef> PossibleConsumers);

public sealed record EdgeChange(string Relation, string From, string? To, string Resolution, string Change);

public sealed record UnitChange(
    string Unit,
    string UnitPath,
    string FromStatus,
    string ToStatus,
    IReadOnlyList<NodeDto> Added,
    IReadOnlyList<NodeDto> Removed,
    IReadOnlyList<NodeDto> NotObserved,
    IReadOnlyList<ContinuityCandidate> Continuity,
    IReadOnlyList<CalculationChange> Calculations,
    IReadOnlyList<EdgeChange> Edges,
    bool InputUnchanged);

public sealed record ChangeReport(string RepositoryId, RevisionInfo From, RevisionInfo To, IReadOnlyList<UnitChange> Units, IReadOnlyList<string> AddedUnits, IReadOnlyList<string> RemovedUnits);

/// <summary>
/// A first diff between two revisions of one repository. Continuity (renames, moves) is only ever a candidate
/// with stated reasons; symbols missing from a partial artifact are "not observed", never "removed".
/// Stage 5 replaces this with proper lineage matching.
/// </summary>
public static class Changes
{
    public static ChangeReport? Compare(ExplorerIndex index, string repositoryId, string from, string to)
    {
        var a = index.FindRevision(repositoryId, from);
        var b = index.FindRevision(repositoryId, to);
        if (a is null || b is null)
        {
            return null;
        }

        var aUnits = a.ArtifactIds.Select(id => index.Artifacts[id]).ToDictionary(x => x.Artifact.Header.Unit.Path, StringComparer.Ordinal);
        var bUnits = b.ArtifactIds.Select(id => index.Artifacts[id]).ToDictionary(x => x.Artifact.Header.Unit.Path, StringComparer.Ordinal);
        var toView = index.View($"{repositoryId}:{b.CommitSha}");
        var units = aUnits.Keys.Intersect(bUnits.Keys).Order(StringComparer.Ordinal)
            .Select(path => Unit(aUnits[path], bUnits[path], toView))
            .ToList();
        return new ChangeReport(
            repositoryId, a, b, units,
            bUnits.Keys.Except(aUnits.Keys).Order(StringComparer.Ordinal).ToList(),
            aUnits.Keys.Except(bUnits.Keys).Order(StringComparer.Ordinal).ToList());
    }

    private static UnitChange Unit(LoadedArtifact a, LoadedArtifact b, ViewIndex toView)
    {
        var aNodes = a.Artifact.Nodes.ToDictionary(n => n.SymbolKey, StringComparer.Ordinal);
        var bNodes = b.Artifact.Nodes.ToDictionary(n => n.SymbolKey, StringComparer.Ordinal);
        var aCalcs = a.Artifact.Calculations.ToDictionary(c => c.SymbolKey, StringComparer.Ordinal);
        var bCalcs = b.Artifact.Calculations.ToDictionary(c => c.SymbolKey, StringComparer.Ordinal);
        NodeDto Dto(LoadedArtifact art, NodeRecord n) => Map.Node(new Occurrence<NodeRecord>(art, n));

        var missing = aNodes.Keys.Except(bNodes.Keys).Select(k => aNodes[k]).ToList();
        var added = bNodes.Keys.Except(aNodes.Keys).Select(k => bNodes[k]).ToList();

        // Continuity candidates between removed and added symbols of the same kind.
        var continuity = new List<ContinuityCandidate>();
        var mapped = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var gone in missing)
        {
            var scored = added.Where(n => n.NodeKind == gone.NodeKind).Select(n => (Node: n, Reasons: Reasons(gone, n, aCalcs, bCalcs))).Where(x => x.Reasons.Count > 0).ToList();
            if (scored.Count == 0)
            {
                continue;
            }

            var best = scored.MaxBy(x => x.Reasons.Count);
            var ambiguous = scored.Count(x => x.Reasons.Count == best.Reasons.Count) > 1;
            continuity.Add(new ContinuityCandidate(Dto(a, gone), Dto(b, best.Node), best.Reasons, ambiguous));
            if (!ambiguous)
            {
                mapped[gone.SymbolKey] = best.Node.SymbolKey;
            }
        }

        var continued = continuity.Where(c => !c.Ambiguous).ToList();
        var removedOrUnseen = missing.Where(n => !continued.Any(c => c.From.Key == n.SymbolKey)).ToList();
        var partial = b.Artifact.Complete.Status == "partial";

        // Calculations: same key, or continued key.
        var calcChanges = new List<CalculationChange>();
        foreach (var (key, ca) in aCalcs)
        {
            var toKey = mapped.GetValueOrDefault(key, key);
            if (!bCalcs.TryGetValue(toKey, out var cb))
            {
                continue;
            }

            var attrs = AttributeChanges(ca.Raw, cb.Raw);
            var logic = Hash(ca.Raw) != Hash(cb.Raw);
            if (logic || attrs.Count > 0)
            {
                calcChanges.Add(new CalculationChange(key, toKey, bNodes.TryGetValue(toKey, out var n) ? n.Name : Map.DisplayName(toKey), logic, attrs, Consumers(toView, toKey)));
            }
        }

        // Edges, after mapping continued keys so a rename does not show as removed + added edges.
        string M(string k) => mapped.GetValueOrDefault(k, k);
        static string EdgeKey(string from, string rel, string? to, string? reason) => $"{from}\u0001{rel}\u0001{to ?? "?" + reason}";
        var aEdges = a.Artifact.Edges.GroupBy(e => EdgeKey(M(e.From), e.Relation, e.To is null ? null : M(e.To), e.UnresolvedReason)).ToDictionary(g => g.Key, g => g.First());
        var bEdges = b.Artifact.Edges.GroupBy(e => EdgeKey(e.From, e.Relation, e.To, e.UnresolvedReason)).ToDictionary(g => g.Key, g => g.First());
        var edgeChanges = bEdges.Keys.Except(aEdges.Keys).Select(k => Edge(bEdges[k], "added"))
            .Concat(aEdges.Keys.Except(bEdges.Keys).Select(k => Edge(aEdges[k], partial ? "notObserved" : "removed")))
            .Concat(aEdges.Keys.Intersect(bEdges.Keys).Where(k => aEdges[k].Resolution != bEdges[k].Resolution).Select(k => Edge(bEdges[k], $"resolution {aEdges[k].Resolution} → {bEdges[k].Resolution}")))
            .OrderBy(e => e.Change, StringComparer.Ordinal).ThenBy(e => e.From, StringComparer.Ordinal).ToList();

        return new UnitChange(
            b.Artifact.Header.Unit.Name,
            b.Artifact.Header.Unit.Path,
            a.Artifact.Complete.Status,
            b.Artifact.Complete.Status,
            added.Where(n => !continued.Any(c => c.To.Key == n.SymbolKey)).Select(n => Dto(b, n)).ToList(),
            partial ? [] : removedOrUnseen.Select(n => Dto(a, n)).ToList(),
            partial ? removedOrUnseen.Select(n => Dto(a, n)).ToList() : [],
            continuity,
            calcChanges,
            edgeChanges,
            a.Artifact.Header.InputHash == b.Artifact.Header.InputHash);
    }

    private static List<string> Reasons(NodeRecord gone, NodeRecord candidate, Dictionary<string, CalculationRecord> aCalcs, Dictionary<string, CalculationRecord> bCalcs)
    {
        var reasons = new List<string>();
        if (aCalcs.TryGetValue(gone.SymbolKey, out var ca) && bCalcs.TryGetValue(candidate.SymbolKey, out var cb) && Hash(ca.Raw) is { } h && h == Hash(cb.Raw))
        {
            reasons.Add("identical normalised calculation");
        }

        if (gone.Name == candidate.Name)
        {
            reasons.Add("same name (moved)");
        }

        if (gone.SymbolKey.StartsWith("cs|", StringComparison.Ordinal) && Parameters(gone.SymbolKey) is { } p && p == Parameters(candidate.SymbolKey) && ContainingType(gone.SymbolKey) == ContainingType(candidate.SymbolKey))
        {
            reasons.Add("same containing type and parameter list (renamed)");
        }

        return reasons;
    }

    private static string? Parameters(string key) => key.IndexOf('(', StringComparison.Ordinal) is var i and >= 0 ? key[i..] : null;

    private static string ContainingType(string key)
    {
        var doc = key[(key.LastIndexOf('|') + 1)..];
        var head = doc.IndexOf('(', StringComparison.Ordinal) is var i and >= 0 ? doc[..i] : doc;
        return head.Contains('.', StringComparison.Ordinal) ? head[..head.LastIndexOf('.')] : head;
    }

    private static string? Hash(JsonElement calc) => calc.TryGetProperty("irHash", out var h) ? h.GetString() : null;

    private static List<AttributeChange> AttributeChanges(JsonElement a, JsonElement b)
    {
        static string? Describe(JsonElement? attr) => attr is { } x
            ? $"{x.GetProperty("status").GetString()}{(x.TryGetProperty("value", out var v) ? $": {v}" : "")}{(x.TryGetProperty("detail", out var d) ? $" {d.GetRawText()}" : "")}"
            : null;
        var aa = a.GetProperty("attributes");
        var ba = b.GetProperty("attributes");
        var names = aa.EnumerateObject().Select(p => p.Name).Union(ba.EnumerateObject().Select(p => p.Name)).Order(StringComparer.Ordinal);
        return names.Select(n => new AttributeChange(n, Describe(aa.TryGetProperty(n, out var x) ? x : null), Describe(ba.TryGetProperty(n, out var y) ? y : null)))
            .Where(c => c.From != c.To).ToList();
    }

    /// <summary>
    /// Static consumers in the new revision: direct callers, callers through an implemented interface member,
    /// and the endpoints those callers handle. A static path is not proof of runtime impact.
    /// </summary>
    private static List<SymbolRef> Consumers(ViewIndex view, string key)
    {
        var targets = new HashSet<string>(StringComparer.Ordinal) { key };
        foreach (var e in view.Outgoing[key].Where(e => e.Record.Relation == "IMPLEMENTS" && e.Record.To is not null))
        {
            targets.Add(e.Record.To!);
        }

        var callers = targets.SelectMany(t => view.Incoming[t]).Where(e => e.Record.Relation == "CALLS").Select(e => e.Record.From).Distinct(StringComparer.Ordinal).ToList();
        var endpoints = callers.SelectMany(c => view.Outgoing[c]).Where(e => e.Record.Relation == "HANDLES" && e.Record.To is not null).Select(e => e.Record.To!);
        return callers.Concat(endpoints).Distinct(StringComparer.Ordinal).Select(k => Map.Ref(view, k)).ToList();
    }

    private static EdgeChange Edge(EdgeRecord e, string change) => new(e.Relation, e.From, e.To, e.Resolution, change);
}
