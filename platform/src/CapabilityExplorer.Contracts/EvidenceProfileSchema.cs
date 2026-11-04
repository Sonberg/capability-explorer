using System.Collections.Frozen;
using System.Text.Json;
using Json.Schema;

namespace CapabilityExplorer.Contracts;

public static class EvidenceProfileSchema
{
    private static readonly Lazy<FrozenDictionary<string, JsonSchema>> Schemas = new(() =>
        new Dictionary<string, JsonSchema>(StringComparer.Ordinal)
        {
            ["ce.models.v1"] = JsonSchema.FromText(ContractSchema.ReadResource("ce.models.v1.schema.json")),
            ["ce.candidates.v1"] = JsonSchema.FromText(ContractSchema.ReadResource("ce.candidates.v1.schema.json"))
        }.ToFrozenDictionary(StringComparer.Ordinal));

    public static IReadOnlyList<string> Validate(string profile, JsonElement payload)
    {
        if (!Schemas.Value.TryGetValue(profile, out var schema)) return ["Unsupported model/candidate evidence profile"];
        if (payload.ValueKind != JsonValueKind.Object) return ["Evidence profile payload must be an object"];
        if (schema.Evaluate(payload, new EvaluationOptions { OutputFormat = OutputFormat.Flag }).IsValid) return [];
        var result = schema.Evaluate(payload, new EvaluationOptions { OutputFormat = OutputFormat.List });
        var problems = (result.Details ?? []).Where(detail => detail.Errors is { Count: > 0 })
            .SelectMany(detail => detail.Errors!.Select(error => $"{detail.InstanceLocation}: {error.Value}"))
            .Distinct(StringComparer.Ordinal).Take(8).ToArray();
        return problems.Length == 0 ? ["Evidence does not match the declared profile schema"] : problems;
    }
}
