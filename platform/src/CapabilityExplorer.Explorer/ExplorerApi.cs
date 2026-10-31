using CapabilityExplorer.Capabilities;
using CapabilityExplorer.Interpretation;

namespace CapabilityExplorer.Explorer;

public sealed record ExplorerOptions(
    IReadOnlyList<string> ArtifactDirectories,
    string? RepositoriesDirectory,
    string? EvaluationReport,
    string CapabilitiesFile,
    string Workspace)
{
    public static ExplorerOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var artifacts = (configuration["artifacts"] ?? "fixtures/expected").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new ExplorerOptions(
            artifacts,
            configuration["repos"],
            configuration["evaluation"],
            configuration["capabilities"] ?? "capabilities.json",
            configuration["workspace"] ?? ".sandbox-cache/sources");
    }

    /// <summary>Builds the index, resolving repository sources through capabilities first, then the repos directory.</summary>
    public (ExplorerIndex Index, CapabilityService Capabilities) Build()
    {
        var store = CapabilityStore.Open(CapabilitiesFile, Path.Combine(Workspace, "snapshots.json"));
        var auth = GitHubAuth.FromEnvironment();
        var sync = new SourceSync(Workspace, auth: auth);
        var git = new GitSource(id =>
            store.FindRepository(id) is { } source ? sync.GitDirectory(source)
            : RepositoriesDirectory is not null && GitSource.IsSafeSegment(id) ? Path.Combine(RepositoriesDirectory, id)
            : null, auth);
        var index = ExplorerIndex.Load(ArtifactDirectories, git);
        return (index, new CapabilityService(store, sync, index));
    }
}

public static class ExplorerApi
{
    private const int ContextLines = 4;

    public static void MapExplorerApi(this IEndpointRouteBuilder app, string? evaluationReport)
    {
        var api = app.MapGroup("/api");

        // Cheap poll target: the client reloads when the generation changes (artifacts or capabilities on disk changed).
        api.MapGet("/generation", (ExplorerState state) => new { generation = state.Current.Generation });

        api.MapGet("/meta", (ExplorerState state, InterpretationLayer interpretation) =>
        {
            var (index, capabilities, generation) = state.Current;
            return new
            {
                generation,
                capabilities = capabilities.List().Select(c => new { c.Id, c.Name, repositories = c.Repositories.Select(r => r.Id) }),
                githubAuth = new { configured = capabilities.Sync.Auth.Configured, source = capabilities.Sync.Auth.Source },
                interpretation = new { enabled = interpretation.Enabled, provider = interpretation.Options.Provider, model = interpretation.Explanations?.ModelId },
                revisions = index.Revisions,
                defaultView = index.DefaultSelection,
                artifacts = index.Artifacts.Count,
                problems = index.Problems,
                hasSource = index.Git is not null,
                hasEvaluation = evaluationReport is not null && File.Exists(evaluationReport),
                nodeKinds = index.Artifacts.SelectMany(a => a.Artifact.Nodes.Select(n => n.NodeKind)).Distinct().Order(StringComparer.Ordinal),
            };
        });

        // Every view can be scoped to a capability (cap=<id>): only its repositories, starting from its locked revisions.
        api.MapGet("/view", (ExplorerIndex index, CapabilityService caps, string? view, string? cap) => index.View(view, caps.Scope(cap)).Selection);

        api.MapGet("/search", (ExplorerIndex index, CapabilityService caps, string? q, string? kind, string? repo, string? view, string? cap) =>
            Queries.Search(index.View(view, caps.Scope(cap)), q, kind, repo));

        api.MapGet("/symbol", (ExplorerIndex index, CapabilityService caps, string key, string? view, string? cap) =>
            Queries.Symbol(index.View(view, caps.Scope(cap)), key) is { } detail ? Results.Ok(detail) : Results.NotFound(new { error = $"{key} is not declared in the selected revisions" }));

        api.MapGet("/calculations", (ExplorerIndex index, CapabilityService caps, string? view, string? cap) => Queries.Calculations(index.View(view, caps.Scope(cap))));

        // Layered architecture canvas: level = system | repository | component.
        api.MapGet("/architecture", (ExplorerIndex index, CapabilityService caps, string? level, string? focus, string? view, string? cap) =>
            Architecture.Build(index.View(view, caps.Scope(cap)), level ?? "system", focus));

        api.MapGet("/integrations", (ExplorerIndex index, CapabilityService caps, string? view, string? cap) => Queries.Integrations(index.View(view, caps.Scope(cap))));

        api.MapGet("/changes", (ExplorerIndex index, string repo, string from, string to) =>
            Changes.Compare(index, repo, from, to) is { } report ? Results.Ok(report) : Results.NotFound(new { error = "unknown repository or revision" }));

        api.MapGet("/artifacts", (ExplorerIndex index) => Queries.Artifacts(index));

        // Deterministic comparison: always available, no model involved.
        api.MapGet("/compare", (ExplorerIndex index, CapabilityService caps, string a, string b, string? view, string? cap) =>
            Comparisons.Compare(index.View(view, caps.Scope(cap)), a, b) is { } c
                ? Results.Ok(Comparisons.ToDto(c))
                : Results.NotFound(new { error = "both calculations must exist in the selected revisions" }));

        // Optional: an AI-written explanation of the deterministic comparison. Off unless a provider is configured.
        api.MapPost("/explanations", async (ExplorerIndex index, CapabilityService caps, InterpretationLayer interpretation, ExplainRequest request, CancellationToken ct) =>
        {
            if (interpretation.Explanations is not { } service)
            {
                return Results.Json(new { error = "LLM integration is disabled (interpretation:provider = none)", enabled = false }, statusCode: StatusCodes.Status409Conflict);
            }

            if (Comparisons.Compare(index.View(request.View, caps.Scope(request.Cap)), request.A, request.B) is not { } comparison)
            {
                return Results.NotFound(new { error = "both calculations must exist in the selected revisions" });
            }

            if (comparison.Differences.Count == 0)
            {
                return Results.UnprocessableEntity(new { error = "the calculations do not differ in any attribute; nothing to explain" });
            }

            try
            {
                return Results.Ok(await service.ExplainAsync(comparison, ct));
            }
            catch (InterpretationUnavailableException e)
            {
                return Results.Json(new { error = e.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

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

        api.MapGet("/capabilities", (CapabilityService caps) => caps.List());

        api.MapGet("/capabilities/{id}", (CapabilityService caps, string id) =>
            caps.Get(id) is { } c ? Results.Ok(c) : Results.NotFound(new { error = $"no capability '{id}'" }));

        // Define a capability from one repository per line: [id=]https://github.com/owner/repo [ref] | [id=]/local/path [ref]
        api.MapPost("/capabilities", async (CapabilityService caps, CreateCapabilityRequest request, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new { error = "a name is required" });
            }

            var (sources, errors) = SourceParser.Parse((request.Repositories ?? "").Split('\n'));
            if (errors.Count > 0)
            {
                return Results.BadRequest(new { error = "some repositories could not be read", lines = errors });
            }

            try
            {
                var created = await caps.Store.AddAsync(request.Name, request.Description, sources, ct);
                return Results.Created($"/api/capabilities/{created.Id}", await caps.CheckAsync(created.Id, ct));
            }
            catch (CapabilityConflictException e)
            {
                return Results.Conflict(new { error = e.Message });
            }
        });

        // "Check together": acquire every source read-only and lock each to one exact commit.
        api.MapPost("/capabilities/{id}/check", async (CapabilityService caps, string id, CancellationToken ct) =>
            caps.Store.Find(id) is null ? Results.NotFound(new { error = $"no capability '{id}'" }) : Results.Ok(await caps.CheckAsync(id, ct)));

        api.MapDelete("/capabilities/{id}", async (CapabilityService caps, string id, CancellationToken ct) =>
            await caps.Store.RemoveAsync(id, ct) ? Results.NoContent() : Results.NotFound(new { error = $"no capability '{id}'" }));

        api.MapGet("/evaluation", () =>
            evaluationReport is not null && File.Exists(evaluationReport)
                ? Results.Text(File.ReadAllText(evaluationReport), "application/json")
                : Results.NotFound(new { error = "no evaluation report: start the explorer with --evaluation <report.json>" }));
    }
}
