using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CapabilityExplorer.Contracts;

public sealed record Problem(int Line, string Message)
{
    public override string ToString() => Line > 0 ? $"line {Line}: {Message}" : Message;
}

/// <summary>A fully validated extractor artifact. Only <see cref="ArtifactValidator"/> creates these.</summary>
public sealed class Artifact
{
    internal Artifact(
        HeaderRecord header,
        CompleteRecord complete,
        IReadOnlyList<NodeRecord> nodes,
        IReadOnlyList<EdgeRecord> edges,
        IReadOnlyList<EvidenceRecord> evidence,
        IReadOnlyList<CalculationRecord> calculations,
        IReadOnlyList<DiagnosticRecord> diagnostics,
        string contentSha256)
    {
        Header = header;
        Complete = complete;
        Nodes = nodes;
        Edges = edges;
        Evidence = evidence;
        Calculations = calculations;
        Diagnostics = diagnostics;
        ContentSha256 = contentSha256;
    }

    public HeaderRecord Header { get; }
    public CompleteRecord Complete { get; }
    public IReadOnlyList<NodeRecord> Nodes { get; }
    public IReadOnlyList<EdgeRecord> Edges { get; }
    public IReadOnlyList<EvidenceRecord> Evidence { get; }
    public IReadOnlyList<CalculationRecord> Calculations { get; }
    public IReadOnlyList<DiagnosticRecord> Diagnostics { get; }

    /// <summary>sha256 of the artifact bytes. Same analysis key + different content means a non-deterministic extractor.</summary>
    public string ContentSha256 { get; }
}

public sealed record ValidationReport(Artifact? Artifact, IReadOnlyList<Problem> Problems)
{
    public bool IsValid => Problems.Count == 0 && Artifact is not null;
}

/// <summary>
/// Validates an artifact against the ce.v1 contract: JSON Schema per line, then the rules from schema/README.md
/// that JSON Schema cannot express (ordering, identity, references, counts, coverage, relation catalogue).
/// </summary>
public static class ArtifactValidator
{
    private static readonly Dictionary<string, string> IdPrefix = new(StringComparer.Ordinal)
    {
        ["node"] = "n:", ["edge"] = "e:", ["evidence"] = "ev:", ["calculation"] = "c:", ["diagnostic"] = "d:",
    };

    public static ValidationReport Validate(string jsonl)
    {
        var problems = new List<Problem>();
        var contentSha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(jsonl)));
        var lines = jsonl.Split('\n');
        var count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        if (count == 0)
        {
            return new ValidationReport(null, [new Problem(0, "empty artifact")]);
        }

        // 1. Every line: JSON + schema.
        var parsed = new List<(int Line, string Kind, JsonElement Element)>(count);
        var schema = ContractSchema.Instance;
        for (var i = 0; i < count; i++)
        {
            var line = i + 1;
            JsonElement element;
            try
            {
                using var doc = JsonDocument.Parse(lines[i]);
                element = doc.RootElement.Clone();
            }
            catch (JsonException e)
            {
                problems.Add(new Problem(line, $"invalid JSON: {e.Message}"));
                continue;
            }

            var errors = schema.Validate(element);
            if (errors.Count > 0)
            {
                problems.AddRange(errors.Select(e => new Problem(line, $"schema {e}")));
                continue;
            }

            parsed.Add((line, element.GetProperty("kind").GetString()!, element));
        }

        if (problems.Count > 0)
        {
            return new ValidationReport(null, problems);
        }

        // 2. Framing: one header first, one complete last.
        if (parsed[0].Kind != "header")
        {
            problems.Add(new Problem(1, "first record must be the header"));
        }

        if (parsed[^1].Kind != "complete")
        {
            problems.Add(new Problem(count, "last record must be 'complete' (artifact is truncated or unfinished)"));
        }

        foreach (var (line, kind, _) in parsed.Skip(1).SkipLast(1))
        {
            if (kind is "header" or "complete")
            {
                problems.Add(new Problem(line, $"'{kind}' may appear only once"));
            }
        }

        if (problems.Count > 0)
        {
            return new ValidationReport(null, problems);
        }

        var header = parsed[0].Element.Deserialize<HeaderRecord>(Json.Options)!;
        var complete = parsed[^1].Element.Deserialize<CompleteRecord>(Json.Options)!;
        var body = parsed.Skip(1).SkipLast(1).ToList();

        // 3. Identity.
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var nodes = new List<(int Line, NodeRecord Node)>();
        var edges = new List<(int Line, EdgeRecord Edge)>();
        var evidence = new List<(int Line, EvidenceRecord Evidence)>();
        var calculations = new List<(int Line, CalculationRecord Calculation)>();
        var diagnostics = new List<(int Line, DiagnosticRecord Diagnostic)>();
        foreach (var (line, kind, element) in body)
        {
            var id = element.GetProperty("id").GetString()!;
            if (!ids.Add(id))
            {
                problems.Add(new Problem(line, $"duplicate id {id}"));
            }

            if (!id.StartsWith(IdPrefix[kind], StringComparison.Ordinal))
            {
                problems.Add(new Problem(line, $"{kind} id must start with '{IdPrefix[kind]}'"));
            }

            switch (kind)
            {
                case "node": nodes.Add((line, element.Deserialize<NodeRecord>(Json.Options)!)); break;
                case "edge": edges.Add((line, element.Deserialize<EdgeRecord>(Json.Options)!)); break;
                case "evidence": evidence.Add((line, element.Deserialize<EvidenceRecord>(Json.Options)!)); break;
                case "diagnostic": diagnostics.Add((line, element.Deserialize<DiagnosticRecord>(Json.Options)!)); break;
                case "calculation":
                    calculations.Add((line, new CalculationRecord(id, element.GetProperty("symbolKey").GetString()!, element)));
                    break;
            }
        }

        var nodeKinds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (line, node) in nodes)
        {
            if (!nodeKinds.TryAdd(node.SymbolKey, node.NodeKind))
            {
                problems.Add(new Problem(line, $"duplicate node symbolKey {node.SymbolKey}"));
            }

            CheckKey(problems, line, node.SymbolKey);
            var owner = SymbolKey.OwningRepository(node.SymbolKey);
            if (owner is not null && owner != header.RepositoryId)
            {
                problems.Add(new Problem(line, $"node {node.SymbolKey} is declared in {header.RepositoryId} but its key names repository '{owner}'"));
            }
        }

        // 4. Evidence belongs to this exact revision.
        var evidenceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (line, ev) in evidence)
        {
            evidenceIds.Add(ev.Id);
            if (ev.RepositoryId != header.RepositoryId || ev.CommitSha != header.CommitSha)
            {
                problems.Add(new Problem(line, "evidence must reference the header's repository and commitSha"));
            }

            var s = ev.Span;
            if (s.EndLine < s.StartLine || (s.EndLine == s.StartLine && s.EndColumn < s.StartColumn))
            {
                problems.Add(new Problem(line, "span ends before it starts"));
            }
        }

        void RequireEvidence(int line, IReadOnlyList<string>? refs, string what, bool mandatory)
        {
            if (mandatory && (refs is null || refs.Count == 0))
            {
                problems.Add(new Problem(line, $"{what} without evidence"));
            }

            foreach (var r in refs ?? [])
            {
                if (!evidenceIds.Contains(r))
                {
                    problems.Add(new Problem(line, $"unknown evidence reference {r}"));
                }
            }
        }

        foreach (var (line, node) in nodes)
        {
            RequireEvidence(line, node.Evidence, "node", mandatory: true);
        }

        foreach (var (line, d) in diagnostics)
        {
            RequireEvidence(line, d.Evidence, "diagnostic", mandatory: false);
        }

        // 5. Edges originate in this unit; relation catalogue holds where both ends are known here.
        var relations = schema.Relations;
        foreach (var (line, edge) in edges)
        {
            RequireEvidence(line, edge.Evidence, "edge", mandatory: true);
            CheckKey(problems, line, edge.From);
            if (edge.To is not null)
            {
                CheckKey(problems, line, edge.To);
            }

            if (!nodeKinds.TryGetValue(edge.From, out var fromKind))
            {
                problems.Add(new Problem(line, $"edge source {edge.From} is not declared in this artifact"));
            }
            else if (!relations.AllowsFrom(edge.Relation, fromKind))
            {
                problems.Add(new Problem(line, $"{edge.Relation} may not start at a {fromKind}"));
            }

            if (edge.To is not null && nodeKinds.TryGetValue(edge.To, out var toKind) && !relations.AllowsTo(edge.Relation, toKind))
            {
                problems.Add(new Problem(line, $"{edge.Relation} may not end at a {toKind}"));
            }
        }

        // 6. Calculations: declared symbol, evidence everywhere, placeholders in scope.
        foreach (var (line, calc) in calculations)
        {
            if (!nodeKinds.ContainsKey(calc.SymbolKey))
            {
                problems.Add(new Problem(line, $"calculation symbol {calc.SymbolKey} is not declared in this artifact"));
            }

            var raw = calc.Raw;
            RequireEvidence(line, Refs(raw), "calculation", mandatory: true);
            foreach (var attribute in raw.GetProperty("attributes").EnumerateObject())
            {
                RequireEvidence(line, Refs(attribute.Value), $"attribute {attribute.Name}", mandatory: false);
            }

            var inputs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var input in raw.GetProperty("inputs").EnumerateArray())
            {
                if (!inputs.Add(input.GetProperty("ref").GetString()!))
                {
                    problems.Add(new Problem(line, $"duplicate input {input.GetProperty("ref").GetString()}"));
                }
            }

            foreach (var unbound in UnboundPlaceholders(raw.GetProperty("expr"), inputs))
            {
                problems.Add(new Problem(line, $"expression uses {unbound} which is neither an input nor bound by an enclosing agg"));
            }
        }

        // 7. Counts and coverage.
        var actual = new Counts(nodes.Count, edges.Count, evidence.Count, calculations.Count, diagnostics.Count);
        if (actual != complete.Counts)
        {
            problems.Add(new Problem(count, $"complete.counts {complete.Counts} does not match records {actual}"));
        }

        var coverage = complete.Coverage;
        var incomplete = coverage.IncompletePaths?.Count ?? 0;
        if (coverage.FilesAnalyzed > coverage.FilesTotal)
        {
            problems.Add(new Problem(count, "filesAnalyzed exceeds filesTotal"));
        }

        if (complete.Status == "full" && (coverage.FilesAnalyzed != coverage.FilesTotal || incomplete > 0))
        {
            problems.Add(new Problem(count, "status 'full' requires every file to be analysed"));
        }

        if (complete.Status == "partial" && coverage.FilesAnalyzed == coverage.FilesTotal && incomplete == 0
            && !diagnostics.Any(d => d.Diagnostic.AffectsCoverage == true))
        {
            problems.Add(new Problem(count, "status 'partial' must say what is incomplete (incompletePaths or a coverage diagnostic)"));
        }

        if (complete.Status == "full" && diagnostics.Any(d => d.Diagnostic.AffectsCoverage == true))
        {
            problems.Add(new Problem(count, "a diagnostic affects coverage but status is 'full'"));
        }

        var unresolved = edges.Count(e => e.Edge.Resolution == "unresolved");
        if (coverage.UnresolvedEdges is { } reported && reported != unresolved)
        {
            problems.Add(new Problem(count, $"coverage.unresolvedEdges is {reported}, artifact has {unresolved}"));
        }

        if (problems.Count > 0)
        {
            return new ValidationReport(null, problems);
        }

        return new ValidationReport(
            new Artifact(
                header,
                complete,
                nodes.Select(n => n.Node).ToList(),
                edges.Select(e => e.Edge).ToList(),
                evidence.Select(e => e.Evidence).ToList(),
                calculations.Select(c => c.Calculation).ToList(),
                diagnostics.Select(d => d.Diagnostic).ToList(),
                contentSha),
            []);
    }

    private static void CheckKey(List<Problem> problems, int line, string key)
    {
        if (SymbolKey.Check(key) is { } problem)
        {
            problems.Add(new Problem(line, $"{problem}: {key}"));
        }
    }

    private static List<string> Refs(JsonElement element) =>
        element.TryGetProperty("evidence", out var refs) ? refs.EnumerateArray().Select(r => r.GetString()!).ToList() : [];

    private static IEnumerable<string> UnboundPlaceholders(JsonElement expr, HashSet<string> scope)
    {
        var op = expr.GetProperty("op").GetString();
        if (op == "var")
        {
            var name = expr.GetProperty("ref").GetString()!;
            if (!scope.Contains(name))
            {
                yield return name;
            }

            yield break;
        }

        if (op == "agg")
        {
            foreach (var u in UnboundPlaceholders(expr.GetProperty("over"), scope))
            {
                yield return u;
            }

            var inner = new HashSet<string>(scope, StringComparer.Ordinal);
            if (expr.TryGetProperty("bind", out var bind))
            {
                inner.Add(bind.GetString()!);
            }

            foreach (var name in new[] { "value", "filter" })
            {
                if (expr.TryGetProperty(name, out var child))
                {
                    foreach (var u in UnboundPlaceholders(child, inner))
                    {
                        yield return u;
                    }
                }
            }

            yield break;
        }

        foreach (var property in expr.EnumerateObject())
        {
            var children = property.Value.ValueKind switch
            {
                JsonValueKind.Object when property.Value.TryGetProperty("op", out _) => [property.Value],
                JsonValueKind.Array => property.Value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("op", out _)).ToList(),
                _ => new List<JsonElement>(),
            };
            foreach (var child in children)
            {
                foreach (var u in UnboundPlaceholders(child, scope))
                {
                    yield return u;
                }
            }
        }
    }
}
