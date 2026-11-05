using System.Globalization;
using CapabilityExplorer.Binding;
using CapabilityExplorer.Capabilities;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Interpretation;
using CapabilityExplorer.Store;

namespace CapabilityExplorer.Explorer;

public sealed record ExplorerOptions(
    IReadOnlyList<string> ArtifactDirectories,
    string? RepositoriesDirectory,
    string? EvaluationReport,
    string CapabilitiesFile,
    string Workspace,
    string? BindingsFile = null,
    string RequestsDirectory = ".sandbox-cache/requests",
    IReadOnlyList<string>? CollectionDirectories = null)
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
            configuration["workspace"] ?? ".sandbox-cache/sources",
            configuration["bindings"],
            configuration["requests"] ?? ".sandbox-cache/requests",
            (configuration["collections"] ?? "").Split(',',StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>Builds the index, resolving repository sources through capabilities first, then the repos directory.</summary>
    public (ExplorerIndex Index, CapabilityService Capabilities) Build()
    {
        var store = CapabilityStore.Open(CapabilitiesFile, Path.Combine(Workspace, "snapshots.json"), loadSnapshots: false);
        var git = new GitSource(id =>
            store.FindRepository(id) is { } source ? SourcePathResolver.GitDirectory(source, Workspace)
            : RepositoriesDirectory is not null && GitSource.IsSafeSegment(id) ? Path.Combine(RepositoriesDirectory, id)
            : null);
        BindingFile? bindings = null;
        string? problem = null;
        if (BindingsFile is not null)
        {
            try
            {
                bindings = BindingFile.Load(BindingsFile);
            }
            catch (Exception e) when (e is FormatException or IOException)
            {
                problem = $"{BindingsFile}: {e.Message}"; // shown in the UI, and nothing is bound
            }
        }

        var index = ExplorerIndex.Load(ArtifactDirectories, git, bindings, problem);
        return (index, new CapabilityService(store, this, index));
    }
}

public sealed record MarkRequest(string Key, string Status, string? Note, long? ExpectedVersion = null);

public static class ExplorerApi
{
    private const int ContextLines = 4;

    private static IResult SimilarityOff(Intelligence intelligence) => Results.Json(
        new { error = intelligence.Store is null ? "similarity search needs the database (set CE_DATABASE)" : "no embedding model is configured (Settings, or make ollama)" },
        statusCode: StatusCodes.Status409Conflict);

    private static IResult DatabaseUnavailable(Npgsql.NpgsqlException e) => Results.Json(
        new { error = e is Npgsql.PostgresException { SqlState: "42P01" } ? "no embeddings yet: run `ce embed` (or make analyze with a model configured)" : $"database: {e.Message}" },
        statusCode: StatusCodes.Status503ServiceUnavailable);

    internal static List<SimilarHit> InView(ViewIndex view, IReadOnlyList<SimilarSymbol> hits, int limit) =>
        hits.Select(h => (h, Node: view.Node(h.SymbolKey)))
            .Where(x => x.Node is not null)
            .Take(limit)
            .Select(x => new SimilarHit(x.h.SymbolKey, x.Node!.Record.Name, x.Node.Record.NodeKind, x.Node.Artifact.RepositoryId,
                x.Node.Artifact.Artifact.Header.Unit.Name, view.Calculations.ContainsKey(x.h.SymbolKey), Math.Round(x.h.Score, 3)))
            .ToList();

    private static object ModelStatus(Intelligence intelligence)
    {
        var o = intelligence.Layer.Options;
        return new
        {
            source = intelligence.Saved is not null ? "saved" : o.Enabled ? "configuration" : "none",
            settingsPath = intelligence.SettingsPath,
            provider = o.Provider,
            endpoint = o.Endpoint,
            chatModel = o.Deployment,
            embeddingModel = o.EmbeddingModel,
            ollamaEndpoint = ModelSettings.OllamaEndpoint,
            hasApiKey = intelligence.Saved?.ApiKey is { Length: > 0 } || o.UseEnvironmentApiKey && Environment.GetEnvironmentVariable(o.ApiKeyVariable) is { Length: > 0 },
            apiKeySource = intelligence.Saved?.ApiKey is { Length: > 0 } ? "saved" : o.UseEnvironmentApiKey && Environment.GetEnvironmentVariable(o.ApiKeyVariable) is { Length: > 0 } ? o.ApiKeyVariable : "none",
            database = intelligence.Store is not null,
            problem = intelligence.Problem,
        };
    }

    public static void MapExplorerApi(this IEndpointRouteBuilder app, string? evaluationReport)
    {
        var api = app.MapGroup("/api");
        api.MapArchitectureConnections();
        api.MapIntegrationSuggestions();
        api.MapAutomaticIntegrationConnections();

        // Snapshot identity also changes on restart, when the numeric generation can still be zero.
        api.MapGet("/generation", (ExplorerState state) =>
        {
            var current = state.Current;
            return new { generation = current.Generation, snapshotId = current.ModelSnapshotId };
        });

        api.MapGet("/meta", (ExplorerState state, InterpretationLayer interpretation, Intelligence intelligence) => SourceCheckApi.Respond(() =>
        {
            var current = state.Current;
            var (index, capabilities, generation) = current;
            return Results.Ok(new
            {
                generation,
                snapshotId = current.ModelSnapshotId,
                capabilities = capabilities.List().Select(c => new { c.Id, c.Name, repositories = c.Repositories.Select(r => r.Id) }),
                githubAuth = new { source = "host-only", hostManaged = true },
                interpretation = new { enabled = interpretation.Enabled, chat = interpretation.Chat is not null, provider = interpretation.Options.Provider, model = interpretation.Explanations?.ModelId },
                similarity = new { database = intelligence.Store is not null, embeddings = intelligence.Embeddings?.Id },
                revisions = index.Revisions,
                defaultView = index.DefaultSelection,
                artifacts = index.Artifacts.Count,
                problems = index.Problems,
                hasSource = index.Git is not null,
                hasEvaluation = evaluationReport is not null && File.Exists(evaluationReport),
                nodeKinds = index.Artifacts.SelectMany(a => a.Artifact.Nodes.Select(n => n.NodeKind)).Distinct().Order(StringComparer.Ordinal),
            });
        }));

        // Every view can be scoped to a capability (cap=<id>): only its repositories, starting from its locked revisions.
        api.MapGet("/view", (ExplorerIndex index, CapabilityService caps, string? view, string? cap) => index.View(view, caps.Scope(cap)).Selection);

        api.MapGet("/search", (ExplorerState state, ExplorerQueryCursor cursors, string? q, string? kind, string? repo, string? view, string? cap, int? limit, string? cursor, Guid? snapshot) =>
        {
            var current = state.Current;
            if (!string.IsNullOrEmpty(cap) && current.Capabilities.Get(cap) is null)
                return Results.BadRequest(new { code = "unknown-capability", error = "unknown capability" });
            var selected = current.Index.View(view, current.Capabilities.Scope(cap));
            if (limit is null && cursor is null)
            {
                return Results.Ok(Queries.Search(selected, q, kind, repo));
            }

            var size = limit ?? 50;
            if (size is < 1 or > 100)
            {
                return Results.BadRequest(new { error = "page limit must be 1 to 100" });
            }

            if (snapshot is not null && snapshot != current.ModelSnapshotId)
                return Results.Conflict(new { code = "stale-snapshot", error = "Results changed. Reload the results to continue." });
            if (cursor is not null && snapshot is null)
                return Results.BadRequest(new { code = "invalid-cursor", error = "continuation requires snapshot" });

            var context = new ExplorerQueryContext(current.ModelSnapshotId, cap ?? "", current.Generation, selected.Selection);
            var filter = System.Text.Json.JsonSerializer.Serialize(new { endpoint = "file-search.v1", q = (q ?? "").Trim(), kind = kind ?? "", repo = repo ?? "", size });
            var offset = 0;
            try
            {
                var position = cursors.Decode(context, filter, cursor);
                if (position is not null && (position.Length != 1
                    || !int.TryParse(position[0], NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0))
                    return Results.BadRequest(new { code = "invalid-cursor", error = "invalid continuation cursor" });
            }
            catch (ArgumentException)
            {
                return Results.BadRequest(new { code = "invalid-cursor", error = "invalid or incompatible continuation cursor" });
            }
            var page = Queries.SearchSlice(selected, q, kind, repo, offset, size);
            if (offset > page.Total)
                return Results.BadRequest(new { code = "invalid-cursor", error = "cursor is outside the result set" });
            var next = page.Items.Count + offset < page.Total
                ? cursors.Encode(context, filter, (offset + page.Items.Count).ToString(CultureInfo.InvariantCulture))
                : null;
            return Results.Ok(new { items = page.Items, nextCursor = next, total = page.Total,
                snapshotId = current.ModelSnapshotId, generation = current.Generation });
        });

        // Similarity (pgvector): stored vectors of the configured embedding model, limited to what the view shows.
        api.MapGet("/similar", async (ExplorerIndex index, CapabilityService caps, Intelligence intelligence, string key, string? view, string? cap, int? k, CancellationToken ct) =>
        {
            if (intelligence.Store is not { } store || intelligence.Embeddings is not { } model)
            {
                return SimilarityOff(intelligence);
            }

            try
            {
                var hits = await store.SimilarAsync(key, model.Id, Math.Clamp(k ?? 10, 1, 50) * 3, ct);
                return Results.Ok(InView(index.View(view, caps.Scope(cap)), hits, Math.Clamp(k ?? 10, 1, 50)));
            }
            catch (Npgsql.NpgsqlException e)
            {
                return DatabaseUnavailable(e);
            }
        });

        api.MapGet("/search/semantic", async (ExplorerIndex index, CapabilityService caps, Intelligence intelligence, string q, string? view, string? cap, CancellationToken ct) =>
        {
            if (intelligence.Store is not { } store || intelligence.Embeddings is not { } model)
            {
                return SimilarityOff(intelligence);
            }

            if (q.Trim().Length is < 3 or > 500)
            {
                return Results.BadRequest(new { error = "the query must be 3 to 500 characters" });
            }

            try
            {
                var vector = (await model.EmbedAsync([q.Trim()], ct))[0];
                return Results.Ok(InView(index.View(view, caps.Scope(cap)), await store.NearestAsync(vector, model.Id, 30, ct), 10));
            }
            catch (InterpretationUnavailableException e)
            {
                return Results.Json(new { error = e.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (Npgsql.NpgsqlException e)
            {
                return DatabaseUnavailable(e);
            }
        });

        // Model connection (local Ollama or Foundry), editable from the Settings page. The API key is write-only: it is never sent back.
        api.MapGet("/settings/model", (Intelligence intelligence) => ModelStatus(intelligence));

        api.MapPut("/settings/model", (Intelligence intelligence, ModelSettingsRequest request) =>
            intelligence.Save(request) is { } problem ? Results.BadRequest(new { error = problem }) : Results.Ok(ModelStatus(intelligence)));

        api.MapGet("/settings/package-feeds", (PackageFeedSettingsStore settings) => settings.Status());

        api.MapPut("/settings/package-feeds", () => Results.Json(new
        {
            error = "Package feed configuration and credentials moved to the host. Explorer is read-only. Use ce package-feeds --import <old-settings.json> --catalog .sandbox-cache/package-feed-catalog/catalog.json on the host.",
        }, statusCode: StatusCodes.Status410Gone));

        // Round-trips one short text through the embedding model (the chat model is exercised by explanations).
        api.MapPost("/settings/model/test", async (Intelligence intelligence, CancellationToken ct) =>
        {
            if (intelligence.Embeddings is not { } model)
            {
                return Results.Json(new { error = "no embedding model is configured" }, statusCode: StatusCodes.Status409Conflict);
            }

            try
            {
                var vector = (await model.EmbedAsync(["capability explorer connection test"], ct))[0];
                return Results.Ok(new { model = model.Id, dimensions = vector.Length });
            }
            catch (InterpretationUnavailableException e)
            {
                return Results.Json(new { error = e.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        api.MapGet("/symbol", (ExplorerIndex index, CapabilityService caps, string key, string? view, string? cap) =>
            Queries.Symbol(index.View(view, caps.Scope(cap)), key) is { } detail ? Results.Ok(detail) : Results.NotFound(new { error = $"{key} is not declared in the selected revisions" }));

        api.MapGet("/calculations", (ExplorerIndex index, CapabilityService caps, string? view, string? cap) => Queries.Calculations(index.View(view, caps.Scope(cap))));

        static Dictionary<string, IReadOnlyList<string>> Membership(CapabilityService caps) => caps.Store.Capabilities
            .SelectMany(c => c.Repositories.Select(r => (Repository: r.Id, Capability: c.Id)))
            .GroupBy(x => x.Repository, StringComparer.Ordinal)
            .ToDictionary(g => g.Key,
                g => (IReadOnlyList<string>)g.Select(x => x.Capability).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);

        api.MapGet("/glossary", (ExplorerIndex index, CapabilityService caps, string? view, string? cap) =>
            Glossary.Build(index.View(view, caps.Scope(cap)), Membership(caps)));

        // MADR-0005: ranked concepts with reasons, over the same selected artifacts. Additive; /glossary is unchanged.
        api.MapGet("/glossary/concepts", (ExplorerIndex index, CapabilityService caps, string? view, string? cap) =>
            GlossaryConcepts.Build(index.View(view, caps.Scope(cap)), Membership(caps)));

        // Layered architecture canvas: level = landscape | system | repository | component. The landscape ignores cap.
        api.MapGet("/architecture", (HttpRequest request, ExplorerIndex index, CapabilityService caps, ExplorerOptions explorerOptions, string? level, string? focus, string? view, string? cap) =>
        {
            if (!ArchitectureBudgeting.TryParse(request.Query, out var budgetOptions, out var error))
                return Results.BadRequest(new { error });
            var capability = string.IsNullOrEmpty(cap) ? null : caps.Get(cap);
            try
            {
                var graph = Architecture.Build(index.View(view, caps.Scope(cap)), level ?? (capability is null ? "landscape" : "system"), focus, index.View(view), caps.List(), capability,
                    budgetOptions.NodeBudget, budgetOptions.EdgeBudget, budgetOptions.HideKinds, budgetOptions.Query, budgetOptions.Include);
                graph = new AutomaticIntegrationConnections(explorerOptions.CapabilitiesFile).Apply(graph, index.View(view), caps.List(), capability?.Id);
                return Results.Ok(new ArchitectureConnections(explorerOptions.CapabilitiesFile).Apply(graph, caps.List()));
            }
            catch (ArchitectureBudgetRejectedException e)
            {
                return Results.BadRequest(new { error = e.Message });
            }
        });

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

        // Question answering over the selected view: retrieval (vector, names, glossary) plus a chat model that cites the items.
        api.MapPost("/chat", async (ExplorerIndex index, CapabilityService caps, Intelligence intelligence, ChatRequest request, CancellationToken ct) =>
        {
            if (intelligence.Layer.Chat is null)
            {
                return Results.Json(new { error = "no chat model is configured (Settings, or make ollama)" }, statusCode: StatusCodes.Status409Conflict);
            }

            if (request.Messages is not { Count: > 0 } || request.Messages.Count > Chat.MaxTurns
                || request.Messages[^1].Role != "user" || request.Messages[^1].Content.Trim().Length is 0 or > Chat.MaxQuestion
                || request.Messages.Any(m => m.Role is not ("user" or "assistant")))
            {
                return Results.BadRequest(new { error = $"send up to {Chat.MaxTurns} user/assistant messages ending with a question of 1 to {Chat.MaxQuestion} characters" });
            }

            try
            {
                return Results.Ok(await Chat.AnswerAsync(request, index.View(request.View, caps.Scope(request.Cap)), Membership(caps), intelligence, ct));
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

        api.MapGet("/capabilities", (CapabilityService caps) => SourceCheckApi.Respond(() => Results.Ok(caps.List())));

        api.MapGet("/capabilities/{id}", (CapabilityService caps, string id) =>
            SourceCheckApi.Respond(() => caps.Get(id) is { } c ? Results.Ok(c) : Results.NotFound(new { error = $"no capability '{id}'" })));

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
                try
                {
                    var admission = caps.AdmitCheck(created.Id);
                    var check = caps.CheckStatus(admission.Check, !admission.Accepted);
                    return Results.Accepted(check.StatusUrl, new { created = true, queued = true, capability = caps.Get(created.Id), check });
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
                    or ArgumentException or FormatException or System.Text.Json.JsonException or PlatformNotSupportedException)
                {
                    return Results.Json(new
                    {
                        created = true, queued = false,
                        capability = new { created.Id, created.Name, created.Description },
                        error = "Capability was created, but its check could not be queued. Inspect the host worker and retry Check; do not recreate the capability.",
                    }, statusCode: e is InvalidOperationException ? 409 : 503);
                }
            }
            catch (CapabilityConflictException e)
            {
                return Results.Conflict(new { error = e.Message });
            }
        });

        api.MapSourceChecks();

        api.MapPost("/capabilities/{id}/rebuild", async (ExplorerIndex index, CapabilityService caps, Intelligence intelligence, string id, CancellationToken ct) =>
        {
            if (caps.Store.Find(id) is null)
            {
                return Results.NotFound(new { error = $"no capability '{id}'" });
            }

            if (intelligence.DataSource is not { } db || intelligence.Store is not { } store || intelligence.Embeddings is not { } model)
            {
                return SimilarityOff(intelligence);
            }
            try
            {
                await Migrator.MigrateAsync(db, ct);
                var importer = new ArtifactImporter(db);
                var stored = new List<(long ArtifactId, Artifact Artifact)>();
                var imported = 0;
                var failed = new List<string>();
                foreach (var a in index.Artifacts)
                {
                    try
                    {
                        var result = await importer.ImportAsync(a.Artifact, ct);
                        stored.Add((result.ArtifactId, a.Artifact));
                        imported += result.Outcome == ImportOutcome.Imported ? 1 : 0;
                    }
                    catch (Exception e) when (e is InvalidArtifactException or ArtifactConflictException)
                    {
                        failed.Add($"{a.RepositoryId}@{a.CommitSha[..Math.Min(12, a.CommitSha.Length)]} {a.Artifact.Header.Unit.Name}: {e.Message}");
                    }
                }

                if (index.Bindings.Entries.Count > 0 && stored.Count > 0)
                {
                    await new BindingStore(db).SaveAsync(stored, index.Bindings, ct);
                }

                var embedded = await store.EmbedPendingAsync(model.Id, model.EmbedAsync, cancellationToken: ct);
                return Results.Ok(new { imported, unchanged = stored.Count - imported, failed, embedded, model = model.Id });
            }
            catch (Npgsql.NpgsqlException e)
            {
                return DatabaseUnavailable(e);
            }
            catch (InterpretationUnavailableException e)
            {
                return Results.Json(new { error = e.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        // Admission is one journal operation. The host worker consumes frozen accepted inputs.
        api.MapPost("/capabilities/{id}/analyze", (CapabilityService caps, ExplorerOptions options, string id) =>
            AnalysisJob.Respond(() =>
            {
                var definition = caps.Store.Find(id);
                if (definition is null) return Results.NotFound(new { error = $"no capability '{id}'" });
                var store = new CapabilityExplorer.Capabilities.Analysis.FileRunStore(options.RequestsDirectory);
                var profile = store.ReadWorkerDefaults();
                var settings = (profile?.Settings ?? new CapabilityExplorer.Capabilities.Analysis.AnalysisSettings()) with
                {
                    IncludeTests = profile?.IncludeTestsOverride ?? definition.IncludeTests,
                };
                var result = store.Admit(id, options.CapabilitiesFile, options.BindingsFile, settings);
                var job = AnalysisJob.Project(store, result.Run);
                return result.Accepted
                    ? Results.Accepted($"/api/capabilities/{id}/analyze?runId={result.Run.RunId}", job)
                    : Results.Conflict(new { error = "An unresolved analysis already exists for this capability.", job });
            }));

        api.MapPost("/capabilities/{id}/retry", (CapabilityService caps, ExplorerOptions options, string id, RetryAnalysisRequest request) =>
            AnalysisJob.Respond(() =>
            {
                if (!request.AcknowledgeUnverifiedExecution)
                    return Results.BadRequest(new { error = "Explicit acknowledgement of unverified execution is required before retrying." });
                var definition = caps.Store.Find(id);
                if (definition is null) return Results.NotFound(new { error = $"no capability '{id}'" });
                var store = new CapabilityExplorer.Capabilities.Analysis.FileRunStore(options.RequestsDirectory);
                using var owner = store.AcquireWorker();
                var profile = store.ReadWorkerDefaults();
                var settings = (profile?.Settings ?? new CapabilityExplorer.Capabilities.Analysis.AnalysisSettings()) with
                {
                    IncludeTests = profile?.IncludeTestsOverride ?? definition.IncludeTests,
                };
                try { store.RetireCancelledExecution(owner,id,new(request.RunId,request.AttemptId)); }
                catch (InvalidOperationException)
                {
                    return Results.Conflict(new { error = "Retry requires the exact cancelled, interrupted run with no publication evidence. Refresh status before retrying; publication recovery needs host review." });
                }
                var admitted = store.Admit(id,options.CapabilitiesFile,options.BindingsFile,settings);
                var job = AnalysisJob.Project(store,admitted.Run);
                return admitted.Accepted
                    ? Results.Accepted($"/api/capabilities/{id}/analyze?runId={admitted.Run.RunId}",job)
                    : Results.Conflict(new { error = "An analysis run is already queued for this capability. Refresh status to follow it.", job });
            }));

        // Omitting runId retains the legacy latest-capability target. New clients always send the observed run ID.
        api.MapDelete("/capabilities/{id}/analyze", (CapabilityService caps, ExplorerOptions options, string id, string? runId) =>
            AnalysisJob.Respond(() =>
            {
                if (caps.Store.Find(id) is null) return Results.NotFound(new { error = $"no capability '{id}'" });
                var store = new CapabilityExplorer.Capabilities.Analysis.FileRunStore(options.RequestsDirectory);
                var target = AnalysisJob.Read(store, id, runId);
                if (target.RunId is null) return Results.Conflict(new { error = "No cancellable analysis run exists." });
                var result = store.Cancel(target.RunId);
                var job = AnalysisJob.Project(store, result.Run, result.Outcome);
                return result.Outcome is "accepted" or "already-cancelled" ? Results.Ok(job)
                    : Results.Conflict(new { error = "Cancellation is too late or the run is already terminal.", job });
            }));

        api.MapGet("/capabilities/{id}/analyze", (CapabilityService caps, ExplorerOptions options, string id, string? runId) =>
            AnalysisJob.Respond(() => caps.Store.Find(id) is null
                ? Results.NotFound(new { error = $"no capability '{id}'" })
                : Results.Ok(AnalysisJob.Read(new CapabilityExplorer.Capabilities.Analysis.FileRunStore(options.RequestsDirectory), id, runId))));

        api.MapGet("/analysis-status", (CapabilityService caps, ExplorerOptions options) =>
            AnalysisJob.Respond(() => Results.Ok(AnalysisJob.ReadAll(
                new CapabilityExplorer.Capabilities.Analysis.FileRunStore(options.RequestsDirectory), caps.Store.Capabilities.Select(capability => capability.Id)))));

        api.MapDelete("/capabilities/{id}", async (CapabilityService caps, string id, CancellationToken ct) =>
            await caps.Store.RemoveAsync(id, ct) ? Results.NoContent() : Results.NotFound(new { error = $"no capability '{id}'" }));

        api.MapGet("/evaluation", () =>
            evaluationReport is not null && File.Exists(evaluationReport)
                ? Results.Text(File.ReadAllText(evaluationReport), "application/json")
                : Results.NotFound(new { error = "no evaluation report: start the explorer with --evaluation <report.json>" }));
    }
}
