using System.Text.Json;
using System.Text.Json.Serialization;

namespace CapabilityExplorer.Extractors.CSharp;

public sealed record CandidateSource(string UnitPath, string CommitSha, string SourceSha256,
    int Start, int Length, int StartLine, int StartColumn, int EndLine, int EndColumn);
public sealed record CandidateDiagnostic(string Code, string Message, CandidateSource? Source = null);
public sealed record CandidateCoverage(string Status, int SupportedMethods, int UnsupportedMethods)
{
    public string Scope { get; } = "single-file-intra-method";
}
public sealed record CandidateParameter(string Name, string Symbol, string Type);
public sealed record CandidateNode(string Kind, CandidateSource Source, IReadOnlyList<CandidateNode> Children,
    string? Operator = null, string? Value = null, string? Type = null, string? Symbol = null,
    string? RawPredicate = null, string? Failure = null);
public sealed record StaticCandidate(string Id, string Kind, string ReviewStatus, bool Executable,
    string ContentProfile, CandidateSource Source, IReadOnlyList<CandidateParameter> Parameters,
    IReadOnlyList<string> InputsRead, IReadOnlyList<string> InputsWritten, CandidateNode Body);
public sealed record CandidateArtifact(string Schema, string RepositoryId, string CommitSha, string UnitPath,
    string SourceSha256, CandidateCoverage Coverage, IReadOnlyList<CandidateDiagnostic> Diagnostics,
    IReadOnlyList<StaticCandidate> Candidates)
{
    public string Recipe { get; } = "roslyn.scalar-guards.v1";

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions) + "\n";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
