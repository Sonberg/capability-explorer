using System.Text.Json;

namespace CapabilityExplorer.Contracts;

public static partial class ArtifactValidator
{
    /// <summary>Record-local semantic rules shared by materialized and streaming validation.
    /// The record must first pass <see cref="ContractSchema.Validate"/>. Cross-record rules remain mandatory.</summary>
    public static IReadOnlyList<string> ValidateRecordSemantics(JsonElement element, HeaderRecord header)
    {
        ArgumentNullException.ThrowIfNull(header);
        var problems = new List<Problem>();
        var kind = element.GetProperty("kind").GetString();
        switch (kind)
        {
            case "node":
                var key = element.GetProperty("symbolKey").GetString()!;
                CheckKey(problems, 0, key, header.RepositoryId);
                var owner = SymbolKey.OwningRepository(key);
                if (owner is not null && owner != header.RepositoryId)
                    problems.Add(new Problem(0, $"node {key} is declared in {header.RepositoryId} but its key names repository '{owner}'"));
                break;
            case "edge":
                CheckKey(problems, 0, element.GetProperty("from").GetString()!, header.RepositoryId);
                if (element.TryGetProperty("to", out var target))
                    CheckKey(problems, 0, target.GetString()!, header.RepositoryId);
                break;
            case "evidence":
                var evidence = element.Deserialize<EvidenceRecord>(Json.Options)!;
                if (evidence.RepositoryId != header.RepositoryId || evidence.CommitSha != header.CommitSha)
                    problems.Add(new Problem(0, "evidence must reference the header's repository and commitSha"));
                var span = evidence.Span;
                if (span.EndLine < span.StartLine || (span.EndLine == span.StartLine && span.EndColumn < span.StartColumn))
                    problems.Add(new Problem(0, "span ends before it starts"));
                break;
            case "calculation":
                var inputs = new HashSet<string>(StringComparer.Ordinal);
                foreach (var input in element.GetProperty("inputs").EnumerateArray())
                {
                    var name = input.GetProperty("ref").GetString()!;
                    if (!inputs.Add(name)) problems.Add(new Problem(0, $"duplicate input {name}"));
                }
                foreach (var unbound in UnboundPlaceholders(element.GetProperty("expr"), inputs))
                    problems.Add(new Problem(0, $"expression uses {unbound} which is neither an input nor bound by an enclosing agg"));
                break;
        }
        return problems.Select(p => p.Message).ToArray();
    }
    /// <summary>Terminal counts and coverage rules shared by both import paths.</summary>
    public static IReadOnlyList<string> ValidateCompletion(CompleteRecord complete, Counts actual, bool affectsCoverage, int unresolvedEdges)
    {
        ArgumentNullException.ThrowIfNull(complete);
        var problems = new List<string>();
        if (actual != complete.Counts) problems.Add($"complete.counts {complete.Counts} does not match records {actual}");
        var coverage = complete.Coverage;
        var incomplete = coverage.IncompletePaths?.Count ?? 0;
        if (coverage.FilesAnalyzed > coverage.FilesTotal) problems.Add("filesAnalyzed exceeds filesTotal");
        if (complete.Status == "full" && (coverage.FilesAnalyzed != coverage.FilesTotal || incomplete > 0))
            problems.Add("status 'full' requires every file to be analysed");
        if (complete.Status == "partial" && coverage.FilesAnalyzed == coverage.FilesTotal && incomplete == 0 && !affectsCoverage)
            problems.Add("status 'partial' must say what is incomplete (incompletePaths or a coverage diagnostic)");
        if (complete.Status == "full" && affectsCoverage) problems.Add("a diagnostic affects coverage but status is 'full'");
        if (coverage.UnresolvedEdges is { } reported && reported != unresolvedEdges)
            problems.Add($"coverage.unresolvedEdges is {reported}, artifact has {unresolvedEdges}");
        return problems;
    }

}
