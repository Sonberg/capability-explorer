using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CapabilityExplorer.Binding;
using CapabilityExplorer.Contracts;

namespace CapabilityExplorer.Explorer.LandscapeAnalysis;

public sealed record LandscapeAnalysisRevision(string RepositoryId, string CommitSha);

public sealed record LandscapeAnalysisRequest(
    IReadOnlyList<LandscapeAnalysisRevision>? Revisions,
    string? BindingsSha256);

public sealed record LandscapeArtifactInput(
    string RepositoryId,
    string CommitSha,
    string UnitPath,
    string UnitName,
    string Language,
    string ArtifactSha256,
    ToolRef Extractor,
    IReadOnlyList<ToolRef>? Rules,
    string ConfigHash,
    string InputHash,
    string Status,
    Counts Counts,
    Coverage Coverage,
    IReadOnlyList<DiagnosticRecord> Diagnostics);

public sealed record LandscapeAnalysisSnapshot(
    string Schema,
    string Status,
    string RuleVersion,
    string IntegrationRuleVersion,
    string BindingsSha256,
    IReadOnlyList<BindingEntry> Bindings,
    string? IntegrationMatchesFingerprint,
    string ManualConnectionsFingerprint,
    ArchitectureConnectionSnapshot ManualConnections,
    IReadOnlyList<LandscapeAnalysisRevision> Revisions,
    IReadOnlyList<LandscapeArtifactInput> Inputs,
    ArchitectureGraph Landscape,
    string? ContentHash = null);

public sealed record LandscapeAnalysisError(
    string Code,
    string Message,
    IReadOnlyList<LandscapeAnalysisRevision> Missing,
    IReadOnlyList<LandscapeArtifactInput> Partial,
    IReadOnlyList<string> Invalid);

internal sealed record LandscapeAnalysisBuild(LandscapeAnalysisSnapshot? Snapshot, LandscapeAnalysisError? Error);

internal static class LandscapeAnalysisEngine
{
    internal const string Schema = "ce.landscape-analysis.v1";
    internal const string RuleVersion = "landscape-analysis-v1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static LandscapeAnalysisBuild Build(
        ExplorerIndex index,
        IReadOnlyList<CapabilityStatus> capabilities,
        string capabilitiesFile,
        LandscapeAnalysisRequest? request)
    {
        if (request?.Revisions is not { Count: > 0 and <= 100 } revisions
            || request.BindingsSha256 is null
            || !IsSha256(request.BindingsSha256)
            || revisions.Any(item => item is null || string.IsNullOrWhiteSpace(item.RepositoryId) || !IsCommit(item.CommitSha))
            || revisions.Select(item => item.RepositoryId).Distinct(StringComparer.Ordinal).Count() != revisions.Count)
        {
            return Invalid("Request must include 1-100 unique repository revisions and a SHA-256 mapping identity.");
        }

        if (index.Problems.Count > 0)
        {
            return Invalid("The loaded artifact or mapping inputs contain validation problems.", index.Problems);
        }

        if (!string.Equals(request.BindingsSha256, index.Bindings.Sha256, StringComparison.Ordinal))
        {
            return Invalid("The mapping identity does not match the loaded binding file.");
        }

        var missing = revisions
            .Where(item => !index.Revisions.Any(revision => revision.RepositoryId == item.RepositoryId && revision.CommitSha == item.CommitSha))
            .OrderBy(item => item.RepositoryId, StringComparer.Ordinal)
            .ToArray();
        if (missing.Length > 0)
        {
            return Incomplete(missing);
        }

        var orderedRevisions = revisions.OrderBy(item => item.RepositoryId, StringComparer.Ordinal).ToArray();
        var selection = string.Join(",", orderedRevisions.Select(item => $"{item.RepositoryId}:{item.CommitSha}"));
        var scope = new ViewScope(
            "landscape-analysis",
            orderedRevisions.Select(item => item.RepositoryId).ToHashSet(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));
        var view = index.View(selection, scope);
        if (view.Selection.Count != orderedRevisions.Length
            || orderedRevisions.Any(item => !view.Selection.TryGetValue(item.RepositoryId, out var sha) || sha != item.CommitSha))
        {
            return Incomplete(orderedRevisions);
        }

        var artifacts = view.Artifacts
            .OrderBy(item => item.RepositoryId, StringComparer.Ordinal)
            .ThenBy(item => item.Artifact.Header.Unit.Path, StringComparer.Ordinal)
            .ToArray();
        missing = orderedRevisions
            .Where(revision => !artifacts.Any(artifact => artifact.RepositoryId == revision.RepositoryId && artifact.CommitSha == revision.CommitSha))
            .ToArray();
        if (missing.Length > 0)
        {
            return Incomplete(missing);
        }

        var inputs = artifacts.Select(ToInput).ToArray();
        var partial = inputs.Where(input => input.Status != "full").ToArray();
        if (partial.Length > 0)
        {
            return new(null, new LandscapeAnalysisError(
                "incomplete-inputs",
                "One or more selected artifacts are partial.",
                [],
                partial,
                []));
        }

        var automatic = new AutomaticIntegrationConnections(capabilitiesFile);
        (string? Fingerprint, IReadOnlyList<IntegrationMatch> Matches) selectedAutomatic;
        try
        {
            selectedAutomatic = automatic.Selected(view, capabilities);
        }
        catch (Exception error) when (error is InvalidDataException or JsonException)
        {
            return Invalid("The automatic integration mapping is invalid.", [error.Message]);
        }

        var manualStore = new ArchitectureConnections(capabilitiesFile);
        ArchitectureConnectionSnapshot manual;
        try
        {
            manual = manualStore.Read();
        }
        catch (Exception error) when (error is InvalidDataException or JsonException)
        {
            return Invalid("The manual connection mapping is invalid.", [error.Message]);
        }
        var manualFingerprint = Fingerprint(manual);

        var graph = Architecture.Build(view, "landscape", null, view, capabilities, null);
        graph = automatic.Apply(graph, view, capabilities);
        graph = manualStore.Apply(graph, capabilities);

        (string? Fingerprint, IReadOnlyList<IntegrationMatch> Matches) currentAutomatic;
        try
        {
            currentAutomatic = automatic.Selected(view, capabilities);
        }
        catch (Exception error) when (error is InvalidDataException or JsonException)
        {
            return Invalid("The automatic integration mapping became invalid during analysis.", [error.Message]);
        }

        var currentManual = manualStore.Read();
        if (selectedAutomatic.Fingerprint != currentAutomatic.Fingerprint
            || manualFingerprint != Fingerprint(currentManual))
        {
            return new(null, new LandscapeAnalysisError(
                "inputs-changed",
                "Mapping inputs changed while landscape analysis was running.",
                [],
                [],
                []));
        }

        var snapshot = new LandscapeAnalysisSnapshot(
            Schema,
            "complete",
            RuleVersion,
            IntegrationMatching.AlgorithmVersion,
            index.Bindings.Sha256,
            index.Bindings.Entries.OrderBy(entry => entry.Hint, StringComparer.Ordinal).ToArray(),
            selectedAutomatic.Fingerprint,
            manualFingerprint,
            manual with { Connections = manual.Connections.OrderBy(connection => connection.Id, StringComparer.Ordinal).ToArray() },
            orderedRevisions,
            inputs,
            graph with
            {
                Nodes = graph.Nodes.OrderBy(node => node.Id, StringComparer.Ordinal).ToArray(),
                Edges = graph.Edges.OrderBy(edge => edge.Id, StringComparer.Ordinal).ToArray(),
                Notes = graph.Notes.Order(StringComparer.Ordinal).ToArray(),
            });
        return new(snapshot with { ContentHash = ContentHash(snapshot) }, null);
    }

    internal static bool IsValid(LandscapeAnalysisSnapshot snapshot) =>
        snapshot.Schema == Schema
        && snapshot.Status == "complete"
        && snapshot.RuleVersion == RuleVersion
        && snapshot.IntegrationRuleVersion == IntegrationMatching.AlgorithmVersion
        && IsSha256(snapshot.BindingsSha256)
        && IsSha256(snapshot.ManualConnectionsFingerprint)
        && snapshot.ContentHash is { } hash
        && string.Equals(hash, ContentHash(snapshot), StringComparison.Ordinal);

    private static LandscapeArtifactInput ToInput(LoadedArtifact loaded)
    {
        var artifact = loaded.Artifact;
        var header = artifact.Header;
        return new LandscapeArtifactInput(
            header.RepositoryId,
            header.CommitSha,
            header.Unit.Path,
            header.Unit.Name,
            header.Language,
            artifact.ContentSha256,
            header.Extractor,
            header.Rules,
            header.ConfigHash,
            header.InputHash,
            artifact.Complete.Status,
            artifact.Complete.Counts,
            artifact.Complete.Coverage,
            artifact.Diagnostics);
    }

    private static LandscapeAnalysisBuild Invalid(string message, IReadOnlyList<string>? invalid = null) =>
        new(null, new LandscapeAnalysisError("invalid-inputs", message, [], [], invalid ?? []));

    private static LandscapeAnalysisBuild Incomplete(IReadOnlyList<LandscapeAnalysisRevision> missing) =>
        new(null, new LandscapeAnalysisError("incomplete-inputs", "One or more selected revisions are unavailable.", missing, [], []));

    private static string ContentHash(LandscapeAnalysisSnapshot snapshot)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(snapshot with { ContentHash = null }, Json);
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }

    private static string Fingerprint(ArchitectureConnectionSnapshot snapshot)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(
            snapshot with { Connections = snapshot.Connections.OrderBy(connection => connection.Id, StringComparer.Ordinal).ToArray() },
            Json);
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsCommit(string? value) =>
        value is { Length: 40 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
