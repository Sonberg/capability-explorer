using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Contracts.Collection;
using CapabilityExplorer.Interpretation;
using CapabilityExplorer.Store;
using Npgsql;

namespace CapabilityExplorer.Explorer.GeneratedModels;

internal sealed class GeneratedModelRequestException(string code, string message, int statusCode) : Exception(message)
{
    internal string Code { get; } = code;
    internal int StatusCode { get; } = statusCode;
}

public sealed record GeneratedModelAuthorizationStatus(
    string Provider,
    string? Model,
    bool ProviderConfigured,
    bool ProviderAuthorized,
    bool SourceSnippetsAuthorized,
    bool ModelEvidenceAccessGranted,
    bool SourceExcerptAccessGranted,
    string DataPolicy);

public sealed record GeneratedModelLatestResponse(
    GeneratedModelSnapshot Snapshot,
    bool Stale,
    string? CurrentSnapshotId,
    long? CurrentGeneration);

public sealed partial class GeneratedModelAnalysisService(
    ExplorerOptions options,
    ITaskEvidenceSource source,
    TaskQueryAccessPolicy access,
    Intelligence intelligence,
    ILogger<GeneratedModelAnalysisService> logger) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };
    private const int MaximumSelection = 100;
    private const int MaximumPromptBytes = 2 * 1024 * 1024;
    private const int MaximumSnippetBytes = 16 * 1024;
    private readonly GeneratedModelStore _store = new(Path.GetDirectoryName(Path.GetFullPath(options.CapabilitiesFile))!);
    private readonly SemaphoreSlim _runSlot = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new(StringComparer.Ordinal);

    public GeneratedModelAuthorizationStatus AuthorizationStatus()
    {
        var saved = intelligence.Saved;
        var model = intelligence.Layer.Model;
        return new GeneratedModelAuthorizationStatus(
            model is null ? "none" : intelligence.Layer.Options.Provider,
            model?.Id,
            model is not null,
            saved?.AllowGeneratedModelAnalysis == true,
            saved?.AllowGeneratedModelAnalysis == true && saved.AllowSourceSnippets,
            access.AllowsOperation(TaskQueryOperations.SnapshotResolve)
                && access.AllowsOperation(TaskQueryOperations.ModelsList),
            access.AllowsOperation(TaskQueryOperations.SourceExcerpt),
            "Published model evidence only by default; snapshot_resolve and models_list grants are required, and source excerpts require separate Settings consent and source_excerpt rights.");
    }

    public GeneratedModelStoreDocument Status()
    {
        var document = _store.Read();
        if (document.LatestRun is { } latestRun) RequireReadAccess(latestRun.Selection);
        if (document.LatestRun is not { Status: "running" } run || _active.ContainsKey(run.RunId)) return document;
        var interrupted = run with { Status = "failed", CompletedAt = DateTimeOffset.UtcNow, Error = "run-interrupted" };
        return document with { LatestRun = interrupted };
    }

    public bool Cancel(string runId) =>
        Guid.TryParseExact(runId, "D", out _) && _active.TryGetValue(runId, out var cancellation)
        && Cancel(cancellation);

    public async Task<GeneratedModelLatestResponse?> LatestAsync(CancellationToken cancellationToken)
    {
        var snapshot = _store.Read().LatestSuccessful;
        if (snapshot is null) return null;
        RequireReadAccess(snapshot.Selection);
        TaskSnapshotScope? current;
        try { current = await source.ResolveAsync(snapshot.Capability, snapshot.Selection, null, null, cancellationToken); }
        catch (Exception error) when (error is TaskQueryInvalidRequestException or TaskQueryScopeDeniedException or SnapshotConflictException) { current = null; }
        var stale = current is null || current.SnapshotId != snapshot.SnapshotId
            || current.Generation != snapshot.Generation
            || !SameSelection(current.Selection, snapshot.Selection);
        return new(snapshot, stale, current?.SnapshotId, current?.Generation);
    }

    public async Task<GeneratedModelRunResponse> RunAsync(GeneratedModelRequest request, CancellationToken requestCancellation)
    {
        ValidateRequest(request);
        var prior = _store.Read();
        if (!_runSlot.Wait(0, requestCancellation)) throw new GeneratedModelRequestException("run-active", "A generated-model run is already active.", StatusCodes.Status409Conflict);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(requestCancellation, deadline.Token);
        var cancellation = linked;
        if (!_active.TryAdd(request.RunId, cancellation))
        {
            _runSlot.Release();
            throw new GeneratedModelRequestException("run-id-active", "This generated-model run id is already active.", StatusCodes.Status409Conflict);
        }

        var started = DateTimeOffset.UtcNow;
        var provider = request.AuthorizeProvider ? intelligence.Layer.Model?.Id : "deterministic-only";
        var scopeLabel = ScopeLabel(request, request.IncludeSourceSnippets);
        var running = new GeneratedModelRunState(request.RunId, "running", started, null, null, provider, scopeLabel,
            request.SnapshotId, request.Generation, new Dictionary<string, string>(request.Selection, StringComparer.Ordinal));
        try
        {
            _store.Write(prior with { LatestRun = running });
            var result = await BuildAsync(request, provider ?? "none", scopeLabel, linked.Token);
            var finished = running with { Status = result.Snapshot is null ? "partial" : "complete", CompletedAt = DateTimeOffset.UtcNow };
            if (result.Snapshot is { } snapshot)
                _store.Write(new GeneratedModelStoreDocument(snapshot, finished));
            else
                finished = finished with { Error = string.Join("; ", result.Diagnostics.Take(8)) };
            if (result.Snapshot is null)
                _store.Write(new GeneratedModelStoreDocument(prior.LatestSuccessful, finished));
            return new GeneratedModelRunResponse(finished, result.Snapshot);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            var cancelled = running with { Status = "cancelled", CompletedAt = DateTimeOffset.UtcNow, Error = "run-cancelled" };
            _store.Write(new GeneratedModelStoreDocument(prior.LatestSuccessful, cancelled));
            return new GeneratedModelRunResponse(cancelled, null);
        }
        catch (GeneratedModelRequestException error)
        {
            var failed = running with { Status = "failed", CompletedAt = DateTimeOffset.UtcNow, Error = error.Code };
            _store.Write(new GeneratedModelStoreDocument(prior.LatestSuccessful, failed));
            throw;
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException
            or JsonException or FormatException or InterpretationUnavailableException or HttpRequestException
            or TimeoutException or TaskCanceledException or InvalidOperationException or NpgsqlException)
        {
            LogAnalysisFailed(logger, error, request.RunId);
            var failed = running with { Status = "failed", CompletedAt = DateTimeOffset.UtcNow, Error = "analysis-unavailable" };
            _store.Write(new GeneratedModelStoreDocument(prior.LatestSuccessful, failed));
            throw new GeneratedModelRequestException("analysis-unavailable",
                "Generated model analysis failed; the latest successful result was preserved.", StatusCodes.Status503ServiceUnavailable);
        }
        finally
        {
            _active.TryRemove(request.RunId, out _);
            _runSlot.Release();
        }
    }

    private async Task<(GeneratedModelSnapshot? Snapshot, IReadOnlyList<string> Diagnostics)> BuildAsync(
        GeneratedModelRequest request,
        string provider,
        string scopeLabel,
        CancellationToken cancellationToken)
    {
        RequireReadAccess(request.Selection);
        TaskSnapshotScope? scope;
        try
        {
            scope = await source.ResolveAsync(request.Capability, request.Selection, request.SnapshotId, request.Generation, cancellationToken);
        }
        catch (TaskQueryInvalidRequestException error)
        {
            throw new GeneratedModelRequestException("invalid-scope", error.Reason, StatusCodes.Status400BadRequest);
        }
        catch (TaskQueryScopeDeniedException error)
        {
            throw new GeneratedModelRequestException("scope-denied", error.Reason, StatusCodes.Status403Forbidden);
        }
        catch (SnapshotConflictException)
        {
            throw new GeneratedModelRequestException("stale-scope", "The selected published snapshot is no longer available.", StatusCodes.Status409Conflict);
        }
        if (scope is null)
            throw new GeneratedModelRequestException("snapshot-missing", "The selected published snapshot is unavailable.", StatusCodes.Status404NotFound);
        if (!scope.IsCurrent || scope.SnapshotId != request.SnapshotId || scope.Generation != request.Generation)
            throw new GeneratedModelRequestException("stale-scope", "The selected inputs changed; reload the snapshot before running.", StatusCodes.Status409Conflict);
        if (!SameSelection(scope.Selection, request.Selection))
            throw new GeneratedModelRequestException("selection-mismatch", "The resolved snapshot selection does not match the request.", StatusCodes.Status409Conflict);

        var narrowed = source.Narrow(scope, scope.Selection);
        var page = await source.ModelsAsync(narrowed, null, cancellationToken);
        if (page.Heads.Count > GeneratedModelEngine.MaximumProducts || page.Truncated)
            return (null, ["input-limit: selected model products exceed the supported bound"]);

        var products = new List<ModelProduct>(page.Heads.Count);
        foreach (var head in page.Heads.OrderBy(h => h.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!scope.Selection.TryGetValue(head.RepositoryId, out var selectedCommit) || selectedCommit != head.CommitSha)
                throw new GeneratedModelRequestException("selection-mismatch", "A model product is outside the selected revisions.", StatusCodes.Status409Conflict);
            var product = await source.ModelAsync(narrowed, head.Id, cancellationToken);
            if (product is null)
                return (null, [$"missing-input: {head.Id}"]);
            products.Add(product);
        }

        var build = GeneratedModelEngine.Build(products, page.Truncated);
        if (!build.Complete)
            return (null, build.Diagnostics);

        IReadOnlyList<GeneratedModelProposal> aiProposals = [];
        var providerName = "not-used";
        var inputDigest = GeneratedModelEngine.InputDigest(scope.SnapshotId, scope.Generation, scope.Selection,
            build.Inputs, provider, request.IncludeSourceSnippets);
        if (request.AuthorizeProvider)
        {
            var model = intelligence.Layer.Model;
            if (intelligence.Saved?.AllowGeneratedModelAnalysis != true)
                throw new GeneratedModelRequestException("provider-not-authorized", "Enable generated-model provider use in Settings before running.", StatusCodes.Status403Forbidden);
            if (model is null)
                throw new GeneratedModelRequestException("provider-unavailable", "No configured chat provider is available.", StatusCodes.Status409Conflict);
            providerName = model.Id;

            var candidateSummaries = CandidateSummaries(products);
            var excerpts = request.IncludeSourceSnippets
                ? await SourceSnippetsAsync(scope, products, cancellationToken)
                : [];
            var prompt = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = "ce.generated-model-proposal-input.v1",
                scope = new { request.Capability, snapshot = scope.SnapshotId, generation = scope.Generation, selection = scope.Selection },
                candidates = candidateSummaries,
                sourceSnippets = excerpts,
            }, Json);
            if (prompt.Length > MaximumPromptBytes)
                throw new GeneratedModelRequestException("input-limit", "Generated model provider input exceeds its byte limit.", StatusCodes.Status413PayloadTooLarge);
            var hashes = excerpts.Select(item => ModelSource.Digest(Encoding.UTF8.GetBytes(item.Text))).Order(StringComparer.Ordinal);
            inputDigest = ModelSource.Digest(Encoding.UTF8.GetBytes(inputDigest + "\0" + string.Join('\n', hashes)));
            using var schema = JsonDocument.Parse("""
                {"type":"object","additionalProperties":false,"required":["proposals"],"properties":{"proposals":{"type":"array","maxItems":100,"items":{"type":"object","additionalProperties":false,"required":["fromCandidateId","toCandidateId","relation","rationale","evidenceCandidateIds"],"properties":{"fromCandidateId":{"type":"string"},"toCandidateId":{"type":"string"},"relation":{"type":"string","enum":["uses-decision","related-to"]},"rationale":{"type":"string","maxLength":500},"evidenceCandidateIds":{"type":"array","minItems":2,"maxItems":8,"uniqueItems":true,"items":{"type":"string"}}}}}}
                """);
            var response = await model.CompleteJsonAsync(new ModelRequest(
                "Propose only unreviewed, source-evidence-backed relationships between the supplied static candidates. Do not invent business roles, approvals, runtime behavior, execution, or authored-model equivalence. Cite both endpoints and every supporting candidate using only supplied candidate IDs. Return no other JSON members.",
                Encoding.UTF8.GetString(prompt),
                "ce.generated-model-proposals.v1",
                schema.RootElement.Clone(),
                inputDigest), cancellationToken);
            var candidateKinds = candidateSummaries.ToDictionary(candidate => candidate.Id, candidate => candidate.Kind, StringComparer.Ordinal);
            aiProposals = GeneratedModelEngine.ValidateProposals(response.Content, candidateKinds);
        }

        cancellationToken.ThrowIfCancellationRequested();
        TaskSnapshotScope? current;
        try { current = await source.ResolveAsync(request.Capability, request.Selection, null, null, cancellationToken); }
        catch (SnapshotConflictException)
        {
            throw new GeneratedModelRequestException("inputs-changed", "Published inputs changed while analysis was running.", StatusCodes.Status409Conflict);
        }
        if (current is null || current.SnapshotId != scope.SnapshotId || current.Generation != scope.Generation
            || !SameSelection(current.Selection, scope.Selection))
            throw new GeneratedModelRequestException("inputs-changed", "Published inputs changed while analysis was running.", StatusCodes.Status409Conflict);

        var dataScope = ScopeLabel(request, request.IncludeSourceSnippets) + $"; models={build.Inputs.Count}; candidates={CandidateSummariesCount(products)}";
        var snapshot = new GeneratedModelSnapshot(
            GeneratedModelEngine.Schema,
            "complete",
            request.Capability,
            scope.SnapshotId,
            scope.Generation,
            new Dictionary<string, string>(scope.Selection, StringComparer.Ordinal),
            inputDigest,
            build.Inputs,
            build.Diagrams,
            build.DeterministicLinks.Concat(aiProposals).ToArray(),
            build.Diagnostics,
            providerName,
            dataScope,
            request.IncludeSourceSnippets,
            DateTimeOffset.UtcNow,
            "");
        snapshot = snapshot with { ContentHash = GeneratedModelEngine.ContentHash(snapshot) };
        if (JsonSerializer.SerializeToUtf8Bytes(snapshot, Json).Length > GeneratedModelEngine.MaximumOutputBytes)
            throw new GeneratedModelRequestException("output-limit", "Generated model output exceeds its byte limit.", StatusCodes.Status413PayloadTooLarge);
        return (snapshot, build.Diagnostics);
    }

    private async Task<IReadOnlyList<SourceSnippet>> SourceSnippetsAsync(
        TaskSnapshotScope scope,
        IReadOnlyList<ModelProduct> products,
        CancellationToken cancellationToken)
    {
        var saved = intelligence.Saved;
        if (saved is not { AllowGeneratedModelAnalysis: true, AllowSourceSnippets: true })
            throw new GeneratedModelRequestException("source-snippets-not-authorized", "Source excerpts are not authorized in Settings.", StatusCodes.Status403Forbidden);
        if (!access.AllowsOperation(TaskQueryOperations.SourceExcerpt))
            throw new GeneratedModelRequestException("source-excerpts-denied", "Source-excerpt access is not granted by the task-query policy.", StatusCodes.Status403Forbidden);

        var snippets = new List<SourceSnippet>();
        var bytes = 0;
        foreach (var product in products.Where(product => product.Profile == "ce.candidates.v1"))
        {
            var evidence = product.Evidence;
            foreach (var candidate in evidence.GetProperty("candidates").EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceEvidence = candidate.GetProperty("source");
                var path = sourceEvidence.GetProperty("unitPath").GetString()!;
                if (!access.AllowsRepository(product.RepositoryId)
                    || !await source.IsCitedAsync(scope, product.RepositoryId, product.CommitSha, path, cancellationToken))
                    throw new GeneratedModelRequestException("source-excerpt-denied", "A source excerpt is not cited or authorized.", StatusCodes.Status403Forbidden);
                var sourceText = await source.ReadCitedFileAsync(scope, product.RepositoryId, product.CommitSha, path, cancellationToken);
                if (sourceText is null)
                    throw new GeneratedModelRequestException("source-excerpt-missing", "A cited source excerpt is unavailable.", StatusCodes.Status404NotFound);
                var start = sourceEvidence.GetProperty("startLine").GetInt32();
                var end = sourceEvidence.GetProperty("endLine").GetInt32();
                var lines = sourceText.Split('\n');
                if (start < 1 || end < start || end - start > 20 || end > lines.Length)
                    throw new GeneratedModelRequestException("source-excerpt-invalid", "A cited source range is outside its supported bounds.", StatusCodes.Status422UnprocessableEntity);
                var text = string.Join('\n', lines.Skip(start - 1).Take(end - start + 1));
                bytes += Encoding.UTF8.GetByteCount(text);
                if (bytes > MaximumSnippetBytes)
                    throw new GeneratedModelRequestException("source-excerpt-limit", "Source excerpts exceed the aggregate byte limit.", StatusCodes.Status413PayloadTooLarge);
                snippets.Add(new SourceSnippet(
                    GeneratedModelEngine.CandidateId(product.Id, candidate.GetProperty("id").GetString()!),
                    product.RepositoryId,
                    product.CommitSha,
                    path,
                    start,
                    end,
                    text));
            }
        }
        return snippets;
    }

    private static List<ProviderCandidate> CandidateSummaries(IReadOnlyList<ModelProduct> products)
    {
        var candidates = new List<ProviderCandidate>();
        foreach (var product in products.Where(product => product.Profile == "ce.candidates.v1"))
        {
            foreach (var candidate in product.Evidence.GetProperty("candidates").EnumerateArray())
            {
                if (candidates.Count >= GeneratedModelEngine.MaximumCandidates) break;
                var source = candidate.GetProperty("source");
                candidates.Add(new ProviderCandidate(
                    GeneratedModelEngine.CandidateId(product.Id, candidate.GetProperty("id").GetString()!),
                    candidate.GetProperty("kind").GetString()!,
                    product.RepositoryId,
                    product.CommitSha,
                    product.UnitPath,
                    source.GetProperty("startLine").GetInt32(),
                    source.GetProperty("endLine").GetInt32(),
                    candidate.GetProperty("parameters").Clone(),
                    candidate.GetProperty("inputsRead").Clone(),
                    candidate.GetProperty("inputsWritten").Clone(),
                    candidate.GetProperty("body").Clone()));
            }
        }
        return candidates;
    }

    private void RequireReadAccess(IReadOnlyDictionary<string, string> selection)
    {
        if (!access.AllowsOperation(TaskQueryOperations.SnapshotResolve))
            throw new GeneratedModelRequestException("snapshot-resolve-denied", "Generated model analysis is not enabled for snapshot resolution.", StatusCodes.Status403Forbidden);
        if (!access.AllowsOperation(TaskQueryOperations.ModelsList))
            throw new GeneratedModelRequestException("models-read-denied", "Generated model analysis is not enabled for model-list reads.", StatusCodes.Status403Forbidden);
        if (selection.Count is < 1 or > MaximumSelection)
            throw new GeneratedModelRequestException("selection-limit", "Select between 1 and 100 repository revisions.", StatusCodes.Status413PayloadTooLarge);
        if (selection.Any(pair => !access.AllowsRepository(pair.Key)))
            throw new GeneratedModelRequestException("repository-denied", "The task-query policy does not grant every selected repository.", StatusCodes.Status403Forbidden);
    }

    private static void ValidateRequest(GeneratedModelRequest request)
    {
        if (request is null || !Guid.TryParseExact(request.RunId, "D", out _)
            || request.Capability is not { Length: > 0 and <= 100 }
            || !Guid.TryParse(request.SnapshotId, out _)
            || request.Generation < 0
            || request.Selection is null
            || request.Selection.Count is < 1 or > MaximumSelection
            || request.Selection.Any(pair => pair.Key.Length is < 1 or > 100
                || pair.Value.Length != 40
                || pair.Value.Any(character => !char.IsAsciiHexDigit(character))))
            throw new GeneratedModelRequestException("invalid-request", "Generated model request scope or run identity is invalid.", StatusCodes.Status400BadRequest);
        if (request.IncludeSourceSnippets && (!request.AuthorizeProvider || !request.AuthorizeSourceSnippets))
            throw new GeneratedModelRequestException("source-consent-required", "Source excerpts require explicit per-run provider and source-content authorization.", StatusCodes.Status403Forbidden);
    }

    private static string ScopeLabel(GeneratedModelRequest request, bool snippets)
    {
        var repositories = request.Selection.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}@{pair.Value}");
        return $"capability={request.Capability}; snapshot={request.SnapshotId}; generation={request.Generation}; repositories=[{string.Join(",", repositories)}]; sourceSnippets={(snippets ? "included" : "excluded")}";
    }

    private static bool SameSelection(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var commit) && commit == pair.Value);

    private static bool Cancel(CancellationTokenSource cancellation)
    {
        try { cancellation.Cancel(); return true; }
        catch (ObjectDisposedException) { return false; }
    }

    private static int CandidateSummariesCount(IReadOnlyList<ModelProduct> products) =>
        products.Where(product => product.Profile == "ce.candidates.v1")
            .Sum(product => product.Evidence.GetProperty("candidates").GetArrayLength());

    private sealed record ProviderCandidate(
        string Id,
        string Kind,
        string RepositoryId,
        string CommitSha,
        string UnitPath,
        int StartLine,
        int EndLine,
        JsonElement Parameters,
        JsonElement InputsRead,
        JsonElement InputsWritten,
        JsonElement Body);

    private sealed record SourceSnippet(
        string CandidateId,
        string RepositoryId,
        string CommitSha,
        string Path,
        int StartLine,
        int EndLine,
        string Text);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Generated model analysis failed for run {RunId}")]
    private static partial void LogAnalysisFailed(ILogger logger, Exception error, string runId);

    public void Dispose() => _runSlot.Dispose();
}
