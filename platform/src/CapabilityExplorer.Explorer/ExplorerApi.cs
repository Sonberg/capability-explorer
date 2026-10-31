namespace CapabilityExplorer.Explorer;

public sealed record ExplorerOptions(IReadOnlyList<string> ArtifactDirectories, string? RepositoriesDirectory, string? EvaluationReport)
{
    public static ExplorerOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var artifacts = (configuration["artifacts"] ?? "fixtures/expected").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new ExplorerOptions(artifacts, configuration["repos"], configuration["evaluation"]);
    }

    public ExplorerIndex Build() =>
        ExplorerIndex.Load(ArtifactDirectories, RepositoriesDirectory is null ? null : new GitSource(RepositoriesDirectory));
}

public static class ExplorerApi
{
    private const int ContextLines = 4;

    public static void MapExplorerApi(this IEndpointRouteBuilder app, string? evaluationReport)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/meta", (ExplorerIndex index) => new
        {
            revisions = index.Revisions,
            defaultView = index.DefaultSelection,
            artifacts = index.Artifacts.Count,
            problems = index.Problems,
            hasSource = index.Git is not null,
            hasEvaluation = evaluationReport is not null && File.Exists(evaluationReport),
            nodeKinds = index.Artifacts.SelectMany(a => a.Artifact.Nodes.Select(n => n.NodeKind)).Distinct().Order(StringComparer.Ordinal),
        });

        api.MapGet("/search", (ExplorerIndex index, string? q, string? kind, string? repo, string? view) =>
            Queries.Search(index.View(view), q, kind, repo));

        api.MapGet("/symbol", (ExplorerIndex index, string key, string? view) =>
            Queries.Symbol(index.View(view), key) is { } detail ? Results.Ok(detail) : Results.NotFound(new { error = $"{key} is not declared in the selected revisions" }));

        api.MapGet("/calculations", (ExplorerIndex index, string? view) => Queries.Calculations(index.View(view)));

        api.MapGet("/integrations", (ExplorerIndex index, string? view) => Queries.Integrations(index.View(view)));

        api.MapGet("/changes", (ExplorerIndex index, string repo, string from, string to) =>
            Changes.Compare(index, repo, from, to) is { } report ? Results.Ok(report) : Results.NotFound(new { error = "unknown repository or revision" }));

        api.MapGet("/artifacts", (ExplorerIndex index) => Queries.Artifacts(index));

        // Only files that an artifact cites, at that artifact's commit: the explorer is not a general file server.
        api.MapGet("/source", (ExplorerIndex index, string repo, string commit, string path, int? start, int? end) =>
        {
            if (index.Git is null)
            {
                return Results.NotFound(new { error = "source not available: start the explorer with --repos" });
            }

            if (!index.IsCitedFile(repo, commit, path))
            {
                return Results.BadRequest(new { error = "only files cited by a loaded artifact can be shown" });
            }

            var text = index.Git.ReadFile(repo, commit, path);
            if (text is null)
            {
                return Results.NotFound(new { error = $"{path} not found at {commit[..Math.Min(12, commit.Length)]}" });
            }

            var lines = text.Split('\n');
            var s = Math.Clamp(start ?? 1, 1, lines.Length);
            var e = Math.Clamp(end ?? s, s, lines.Length);
            var from = start is null ? 1 : Math.Max(1, s - ContextLines);
            var to = start is null ? lines.Length : Math.Min(lines.Length, e + ContextLines);
            return Results.Ok(new
            {
                repo, commit, path, highlightStart = start is null ? (int?)null : s, highlightEnd = start is null ? (int?)null : e,
                totalLines = lines.Length,
                lines = Enumerable.Range(from, to - from + 1).Select(n => new { n, text = lines[n - 1] }),
            });
        });

        api.MapGet("/evaluation", () =>
            evaluationReport is not null && File.Exists(evaluationReport)
                ? Results.Text(File.ReadAllText(evaluationReport), "application/json")
                : Results.NotFound(new { error = "no evaluation report: start the explorer with --evaluation <report.json>" }));
    }
}
