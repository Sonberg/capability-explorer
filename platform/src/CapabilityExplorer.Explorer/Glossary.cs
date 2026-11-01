using System.Security.Cryptography;
using System.Text.Json;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Interpretation;

namespace CapabilityExplorer.Explorer;

/// <summary>Read-only, exact normalized-label candidates backed by the selected artifacts.</summary>
public static class Glossary
{
    public const int ComparisonBudget = 64;

    private static readonly HashSet<string> DeclarationKinds = new(StringComparer.Ordinal)
        { "Type", "Property", "Field", "Column", "MessageContract" };

    // Small exact denylist, plus the Type-only framework-role suffix heuristic in Eligible.
    private static readonly HashSet<string> InfrastructureNames = new(StringComparer.Ordinal)
    {
        "Program", "Startup", "DbContext", "IHostedService", "BackgroundService", "CancellationToken",
        "HttpClient", "ILogger", "IConfiguration", "IServiceCollection", "IServiceProvider", "WebApplicationBuilder",
    };

    private sealed record Entry(GlossaryOccurrence Occurrence, CalculationSide? Calculation, string? CalculationId);

    public static IReadOnlyList<GlossaryCandidate> Build(
        ViewIndex view, IReadOnlyDictionary<string, IReadOnlyList<string>>? repositoryCapabilities = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var artifacts = view.Artifacts.OrderBy(a => a.RepositoryId, StringComparer.Ordinal)
            .ThenBy(a => a.CommitSha, StringComparer.Ordinal)
            .ThenBy(a => a.Artifact.Header.Unit.Path, StringComparer.Ordinal).ToList();
        var nodes = artifacts.SelectMany(a => a.Artifact.Nodes.Select(n => new Occurrence<NodeRecord>(a, n)))
            .Where(o => Eligible(o.Record)).ToList();
        var declarations = nodes.ToLookup(o => (o.Artifact.RepositoryId, o.Artifact.CommitSha, o.Record.SymbolKey));
        var localDeclarations = nodes.ToLookup(o =>
            (o.Artifact.RepositoryId, o.Artifact.CommitSha, o.Artifact.Artifact.Header.Unit.Path, o.Record.SymbolKey));
        var visitedLocalInputs = new HashSet<(string, string, string, string)>();
        var visitedFallbackInputs = new HashSet<(string, string, string)>();

        foreach (var artifact in artifacts)
        {
            var localNodes = artifact.Artifact.Nodes.Where(Eligible)
                .GroupBy(n => n.SymbolKey, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.OrderBy(n => n.Name, StringComparer.Ordinal).First(), StringComparer.Ordinal);
            foreach (var node in localNodes.Values.Where(n => DeclarationKinds.Contains(n.NodeKind)))
            {
                Add(artifact, node, "declaration", DeclaredType(node, artifact.Artifact.Header.Language), node.Evidence);
            }

            // ViewIndex.Calculations retains only the first unit occurrence for a key. Keep every unit here.
            foreach (var calculation in artifact.Artifact.Calculations.OrderBy(c => c.SymbolKey, StringComparer.Ordinal)
                         .ThenBy(c => c.Id, StringComparer.Ordinal))
            {
                if (!localNodes.TryGetValue(calculation.SymbolKey, out var node))
                {
                    continue; // Output has no name: a missing declaration must not become a guessed label.
                }

                var raw = Canonical(calculation.Raw);
                var side = new CalculationSide(node.SymbolKey, node.Name, artifact.RepositoryId, artifact.CommitSha,
                    artifact.Artifact.Header.Language, raw);
                Add(artifact, node, "calculation", KnownType(Text(raw.GetProperty("output"), "type")),
                    node.Evidence.Concat(References(raw, "evidence")), side, calculation.Id);

                foreach (var input in raw.GetProperty("inputs").EnumerateArray())
                {
                    if (Text(input, "origin") is not { } origin)
                    {
                        continue;
                    }

                    var localKey = (artifact.RepositoryId, artifact.CommitSha, artifact.Artifact.Header.Unit.Path, origin);
                    var fallbackKey = (artifact.RepositoryId, artifact.CommitSha, origin);
                    var local = localDeclarations[localKey];
                    var useLocal = local.Any();
                    if (useLocal ? !visitedLocalInputs.Add(localKey) : !visitedFallbackInputs.Add(fallbackKey))
                    {
                        continue;
                    }

                    // Repeated consumers must not repeatedly walk every declaration in other units.
                    foreach (var declaration in useLocal ? local : declarations[fallbackKey])
                    {
                        // Input.type describes the operand (possibly a collection), not the origin declaration.
                        Add(declaration.Artifact, declaration.Record, "input",
                            DeclaredType(declaration.Record, declaration.Artifact.Artifact.Header.Language), declaration.Record.Evidence);
                    }
                }
            }
        }

        return entries.Values.GroupBy(e => GlossaryNormalization.Normalize(e.Occurrence.Name), StringComparer.Ordinal)
            .Where(g => g.Key.Length > 0).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => Candidate(g.Key, g)).ToList();

        void Add(LoadedArtifact artifact, NodeRecord node, string role, string? type,
            IEnumerable<string> evidence, CalculationSide? calculation = null, string? calculationId = null)
        {
            var unit = artifact.Artifact.Header.Unit;
            var identityParts = new List<string>
                { artifact.RepositoryId, artifact.CommitSha, unit.Path, node.SymbolKey, role };
            // Validated artifacts permit several calculations per symbol but reject duplicate record IDs.
            if (calculationId is not null)
            {
                identityParts.Add(calculationId);
            }

            var identity = JsonSerializer.SerializeToUtf8Bytes(identityParts);
            var id = "glossary:" + Convert.ToHexStringLower(SHA256.HashData(identity));
            var capabilities = repositoryCapabilities is not null && repositoryCapabilities.TryGetValue(artifact.RepositoryId, out var caps)
                ? Sorted(caps) : [];
            var occurrence = new GlossaryOccurrence(id, node.SymbolKey, node.Name, node.NodeKind, role,
                artifact.RepositoryId, artifact.CommitSha, unit.Name, unit.Path, type, capabilities,
                SortEvidence(Map.Evidence(artifact, evidence)));
            if (entries.TryGetValue(id, out var existing))
            {
                occurrence = existing.Occurrence with
                {
                    Evidence = SortEvidence(existing.Occurrence.Evidence.Concat(occurrence.Evidence)),
                };
                calculation = existing.Calculation ?? calculation;
            }

            entries[id] = new Entry(occurrence, calculation, calculationId);
        }
    }

    private static GlossaryCandidate Candidate(string term, IEnumerable<Entry> group)
    {
        var entries = group.OrderBy(e => e.Occurrence.RepositoryId, StringComparer.Ordinal)
            .ThenBy(e => e.Occurrence.CommitSha, StringComparer.Ordinal)
            .ThenBy(e => e.Occurrence.UnitPath, StringComparer.Ordinal)
            .ThenBy(e => e.Occurrence.Key, StringComparer.Ordinal)
            .ThenBy(e => e.Occurrence.Role, StringComparer.Ordinal)
            .ThenBy(e => e.CalculationId, StringComparer.Ordinal).ToList();
        var occurrences = entries.Select(e => e.Occurrence).ToList();
        var calculations = entries.Where(e => e.Calculation is not null).ToList();
        var possible = (long)calculations.Count * (calculations.Count - 1) / 2;
        var comparisons = new List<GlossaryComparison>();
        // Both loop conditions stop at the budget: do not enumerate the remaining O(K²) pairs.
        for (var left = 0; left < calculations.Count && comparisons.Count < ComparisonBudget; left++)
        {
            for (var right = left + 1; right < calculations.Count && comparisons.Count < ComparisonBudget; right++)
            {
                var l = calculations[left];
                var r = calculations[right];
                var comparison = CalculationComparer.Compare(l.Calculation!, r.Calculation!);
                var lh = Text(l.Calculation!.Raw, "irHash");
                var rh = Text(r.Calculation!.Raw, "irHash");
                bool? sameExpression = lh is not null && rh is not null ? lh == rh : null;
                // Difference IDs are reassigned after ordinal sorting to remove JSON property-order effects.
                var differences = comparison.Differences.OrderBy(d => d.Attribute, StringComparer.Ordinal)
                    .ThenBy(d => d.Kind).Select((d, i) => d with { Id = $"d{i + 1}" }).ToList();
                comparisons.Add(new(l.Occurrence.Id, r.Occurrence.Id, sameExpression, differences));
            }
        }

        return new GlossaryCandidate(term, occurrences,
            Sorted(occurrences.Select(o => o.RepositoryId)), Sorted(occurrences.SelectMany(o => o.Capabilities)),
            occurrences.Select(o => o.Type).OfType<string>().Distinct(StringComparer.Ordinal).Skip(1).Any(),
            comparisons.Any(c => c.Differences.Any(d => d.Kind is DifferenceKind.ValueDiffers or DifferenceKind.DetailDiffers)),
            comparisons.Any(c => c.SameExpression == false), possible == comparisons.Count, possible, comparisons);
    }

    private static bool Eligible(NodeRecord node)
    {
        var parts = SymbolKey.Segments(node.SymbolKey);
        return node.Generated != true && !string.IsNullOrWhiteSpace(node.Name) &&
            !InfrastructureNames.Contains(node.Name) &&
            // A reviewable heuristic for framework shells; properties/fields with these labels remain visible.
            !(node.NodeKind == "Type" && (node.Name.EndsWith("Controller", StringComparison.Ordinal) ||
                node.Name.EndsWith("DbContext", StringComparison.Ordinal))) &&
            !parts.Contains(SymbolKey.External, StringComparer.Ordinal) &&
            !(node.Props is { ValueKind: JsonValueKind.Object } props &&
              props.TryGetProperty("external", out var external) && external.ValueKind == JsonValueKind.True);
    }

    private static string? DeclaredType(NodeRecord node, string language)
    {
        if (node.Props is { ValueKind: JsonValueKind.Object } props && KnownType(Text(props, "type"), language) is { } type)
        {
            return type;
        }

        // SQL column signatures state the actual declared type; retain that text rather than guessing a numeric family.
        return node.NodeKind == "Column" ? KnownType(node.Signature) : null;
    }

    private static string? KnownType(string? type, string? language = null)
    {
        if (string.IsNullOrWhiteSpace(type) || string.Equals(type.Trim(), "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Only language-defined aliases, never units, naming conventions, or domain synonyms.
        return (language, type.Trim()) switch
        {
            ("csharp", "int" or "System.Int32") => "int32",
            ("csharp", "long" or "System.Int64") => "int64",
            ("csharp", "short" or "System.Int16") => "int16",
            ("csharp", "byte" or "System.Byte") => "uint8",
            ("csharp", "float" or "System.Single") => "float32",
            ("csharp", "double" or "System.Double") => "float64",
            ("csharp", "System.Decimal") => "decimal",
            ("csharp", "System.Boolean") => "bool",
            ("csharp", "System.String") => "string",
            ("typescript" or "javascript", "number") => "float64",
            ("typescript" or "javascript", "boolean") => "bool",
            (_, var declared) => declared,
        };
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static IEnumerable<string> References(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!) : [];

    private static List<string> Sorted(IEnumerable<string> values) =>
        values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    private static List<EvidenceDto> SortEvidence(IEnumerable<EvidenceDto> evidence) => evidence.Distinct()
        .OrderBy(e => e.RepositoryId, StringComparer.Ordinal).ThenBy(e => e.CommitSha, StringComparer.Ordinal)
        .ThenBy(e => e.Path, StringComparer.Ordinal).ThenBy(e => e.StartLine).ThenBy(e => e.StartColumn)
        .ThenBy(e => e.EndLine).ThenBy(e => e.EndColumn).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();

    private static JsonElement Canonical(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            Write(element, writer);
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();

        static void Write(JsonElement value, Utf8JsonWriter writer)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(property.Value, writer);
                }

                writer.WriteEndObject();
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    Write(item, writer);
                }

                writer.WriteEndArray();
            }
            else
            {
                value.WriteTo(writer);
            }
        }
    }
}
