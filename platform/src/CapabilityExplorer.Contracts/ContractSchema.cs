using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;
using Json.Schema;

namespace CapabilityExplorer.Contracts;

/// <summary>The ce.v1 JSON Schema and relation catalogue, loaded from the embedded copies of schema/v1.</summary>
public sealed class ContractSchema
{
    public const string SchemaId = "ce.v1";
    public static readonly IReadOnlyList<string> RecordKinds = ["header", "node", "edge", "evidence", "calculation", "diagnostic", "complete"];

    private static readonly Lazy<ContractSchema> Shared = new(Load);

    private readonly JsonSchema _record;
    private readonly FrozenDictionary<string, JsonSchema> _byKind;

    private ContractSchema(JsonSchema record, FrozenDictionary<string, JsonSchema> byKind, RelationCatalog relations)
    {
        _record = record;
        _byKind = byKind;
        Relations = relations;
    }

    public static ContractSchema Instance => Shared.Value;

    public RelationCatalog Relations { get; }

    /// <summary>Validates one record. Returns an empty list when valid.</summary>
    /// <remarks>
    /// The top-level oneOf branches are mutually exclusive on the "kind" const, so validating against the record's
    /// own kind definition is equivalent and avoids evaluating every branch. A cheap flag evaluation runs first;
    /// detailed output is only produced for failures.
    /// </remarks>
    public IReadOnlyList<string> Validate(JsonElement record)
    {
        var kind = record.ValueKind == JsonValueKind.Object && record.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String
            ? k.GetString()
            : null;
        var target = kind is not null && _byKind.TryGetValue(kind, out var own) ? own : _record;
        if (target.Evaluate(record, new EvaluationOptions { OutputFormat = OutputFormat.Flag }).IsValid)
        {
            return [];
        }

        var results = target.Evaluate(record, new EvaluationOptions { OutputFormat = OutputFormat.List });
        var messages = (results.Details ?? [])
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => $"{(d.InstanceLocation.ToString() is { Length: > 0 } loc ? loc : "/")}: {e.Value}"))
            .Distinct()
            .Take(8)
            .ToList();
        return messages.Count > 0 ? messages : ["record does not match any ce.v1 record kind"];
    }

    private static ContractSchema Load()
    {
        var registry = new SchemaRegistry();
        var build = new BuildOptions { SchemaRegistry = registry };
        var text = ReadResource("ce.v1.record.schema.json");
        var record = JsonSchema.FromText(text, build);
        var id = JsonDocument.Parse(text).RootElement.GetProperty("$id").GetString()!;
        var byKind = RecordKinds.ToFrozenDictionary(
            kind => kind,
            kind => JsonSchema.FromText(
                $$"""{"$schema":"https://json-schema.org/draft/2020-12/schema","$id":"urn:ce:v1:{{kind}}","$ref":"{{id}}#/$defs/{{kind}}"}""",
                build));
        return new ContractSchema(record, byKind, RelationCatalog.Parse(ReadResource("ce.v1.relations.json")));
    }

    private static string ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"embedded resource {name} missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

public sealed class RelationCatalog
{
    private readonly FrozenDictionary<string, (FrozenSet<string> From, FrozenSet<string> To)> _rules;

    private RelationCatalog(FrozenDictionary<string, (FrozenSet<string> From, FrozenSet<string> To)> rules) => _rules = rules;

    public static RelationCatalog Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var rules = doc.RootElement.GetProperty("relations").EnumerateObject().ToFrozenDictionary(
            p => p.Name,
            p => (
                p.Value.GetProperty("from").EnumerateArray().Select(e => e.GetString()!).ToFrozenSet(),
                p.Value.GetProperty("to").EnumerateArray().Select(e => e.GetString()!).ToFrozenSet()));
        return new RelationCatalog(rules);
    }

    public bool IsKnown(string relation) => _rules.ContainsKey(relation);

    public bool AllowsFrom(string relation, string nodeKind) => _rules.TryGetValue(relation, out var r) && r.From.Contains(nodeKind);

    public bool AllowsTo(string relation, string nodeKind) => _rules.TryGetValue(relation, out var r) && r.To.Contains(nodeKind);
}
