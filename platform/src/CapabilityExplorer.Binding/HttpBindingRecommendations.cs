using CapabilityExplorer.Contracts;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CapabilityExplorer.Binding;

public sealed record HttpRequestEvidence(BindingPreviewSource Source, string EdgeId, string? Target, IReadOnlyList<EvidenceRecord> Evidence);
public sealed record HttpEndpointEvidence(BindingPreviewSource Source, string Key, IReadOnlyList<EvidenceRecord> Evidence);
public sealed record HttpRecommendationMatch(HttpRequestEvidence Request, HttpEndpointEvidence Endpoint);
public sealed record HttpScoreFeature(string Name, int Points);
public sealed record HttpBindingCandidate(string Target, int Score, int MatchedRoutes, int TotalRoutes, string State,
    bool PreviewEligible, IReadOnlyList<HttpScoreFeature> Features, IReadOnlyList<HttpRecommendationMatch> Matches,
    IReadOnlyList<HttpRequestEvidence> Unmatched, IReadOnlyList<string> MissingEvidence);
public sealed record HttpRecommendationGroup(string Id, string? Hint, string Label, string State,
    IReadOnlyList<HttpRequestEvidence> Requests, IReadOnlyList<HttpBindingCandidate> Candidates);

public static class HttpBindingRecommendations
{
    public const string AlgorithmVersion = "exact-http-v1";

    public static IReadOnlyList<HttpRecommendationGroup> Build(IEnumerable<(LoadedFrom From, Artifact Artifact)> sources,
        IEnumerable<(LoadedFrom From, Artifact Artifact)> targets, BindingFile existing)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(existing);
        var sourceList = sources.ToList();
        var targetList = targets.ToList();
        _ = BindingPreview.Catalog(sourceList, existing);
        if (targetList.Count > 0) _ = BindingPreview.Catalog(targetList, existing);
        var all = sourceList.Concat(targetList).ToList();
        BindingPreview.ValidateSources(all);
        all = all.DistinctBy(item => (item.From.RepositoryId, item.From.UnitPath)).ToList();
        var endpoints = targetList.SelectMany(item => item.Artifact.Nodes
            .Where(node => node.NodeKind == "Endpoint" && Parts(node.SymbolKey) is { } parts && !parts.Service.StartsWith('@'))
            .Select(node => new HttpEndpointEvidence(Source(item), node.SymbolKey,
                Evidence(item.Artifact, node.Evidence.Concat(item.Artifact.Edges
                    .Where(edge => edge.To == node.SymbolKey && edge.Relation is "EXPOSES" or "HANDLES").SelectMany(edge => edge.Evidence))))))
            .OrderBy(endpoint => endpoint.Key, StringComparer.Ordinal).ThenBy(endpoint => endpoint.Source.RepositoryId, StringComparer.Ordinal)
            .ThenBy(endpoint => endpoint.Source.UnitPath, StringComparer.Ordinal).ToList();
        var declared = all.SelectMany(item => item.Artifact.Nodes).Select(node => node.SymbolKey).ToHashSet(StringComparer.Ordinal);
        var existingHints = existing.Entries.ToDictionary(entry => entry.Hint, StringComparer.Ordinal);
        var bindings = Binder.Bind(all, existing).Edges;
        var requests = sourceList.SelectMany(item => item.Artifact.Edges.Where(edge => edge.Relation == "REQUESTS")
            .Where(edge => edge.To is null || !declared.Contains(edge.To))
            .Select(edge => new HttpRequestEvidence(Source(item), edge.Id, edge.To, Evidence(item.Artifact, edge.Evidence))))
            .OrderBy(request => request.Source.RepositoryId, StringComparer.Ordinal).ThenBy(request => request.Source.UnitPath, StringComparer.Ordinal)
            .ThenBy(request => request.EdgeId, StringComparer.Ordinal).ToList();
        var groups = new List<HttpRecommendationGroup>();
        foreach (var group in requests.GroupBy(request => Parts(request.Target) is { } parts
                     ? parts.Service : $"dynamic:{request.Source.RepositoryId}:{request.Source.UnitPath}:{request.EdgeId}", StringComparer.Ordinal))
        {
            var hint = group.Key.StartsWith("@unbound:", StringComparison.Ordinal) ? group.Key : null;
            var calls = group.ToList();
            var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(group.Key)));
            if (hint is not null && existingHints.ContainsKey(hint))
            {
                if (bindings.Any(bound => bound.Binding.Hint == hint && bound.Status == BindingStatus.Broken))
                    groups.Add(new(id, hint, group.Key, "broken-binding", calls, []));
                continue;
            }
            var signatures = calls.Where(call => Parts(call.Target) is not null).Select(call => Parts(call.Target)!.Value.Route)
                .Distinct(StringComparer.Ordinal).ToList();
            var candidates = new List<HttpBindingCandidate>();
            foreach (var service in endpoints.GroupBy(endpoint => Parts(endpoint.Key)!.Value.Service, StringComparer.Ordinal))
            {
                var matches = calls.SelectMany(call => service.Where(endpoint => Parts(endpoint.Key)!.Value.Route == Parts(call.Target)?.Route)
                    .Select(endpoint => new HttpRecommendationMatch(call, endpoint))).ToList();
                if (matches.Count == 0) continue;
                var covered = matches.Select(match => Parts(match.Request.Target)!.Value.Route).Distinct(StringComparer.Ordinal).Count();
                var score = 100 * covered / signatures.Count;
                var unmatched = calls.Where(call => !matches.Any(match => match.Request == call)).ToList();
                var eligible = false;
                if (hint is not null)
                {
                    var proposed = BindingFile.Parse(JsonSerializer.Serialize(new { bindings = new[] { new { hint, target = service.Key } } }));
                    var preview = Binder.Bind(all, proposed);
                    eligible = preview.Edges.Count > 0 && preview.Edges.All(edge => edge.Status == BindingStatus.Bound);
                }
                var missing = new List<string> { "Host ownership", "Request payload compatibility", "Query compatibility", "Response schema compatibility", "Headers", "Content type" };
                if (all.Any(item => item.Artifact.Complete.Status == "partial" &&
                    (calls.Any(call => call.Source == Source(item)) || service.Any(endpoint => endpoint.Source == Source(item)))))
                    missing.Add("Analysis coverage is partial");
                candidates.Add(new(service.Key, score, covered, signatures.Count, "candidate", eligible,
                    [new("Distinct method/route coverage (not confidence)", score)], matches, unmatched, missing));
            }
            var ordered = candidates.OrderByDescending(candidate => candidate.Score).ThenBy(candidate => candidate.Target, StringComparer.Ordinal).ToList();
            var ambiguous = ordered.Count > 1 && ordered[0].Score == ordered[1].Score;
            groups.Add(new(id, hint, group.Key, signatures.Count == 0 ? "insufficient-evidence" : ambiguous ? "ambiguous" : ordered.Count == 0 ? "no-candidate" : "candidate", calls, ordered));
        }
        return groups.OrderBy(group => group.Label, StringComparer.Ordinal).ThenBy(group => group.Id, StringComparer.Ordinal).ToList();
    }

    private static (string Service, string Route)? Parts(string? key)
    {
        var parts = key?.Split('|');
        return parts is { Length: 3 } && parts[0] == "http" ? (parts[1], parts[2]) : null;
    }

    private static BindingPreviewSource Source((LoadedFrom From, Artifact Artifact) item) =>
        new(item.From.RepositoryId, item.From.CommitSha, item.From.UnitPath, item.Artifact.ContentSha256);

    private static List<EvidenceRecord> Evidence(Artifact artifact, IEnumerable<string> ids)
    {
        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        return artifact.Evidence.Where(evidence => wanted.Contains(evidence.Id)).OrderBy(evidence => evidence.Id, StringComparer.Ordinal).ToList();
    }
}