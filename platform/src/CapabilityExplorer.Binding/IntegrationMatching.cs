using CapabilityExplorer.Contracts;
using System.Security.Cryptography;
using System.Text;

namespace CapabilityExplorer.Binding;

public sealed record IntegrationMatchInput(string CapabilityId, string RunId, LoadedFrom From, Artifact Artifact);
public sealed record IntegrationMatch(string Id, string SourceCapabilityId, string TargetCapabilityId,
    string SourceRunId, string TargetRunId, BindingPreviewSource Source, BindingPreviewSource Target,
    string EdgeId, string SourceKey, string TargetKey, string Relation, string Kind, string Status,
    string Reason, IReadOnlyList<EvidenceRecord> SourceEvidence, IReadOnlyList<EvidenceRecord> TargetEvidence,
    string? ResourceKey = null, string? TargetEdgeId = null, string? ClusterId = null,
    IntegrationContractAssessment? Contract = null);

public sealed record IntegrationContractAssessment(string Status, int ComparedOperations, int CompatibleOperations,
    IReadOnlyList<string> Differences, IReadOnlyList<string> Unknowns);

public static class IntegrationMatching
{
    public const string AlgorithmVersion = "integration-cluster-v1";

    public static IReadOnlyList<IntegrationMatch> Build(IReadOnlyList<IntegrationMatchInput> inputs, BindingFile bindings)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(bindings);
        var bound = Binder.Bind(inputs.Select(input => (input.From, input.Artifact)), bindings).Edges
            .DistinctBy(edge => (edge.Artifact, edge.Original.Id))
            .ToDictionary(edge => (edge.Artifact, edge.Original.Id));
        var endpoints = inputs.SelectMany(input => input.Artifact.Nodes
            .Where(node => node.NodeKind == "Endpoint" && Known(node.SymbolKey))
            .Select(node => (Input: input, Node: node))).ToList();
        var exact = endpoints.ToLookup(endpoint => endpoint.Node.SymbolKey, StringComparer.Ordinal);
        var routes = endpoints.ToLookup(endpoint => Tail(endpoint.Node.SymbolKey), StringComparer.Ordinal);
        var matches = new List<IntegrationMatch>();
        var participants = inputs.SelectMany(input => input.Artifact.Edges
            .Where(edge => edge.To is not null && edge.Relation is "PUBLISHES" or "CONSUMES" or "READS" or "WRITES")
            .Select(edge => (Input: input, Edge: edge, Key: Resource(bound.GetValueOrDefault((input.From, edge.Id))?.Edge.To ?? edge.To!))))
            .Where(item => item.Key.StartsWith("chan|", StringComparison.Ordinal) || item.Key.StartsWith("sql|", StringComparison.Ordinal)).ToList();
        var resourceNames = participants.ToLookup(item => (item.Key[..item.Key.IndexOf('|')], Tail(item.Key)));
        foreach (var input in inputs)
        {
            foreach (var edge in input.Artifact.Edges.Where(edge => edge.Relation == "REQUESTS" && edge.To is not null))
            {
                bound.TryGetValue((input.From, edge.Id), out var binding);
                if (binding is { Status: BindingStatus.Broken }) continue;
                var key = binding?.Edge.To ?? edge.To!;
                var candidates = exact[key].ToList();
                var strong = candidates.Count > 0 && Known(key);
                if (!strong && binding is null && Tail(key) is not ("GET /" or "HEAD /")) candidates = routes[Tail(key)].ToList();
                var owners = candidates.Select(candidate => candidate.Input.CapabilityId).Distinct(StringComparer.Ordinal).Count();
                foreach (var candidate in candidates.Where(candidate => candidate.Input.CapabilityId != input.CapabilityId
                             && candidate.Input.From.RepositoryId != input.From.RepositoryId))
                {
                    var proven = strong && owners == 1;
                    var evidence = candidate.Node.Evidence.Concat(candidate.Input.Artifact.Edges
                        .Where(handler => handler.To == candidate.Node.SymbolKey && handler.Relation is "HANDLES" or "EXPOSES")
                        .SelectMany(handler => handler.Evidence));
                    matches.Add(Create(input, edge, candidate.Input, candidate.Node.SymbolKey, "http", proven,
                        proven ? binding is null ? "Exact declared endpoint identity." : "Explicit service binding to a declared endpoint."
                            : "Method/route candidate only; service ownership is not proven or is ambiguous.", evidence));
                }
            }
        }
        foreach (var source in participants)
        {
            foreach (var target in resourceNames[(source.Key[..source.Key.IndexOf('|')], Tail(source.Key))]
                         .Where(target => target.Input.CapabilityId != source.Input.CapabilityId
                             && target.Input.From.RepositoryId != source.Input.From.RepositoryId))
            {
                var messaging = source.Key.StartsWith("chan|", StringComparison.Ordinal);
                if (messaging && (source.Edge.Relation != "PUBLISHES" || target.Edge.Relation != "CONSUMES")) continue;
                if (source.Key != target.Key && Known(source.Key) && Known(target.Key)) continue;
                var proven = source.Key == target.Key && Known(source.Key);
                matches.Add(Create(source.Input, source.Edge, target.Input, target.Edge.From, messaging ? "message" : "data", proven,
                    proven ? messaging ? "Producer and consumer share the exact bound channel identity."
                        : "Participants share the exact bound data resource; this is not an inferred execution order."
                        : "Same resource name with an unbound identity; namespace/store equivalence is not proven.",
                    target.Edge.Evidence) with { ResourceKey = source.Key, TargetEdgeId = target.Edge.Id });
            }
        }
        var clustered = ClusteredIntegrationMatching.Build(inputs, matches);
        matches.RemoveAll(match => match.Status != "matched" && clustered.Any(candidate => candidate.SourceRunId == match.SourceRunId
            && candidate.Source.ArtifactHash == match.Source.ArtifactHash && candidate.EdgeId == match.EdgeId));
        matches.AddRange(clustered);
        return matches.DistinctBy(match => match.Id).OrderBy(match => match.Id, StringComparer.Ordinal).ToList();
    }

    private static IntegrationMatch Create(IntegrationMatchInput input, EdgeRecord edge, IntegrationMatchInput target,
        string targetKey, string kind, bool proven, string reason, IEnumerable<string> targetEvidence)
    {
        var identity = string.Join('\n', AlgorithmVersion, input.CapabilityId, input.RunId, input.Artifact.ContentSha256,
            edge.Id, target.CapabilityId, target.RunId, target.Artifact.ContentSha256, targetKey);
        return new(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity))), input.CapabilityId,
            target.CapabilityId, input.RunId, target.RunId, Source(input), Source(target), edge.Id, edge.From,
            targetKey, edge.Relation, kind, proven ? "matched" : "suggested", reason,
            Evidence(input.Artifact, edge.Evidence), Evidence(target.Artifact, targetEvidence));
    }

    private static BindingPreviewSource Source(IntegrationMatchInput input) =>
        new(input.From.RepositoryId, input.From.CommitSha, input.From.UnitPath, input.Artifact.ContentSha256);

    private static List<EvidenceRecord> Evidence(Artifact artifact, IEnumerable<string> ids)
    {
        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        return artifact.Evidence.Where(evidence => wanted.Contains(evidence.Id)).OrderBy(evidence => evidence.Id, StringComparer.Ordinal).ToList();
    }

    private static bool Known(string key) => !key.Contains("@unbound:", StringComparison.Ordinal);
    private static string Resource(string key) => key.StartsWith("sql|", StringComparison.Ordinal) && key.IndexOf('#') is >= 0 and var column
        ? key[..column] : key;
    private static string Tail(string key) => key[(key.LastIndexOf('|') + 1)..];
}