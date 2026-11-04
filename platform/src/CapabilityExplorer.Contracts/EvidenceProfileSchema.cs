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
            ["ce.candidates.v1"] = JsonSchema.FromText(ContractSchema.ReadResource("ce.candidates.v1.schema.json")),
            ["ce.collection.plan.v1"] = JsonSchema.FromText(ContractSchema.ReadResource("ce.collection.plan.v1.schema.json")),
            ["ce.collection.v1"] = JsonSchema.FromText(ContractSchema.ReadResource("ce.collection.v1.schema.json"))
        }.ToFrozenDictionary(StringComparer.Ordinal));

    public static IReadOnlyList<string> Validate(string profile, JsonElement payload)
    {
        if (!Schemas.Value.TryGetValue(profile, out var schema)) return ["Unsupported evidence profile"];
        if (!HasUnambiguousStructure(payload)) return ["Evidence JSON has duplicate members, excessive depth, or malformed Unicode"];
        if (payload.ValueKind != JsonValueKind.Object) return ["Evidence profile payload must be an object"];
        if (schema.Evaluate(payload, new EvaluationOptions { OutputFormat = OutputFormat.Flag }).IsValid) return [];
        var result = schema.Evaluate(payload, new EvaluationOptions { OutputFormat = OutputFormat.List });
        var problems = (result.Details ?? []).Where(detail => detail.Errors is { Count: > 0 })
            .SelectMany(detail => detail.Errors!.Select(error => $"{detail.InstanceLocation}: {error.Value}"))
            .Distinct(StringComparer.Ordinal).Take(8).ToArray();
        return problems.Length == 0 ? ["Evidence does not match the declared profile schema"] : problems;
    }

    internal static bool HasUnambiguousStructure(JsonElement value, int depth = 0)
    {
        if (depth > 32) return false;
        try
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                    if (!keys.Add(property.Name) || !WellFormed(property.Name) ||
                        !HasUnambiguousStructure(property.Value, depth + 1)) return false;
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in value.EnumerateArray())
                    if (!HasUnambiguousStructure(child, depth + 1)) return false;
            }
            else if (value.ValueKind == JsonValueKind.String && !WellFormed(value.GetString()!)) return false;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool WellFormed(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (++index == value.Length || !char.IsLowSurrogate(value[index])) return false;
            }
            else if (char.IsLowSurrogate(value[index])) return false;
        }
        return true;
    }
}
