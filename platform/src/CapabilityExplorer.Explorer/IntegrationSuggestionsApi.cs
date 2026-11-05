using CapabilityExplorer.Contracts;
using CapabilityExplorer.Interpretation;

namespace CapabilityExplorer.Explorer;

public sealed record IntegrationSuggestionRequest(string SourceCapabilityId, string TargetCapabilityId,
    string? SourceRepositoryId = null, string? SourceQuery = null, string? View = null);

internal static class IntegrationSuggestionsApi
{
    public static void MapIntegrationSuggestions(this IEndpointRouteBuilder api)
    {
        api.MapPost("/architecture/integration-suggestions", async (IntegrationSuggestionRequest body, HttpRequest request,
            ExplorerIndex index, CapabilityService capabilities, Intelligence intelligence, CancellationToken cancellationToken) =>
        {
            if (!LocalRequestGuard.Allowed(request) || !request.HasJsonContentType() || request.Headers["X-CE-Integration-Suggestions"] != "1")
                return Results.Json(new { error = "Integration suggestions require a local same-origin JSON request." }, statusCode: 403);
            if (intelligence.Layer.Model is not { } model)
                return Results.Json(new { error = "Integration suggestions require a configured chat model (Settings)." }, statusCode: 409);
            if (string.IsNullOrEmpty(body.SourceCapabilityId) || string.IsNullOrEmpty(body.TargetCapabilityId)
                || body.SourceCapabilityId == body.TargetCapabilityId || body.SourceQuery?.Length > 200 || body.View?.Length > 8192)
                return Results.BadRequest(new { error = "Choose two different capabilities and a bounded source query." });
            var source = capabilities.Get(body.SourceCapabilityId);
            var target = capabilities.Get(body.TargetCapabilityId);
            if (source is null || target is null) return Results.BadRequest(new { error = "Unknown capability." });
            var sourceRepositories = source.Repositories.Select(repository => repository.Id).ToHashSet(StringComparer.Ordinal);
            var targetRepositories = target.Repositories.Select(repository => repository.Id).ToHashSet(StringComparer.Ordinal);
            if (body.SourceRepositoryId is { } repository)
            {
                if (!sourceRepositories.Contains(repository)) return Results.BadRequest(new { error = "Source repository does not belong to this capability." });
                sourceRepositories = [repository];
            }
            var words = (body.SourceQuery ?? target.Name).Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
                .Select(Normalize).Where(word => word.Length >= 3).Append(Normalize(target.Id)).Distinct(StringComparer.Ordinal).ToArray();
            var view = index.View(body.View);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                var evidence = new List<IntegrationSuggestionEvidence>();
                var sourceKeys = await Gather(view, sourceRepositories, "source", words, evidence, timeout.Token);
                var targetKeys = await Gather(view, targetRepositories, "target", words, evidence, timeout.Token);
                var result = await new IntegrationSuggestionService(model).SuggestAsync(new(source.Id, target.Id, sourceKeys, targetKeys, evidence), timeout.Token);
                return Results.Ok(result);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { return Results.Json(new { error = "Integration suggestion timed out; no connection was adopted." }, statusCode: 504); }
            catch (Exception error) when (error is InterpretationUnavailableException or IOException or InvalidOperationException)
            { return Results.Json(new { error = "Integration suggestion unavailable; existing analyses and connections were preserved." }, statusCode: 503); }
        });
    }

    private static async Task<IReadOnlyList<string>> Gather(ViewIndex view, HashSet<string> repositories, string side,
        string[] words, List<IntegrationSuggestionEvidence> evidence, CancellationToken cancellationToken)
    {
        var nodes = view.Artifacts.Where(artifact => repositories.Contains(artifact.RepositoryId))
            .SelectMany(artifact => artifact.Artifact.Nodes.Select(node => (Artifact: artifact, Node: node)))
            .Select(item => (item.Artifact, item.Node, Score: words.Sum(word =>
                (Normalize(item.Node.Name).Contains(word, StringComparison.Ordinal) ? 4 : 0)
                + (Normalize(item.Node.SymbolKey).Contains(word, StringComparison.Ordinal) ? 1 : 0))
                + (side == "target" && item.Node.NodeKind == "Endpoint" ? 20 : 0)))
            .OrderByDescending(item => item.Score).ThenBy(item => item.Node.SymbolKey, StringComparer.Ordinal).ToList();
        var boundaryPaths = view.Artifacts.Where(artifact => repositories.Contains(artifact.RepositoryId))
            .SelectMany(artifact => artifact.Artifact.Edges.Where(edge => edge.Relation is "REQUESTS" or "PUBLISHES" or "CONSUMES" or "READS" or "WRITES")
                .SelectMany(edge => edge.Evidence).Distinct(StringComparer.Ordinal)
                .Select(id => artifact.EvidenceById.GetValueOrDefault(id)).OfType<EvidenceRecord>()
                .Select(item => (artifact.RepositoryId, artifact.CommitSha, item.Path))).ToHashSet();
        var files = nodes.SelectMany(item => item.Node.Evidence.Select(id => item.Artifact.EvidenceById.GetValueOrDefault(id))
                .OfType<EvidenceRecord>().Select(itemEvidence => (item.Artifact, item.Node, item.Score, Evidence: itemEvidence)))
            .Where(item => item.Evidence.Path.EndsWith(".cs", StringComparison.Ordinal)
                || item.Evidence.Path.EndsWith(".ts", StringComparison.Ordinal) || item.Evidence.Path.EndsWith(".tsx", StringComparison.Ordinal))
            .Where(item => !item.Evidence.Path.Split('/').Any(part => part.Equals("tests", StringComparison.OrdinalIgnoreCase)
                || part.EndsWith("Tests", StringComparison.OrdinalIgnoreCase) || part.Contains("TestBase", StringComparison.OrdinalIgnoreCase)))
            .GroupBy(item => (item.Artifact.RepositoryId, item.Artifact.CommitSha, item.Evidence.Path))
            .OrderByDescending(group => group.Max(item => item.Score)
                + (side == "source" && words.Any(word => Normalize(group.Key.Path).Contains(word, StringComparison.Ordinal)) ? 20 : 0)
                + (side == "source" && boundaryPaths.Contains(group.Key) ? 20 : 0)
                + (side == "source" && group.Key.Path.EndsWith("RestClient.cs", StringComparison.Ordinal) ? 20 : 0)
                - (group.Key.Path.Contains("/Interfaces/", StringComparison.Ordinal) ? 12 : 0)
                + (side == "target" ? 5 * group.Where(item => item.Node.NodeKind == "Endpoint"
                    && !item.Node.SymbolKey.EndsWith("|GET /", StringComparison.Ordinal)).Select(item => item.Node.SymbolKey).Distinct().Count() : 0)
                + (side == "target" && words.Any(word => word.Contains("hmesh", StringComparison.Ordinal))
                    && group.Key.Path.Contains("Consumer", StringComparison.Ordinal) ? 60 : 0))
            .ThenBy(group => group.Key.Path, StringComparer.Ordinal).Take(3).ToList();
        if (view.Index.Git is not { } git) return [];
        foreach (var file in files)
        {
            var text = await git.ReadFileAsync(file.Key.RepositoryId, file.Key.CommitSha, file.Key.Path, cancellationToken);
            if (text is null) continue;
            var lines = text.Split('\n');
            var anchor = file.Where(item => item.Node.NodeKind == "Method").OrderByDescending(item => item.Score)
                .ThenBy(item => item.Evidence.Span.StartLine).FirstOrDefault();
            var starts = new[] { 0, Math.Max(0, (anchor.Evidence?.Span.StartLine ?? 1) - 11) }.Distinct().ToList();
            foreach (var start in starts)
            {
                var page = lines.Skip(start).Take(160).ToList();
                while (page.Count > 0 && string.Join('\n', page).Length > 10000) page.RemoveAt(page.Count - 1);
                if (page.Count == 0) continue;
                if (evidence.Sum(item => item.Text.Length) + string.Join('\n', page).Length > 96000) break;
                evidence.Add(new(evidence.Count + 1, side, file.Key.RepositoryId, file.Key.CommitSha,
                    file.Key.Path, start + 1, start + page.Count, string.Join('\n', page)));
            }
        }
        var selectedPaths = files.Select(file => file.Key).ToHashSet();
        return nodes.Where(item => item.Node.Evidence.Any(id => item.Artifact.EvidenceById.TryGetValue(id, out var itemEvidence)
                && selectedPaths.Contains((item.Artifact.RepositoryId, item.Artifact.CommitSha, itemEvidence.Path))))
            .Take(64).Select(item => item.Node.SymbolKey).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string Normalize(string value) => new(value.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}