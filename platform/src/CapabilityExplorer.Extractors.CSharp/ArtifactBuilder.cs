using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CapabilityExplorer.Contracts;
using Microsoft.CodeAnalysis;

namespace CapabilityExplorer.Extractors.CSharp;

public sealed record Tool(string Name, string Version);

/// <summary>
/// Accumulates one ce.v1 artifact. Ids are content-derived so the same input always yields the same bytes
/// (the importer treats different bytes under the same analysis key as an error).
/// </summary>
public sealed class ArtifactBuilder(string repositoryId, string commitSha, Tool extractor)
{
    private readonly Dictionary<string, JsonObject> _evidence = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> _edges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> _calculations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> _diagnostics = new(StringComparer.Ordinal);
    private readonly HashSet<string> _incomplete = new(StringComparer.Ordinal);

    public string RepositoryId => repositoryId;

    public bool HasNode(string key) => _nodes.ContainsKey(key);

    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string Short(string text) => Sha256(text)[..16];

    /// <summary>Evidence for a span in a source file (path relative to the repository root).</summary>
    public string Evidence(string path, FileLinePositionSpan span, string fileText)
    {
        var s = new Span(span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1, span.EndLinePosition.Line + 1, span.EndLinePosition.Character + 1);
        return Evidence(path, s, fileText);
    }

    public string Evidence(string path, Span span, string fileText)
    {
        var id = $"ev:{Short($"{path}:{span.StartLine}:{span.StartColumn}:{span.EndLine}:{span.EndColumn}")}";
        if (!_evidence.ContainsKey(id))
        {
            var snippet = Snippet.Extract(fileText, span) ?? "";
            _evidence[id] = new JsonObject
            {
                ["schema"] = "ce.v1", ["kind"] = "evidence", ["id"] = id,
                ["repositoryId"] = repositoryId, ["commitSha"] = commitSha, ["path"] = path,
                ["span"] = new JsonObject { ["startLine"] = span.StartLine, ["startColumn"] = span.StartColumn, ["endLine"] = span.EndLine, ["endColumn"] = span.EndColumn },
                ["snippetSha256"] = Snippet.Sha256(snippet),
                ["extractor"] = new JsonObject { ["name"] = extractor.Name, ["version"] = extractor.Version },
            };
        }

        return id;
    }

    /// <summary>Evidence for a whole line (used for project files read as XML).</summary>
    public string LineEvidence(string path, int line, string fileText)
    {
        var lines = fileText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var content = lines[Math.Clamp(line, 1, lines.Length) - 1];
        var start = content.Length - content.TrimStart().Length + 1;
        return Evidence(path, new Span(line, start, line, content.TrimEnd().Length + 1), fileText);
    }

    public void Node(string key, string kind, string name, IEnumerable<string> evidence, string? signature = null, bool generated = false, JsonObject? props = null)
    {
        if (_nodes.TryGetValue(key, out var existing))
        {
            // Partial types and the like: one symbol, several declarations.
            var refs = existing["evidence"]!.AsArray();
            foreach (var e in evidence.Where(e => !refs.Any(r => r!.GetValue<string>() == e)))
            {
                refs.Add(e);
            }

            return;
        }

        var node = new JsonObject
        {
            ["schema"] = "ce.v1", ["kind"] = "node", ["id"] = $"n:{Short(key)}", ["symbolKey"] = key, ["nodeKind"] = kind, ["name"] = name,
        };
        if (signature is not null)
        {
            node["signature"] = signature;
        }

        if (generated)
        {
            node["generated"] = true;
        }

        if (props is { Count: > 0 })
        {
            node["props"] = props;
        }

        node["evidence"] = new JsonArray(evidence.Distinct().Select(e => (JsonNode)JsonValue.Create(e)!).ToArray());
        _nodes[key] = node;
    }

    public void Edge(string from, string relation, string? to, IEnumerable<string> evidence, string derivation = "compiler", string resolution = "resolved", string? reason = null, Tool? rule = null, JsonObject? props = null)
    {
        var ev = evidence.Distinct().ToList();
        var id = $"e:{Short($"{from}|{relation}|{to ?? "?"}|{ev.FirstOrDefault()}")}";
        if (_edges.ContainsKey(id))
        {
            return;
        }

        var edge = new JsonObject { ["schema"] = "ce.v1", ["kind"] = "edge", ["id"] = id, ["from"] = from, ["relation"] = relation };
        if (to is not null)
        {
            edge["to"] = to;
        }

        edge["derivation"] = derivation;
        edge["resolution"] = resolution;
        if (reason is not null)
        {
            edge["unresolvedReason"] = reason;
        }

        if (rule is not null)
        {
            edge["rule"] = new JsonObject { ["name"] = rule.Name, ["version"] = rule.Version };
        }

        if (props is { Count: > 0 })
        {
            edge["props"] = props;
        }

        edge["evidence"] = new JsonArray(ev.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray());
        _edges[id] = edge;
    }

    /// <summary>A calculation record. Its evidence (and that of its attributes) is kept in the artifact.</summary>
    public void Calculation(JsonObject calculation) => _calculations[calculation["id"]!.GetValue<string>()] = calculation;

    private static IEnumerable<string> EvidenceRefs(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (name, child) in o)
                {
                    if (name == "evidence" && child is JsonArray refs)
                    {
                        foreach (var r in refs)
                        {
                            yield return r!.GetValue<string>();
                        }
                    }
                    else
                    {
                        foreach (var r in EvidenceRefs(child))
                        {
                            yield return r;
                        }
                    }
                }

                break;
            case JsonArray a:
                foreach (var child in a)
                {
                    foreach (var r in EvidenceRefs(child))
                    {
                        yield return r;
                    }
                }

                break;
        }
    }

    public void Diagnostic(string severity, string code, string message, string? path, bool affectsCoverage, string? evidence = null)
    {
        var id = $"d:{Short($"{code}|{path ?? ""}|{message}")}";
        var d = new JsonObject { ["schema"] = "ce.v1", ["kind"] = "diagnostic", ["id"] = id, ["severity"] = severity, ["code"] = code, ["message"] = message };
        if (path is not null)
        {
            d["path"] = path;
        }

        if (affectsCoverage)
        {
            d["affectsCoverage"] = true;
        }

        if (evidence is not null)
        {
            d["evidence"] = new JsonArray(evidence);
        }

        _diagnostics[id] = d;
    }

    public void Incomplete(string path) => _incomplete.Add(path);

    /// <summary>Drops edges whose source is not declared here (e.g. rule matches in code the extractor skipped).</summary>
    private IEnumerable<JsonObject> ValidEdges() => _edges.Values.Where(e => _nodes.ContainsKey(e["from"]!.GetValue<string>()));

    public string ToJsonl(string unitPath, string unitName, string language, IReadOnlyList<Tool> rules, string configHash, string inputHash, int filesTotal)
    {
        var edges = ValidEdges().ToList();
        var used = _nodes.Values.SelectMany(n => n["evidence"]!.AsArray())
            .Concat(edges.SelectMany(e => e["evidence"]!.AsArray()))
            .Concat(_diagnostics.Values.SelectMany(d => d["evidence"]?.AsArray() ?? []))
            .Select(e => e!.GetValue<string>())
            .Concat(_calculations.Values.SelectMany(c => EvidenceRefs(c)))
            .ToHashSet(StringComparer.Ordinal);
        var evidence = _evidence.Values.Where(e => used.Contains(e["id"]!.GetValue<string>())).ToList();
        var partial = _incomplete.Count > 0 || _diagnostics.Values.Any(d => d["affectsCoverage"] is not null);

        var header = new JsonObject
        {
            ["schema"] = "ce.v1", ["kind"] = "header", ["repositoryId"] = repositoryId, ["commitSha"] = commitSha,
            ["unit"] = new JsonObject { ["path"] = unitPath, ["name"] = unitName }, ["language"] = language,
            ["extractor"] = new JsonObject { ["name"] = extractor.Name, ["version"] = extractor.Version },
            ["rules"] = new JsonArray(rules.Select(r => (JsonNode)new JsonObject { ["name"] = r.Name, ["version"] = r.Version }).ToArray()),
            ["configHash"] = configHash, ["inputHash"] = inputHash,
        };
        var coverage = new JsonObject
        {
            ["filesTotal"] = filesTotal,
            ["filesAnalyzed"] = filesTotal - _incomplete.Count,
            ["unresolvedEdges"] = edges.Count(e => e["resolution"]!.GetValue<string>() == "unresolved"),
        };
        if (_incomplete.Count > 0)
        {
            coverage["incompletePaths"] = new JsonArray(_incomplete.Order(StringComparer.Ordinal).Select(p => (JsonNode)JsonValue.Create(p)!).ToArray());
        }

        var complete = new JsonObject
        {
            ["schema"] = "ce.v1", ["kind"] = "complete", ["status"] = partial ? "partial" : "full",
            ["counts"] = new JsonObject { ["node"] = _nodes.Count, ["edge"] = edges.Count, ["evidence"] = evidence.Count, ["calculation"] = _calculations.Count, ["diagnostic"] = _diagnostics.Count },
            ["coverage"] = coverage,
        };

        static IEnumerable<JsonObject> ById(IEnumerable<JsonObject> xs) => xs.OrderBy(x => x["id"]!.GetValue<string>(), StringComparer.Ordinal);
        var lines = new[] { header }.Concat(ById(evidence)).Concat(ById(_nodes.Values)).Concat(ById(edges)).Concat(ById(_calculations.Values)).Concat(ById(_diagnostics.Values)).Append(complete);
        var options = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        return string.Join('\n', lines.Select(l => l.ToJsonString(options))) + "\n";
    }
}
