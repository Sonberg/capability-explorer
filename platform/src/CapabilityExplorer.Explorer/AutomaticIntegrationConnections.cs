using System.Text.Json;
using CapabilityExplorer.Binding;
using CapabilityExplorer.Capabilities;

namespace CapabilityExplorer.Explorer;

internal sealed class AutomaticIntegrationConnections(string capabilitiesFile)
{
    private readonly IntegrationMatchCatalogueStore _store = new(Path.GetDirectoryName(Path.GetFullPath(capabilitiesFile))!);

    internal (string? Fingerprint, IReadOnlyList<IntegrationMatch> Matches) Selected(ViewIndex view, IReadOnlyList<CapabilityStatus> capabilities, string? capability = null)
    {
        var catalogue = _store.Read();
        if (catalogue is null) return (null, []);
        var artifacts = view.Artifacts.Select(artifact => (artifact.RepositoryId, artifact.CommitSha, artifact.Artifact.ContentSha256)).ToHashSet();
        var membership = capabilities.SelectMany(item => item.Repositories.Select(repository => (item.Id, repository.Id))).ToHashSet();
        return (catalogue.Fingerprint, catalogue.Matches.Where(match =>
            (capability is null || match.SourceCapabilityId == capability || match.TargetCapabilityId == capability)
            && membership.Contains((match.SourceCapabilityId, match.Source.RepositoryId))
            && membership.Contains((match.TargetCapabilityId, match.Target.RepositoryId))
            && artifacts.Contains((match.Source.RepositoryId, match.Source.CommitSha, match.Source.ArtifactHash))
            && artifacts.Contains((match.Target.RepositoryId, match.Target.CommitSha, match.Target.ArtifactHash))).ToList());
    }

    internal ArchitectureGraph Apply(ArchitectureGraph graph, ViewIndex view, IReadOnlyList<CapabilityStatus> capabilities, string? capability = null)
    {
        if (graph.Level is not ("landscape" or "system")) return graph;
        try
        {
            var matches = Selected(view, capabilities, graph.Level == "system" ? capability : null).Matches.Where(match => match.Status is "matched" or "inferred")
                .GroupBy(match => match.ClusterId is null ? match.Id : match.ClusterId + ":" + match.TargetCapabilityId);
            var nodes = graph.Nodes.ToList();
            var edges = graph.Edges.ToList();
            foreach (var group in matches)
            {
                var match = group.First();
                var inferred = match.Status == "inferred";
                var from = graph.Level == "landscape" ? "cap:" + match.SourceCapabilityId : "repo:" + match.Source.RepositoryId;
                var to = graph.Level == "landscape" ? "cap:" + match.TargetCapabilityId : "repo:" + match.Target.RepositoryId;
                if (!nodes.Any(node => node.Id == from || node.Id == to)) continue;
                foreach (var anchor in new[] { (Id: from, Repository: match.Source.RepositoryId, Capability: match.SourceCapabilityId, Key: match.SourceKey),
                             (Id: to, Repository: match.Target.RepositoryId, Capability: match.TargetCapabilityId, Key: match.TargetKey) })
                {
                    if (!nodes.Any(node => node.Id == anchor.Id))
                        nodes.Add(new(anchor.Id, "repository", anchor.Repository, null, null, anchor.Key, anchor.Repository,
                            new("repository", anchor.Repository), ["automatic integration"]));
                }
                var facts = group.Take(40).Select(item =>
                {
                    var evidence = item.SourceEvidence.Concat(item.TargetEvidence).Select(citation => new EvidenceDto(citation.Id,
                        citation.RepositoryId, citation.CommitSha, citation.Path, citation.Span.StartLine, citation.Span.StartColumn, citation.Span.EndLine, citation.Span.EndColumn)).ToList();
                    return new ArchFact(item.Relation,
                        new(item.SourceKey, Map.DisplayName(item.SourceKey), null, view.Node(item.SourceKey) is not null, item.Source.RepositoryId),
                        new(item.TargetKey, Map.DisplayName(item.TargetKey), null, view.Node(item.TargetKey) is not null, item.Target.RepositoryId),
                        inferred ? "candidate" : "resolved", inferred ? "inferred service association" : null,
                        "derived", IntegrationMatching.AlgorithmVersion, evidence, item.ResourceKey);
                }).ToList();
                var note = match.Reason;
                if (match.Contract is { } contract)
                    note += " Contract: " + contract.Status + ". " + string.Join(" ", contract.Differences.Concat(contract.Unknowns));
                edges.Add(new("automatic:" + group.Key, from, to, match.Kind,
                    match.Kind + (inferred ? " (inferred)" : " (automatic)"), group.Count(), inferred ? "candidate" : "bound", facts, Note: note));
            }
            return graph with { Nodes = nodes, Edges = edges };
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        { return graph with { Notes = [.. graph.Notes, "Automatic integration matches are unavailable; existing analyses and manual connections were preserved."] }; }
    }
}

internal static class AutomaticIntegrationConnectionsApi
{
    internal static void MapAutomaticIntegrationConnections(this IEndpointRouteBuilder api)
    {
        api.MapGet("/architecture/integration-matches", (ExplorerOptions options, ExplorerIndex index, CapabilityService capabilities,
            string? view, string? cap, int? offset, int? limit) =>
        {
            var start = offset ?? 0;
            var size = limit ?? 50;
            if (start < 0 || size is < 1 or > 100 || cap is not null && capabilities.Get(cap) is null)
                return Results.BadRequest(new { error = "Invalid capability or match page." });
            try
            {
                var selected = new AutomaticIntegrationConnections(options.CapabilitiesFile).Selected(index.View(view), capabilities.List(), cap);
                return Results.Ok(new { selected.Fingerprint, total = selected.Matches.Count,
                    matched = selected.Matches.Count(match => match.Status == "matched"),
                    inferred = selected.Matches.Count(match => match.Status == "inferred"),
                    clusters = selected.Matches.Where(match => match.ClusterId is not null).Select(match => match.ClusterId).Distinct(StringComparer.Ordinal).Count(),
                    suggested = selected.Matches.Count(match => match.Status == "suggested"), offset = start, limit = size,
                    matches = selected.Matches.Skip(start).Take(size).ToArray() });
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            { return Results.Json(new { error = "Integration catalogue unavailable; existing analyses were preserved." }, statusCode: 503); }
        });
    }
}