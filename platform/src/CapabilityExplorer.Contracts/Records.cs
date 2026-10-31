using System.Text.Json;
using System.Text.Json.Serialization;

namespace CapabilityExplorer.Contracts;

// Typed view of schema/v1/record.schema.json. The JSON Schema is the source of truth; these records are
// only deserialised after a line has passed schema validation.

public sealed record ToolRef(string Name, string Version);

public sealed record UnitRef(string Path, string Name);

public sealed record HeaderRecord(
    string RepositoryId,
    string CommitSha,
    UnitRef Unit,
    string Language,
    ToolRef Extractor,
    IReadOnlyList<ToolRef>? Rules,
    string ConfigHash,
    string InputHash);

public sealed record Span(int StartLine, int StartColumn, int EndLine, int EndColumn);

public sealed record EvidenceRecord(
    string Id,
    string RepositoryId,
    string CommitSha,
    string Path,
    Span Span,
    string SnippetSha256,
    ToolRef Extractor);

public sealed record NodeRecord(
    string Id,
    string SymbolKey,
    string NodeKind,
    string Name,
    string? Signature,
    bool? Generated,
    JsonElement? Props,
    IReadOnlyList<string> Evidence);

public sealed record EdgeRecord(
    string Id,
    string From,
    string Relation,
    string? To,
    string Derivation,
    string Resolution,
    string? UnresolvedReason,
    ToolRef? Rule,
    JsonElement? Props,
    IReadOnlyList<string> Evidence);

public sealed record DiagnosticRecord(
    string Id,
    string Severity,
    string Code,
    string Message,
    string? Path,
    bool? AffectsCoverage,
    IReadOnlyList<string>? Evidence);

/// <summary>Calculations are kept as raw JSON; only identity and evidence are interpreted in stage 1.</summary>
public sealed record CalculationRecord(string Id, string SymbolKey, JsonElement Raw);

public sealed record Counts(int Node, int Edge, int Evidence, int Calculation, int Diagnostic);

public sealed record Coverage(int FilesTotal, int FilesAnalyzed, IReadOnlyList<string>? IncompletePaths, int? UnresolvedEdges);

public sealed record CompleteRecord(string Status, Counts Counts, Coverage Coverage);

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
