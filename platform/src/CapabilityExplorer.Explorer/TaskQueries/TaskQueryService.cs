using System.Globalization;

namespace CapabilityExplorer.Explorer;

/// <summary>Every field every one of the 6 operations might need; whichever fields an operation does not use are
/// simply ignored by its handler. One shape keeps the HTTP and MCP front ends from drifting into two request models.</summary>
public sealed record TaskQueryRequest(
    IReadOnlyDictionary<string, string>? Selection = null,
    string? Cap = null,
    string? View = null,
    string? SnapshotId = null,
    long? Generation = null,
    string? Cursor = null,
    int? Limit = null,
    string? ModelId = null,
    string? Kind = null,
    string? Key = null,
    string? Repo = null,
    string? Commit = null,
    string? Path = null,
    int? Start = null,
    int? End = null);

/// <summary>One typed service over both evidence sources (files, Postgres): every one of the 6 bounded,
/// read-only task-query operations, each returning the single ce.task-query.v1 envelope. Deny-by-default access
/// is enforced here, before any snapshot, model, evidence or source read.</summary>
public sealed partial class TaskQueryService(ITaskEvidenceSource source, TaskQueryAccessPolicy policy, TaskQueryCursorCodec cursors,
    Microsoft.Extensions.Logging.ILogger<TaskQueryService> logger)
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 50;

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning,
        Message = "task-query {Operation} storage read failed")]
    private static partial void LogStorageUnavailable(Microsoft.Extensions.Logging.ILogger logger, string operation);

    public string Mode => source.Mode;

    public Task<TaskQueryEnvelope> SnapshotResolveAsync(TaskQueryRequest request, CancellationToken ct) =>
        RunAsync(TaskQueryOperations.SnapshotResolve, request, (_, begin, _) =>
            Task.FromResult((begin.Allowed.Count == 0 ? TaskQueryStatus.Denied : TaskQueryStatus.Ok,
                begin.Allowed.Count == 0 ? "no-granted-repositories" : (string?)null, (object?)null, (TaskQueryPage?)null)), ct);

    public Task<TaskQueryEnvelope> ModelsListAsync(TaskQueryRequest request, CancellationToken ct) =>
        RunAsync(TaskQueryOperations.ModelsList, request, async (self, begin, token) =>
        {
            var limit = BoundedLimit(request.Limit);
            var result = await source.ModelsAsync(source.Narrow(begin.Scope, begin.Allowed), request.Kind, token);
            var heads = result.Heads.OrderBy(h => h.Id, StringComparer.Ordinal).ToList();
            var filterDigest = TaskQueryCursorCodec.CanonicalDigest(
                new { request.Kind, limit, Selection = begin.Allowed.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray() });
            var scopeKey = TaskQueryCursorCodec.ScopeDigest(TaskQueryOperations.ModelsList, policy.Digest, filterDigest);
            if (!self.DecodeCursor(begin.Scope.SnapshotId, scopeKey, request.Cursor, out var position, out var cursorError)) return cursorError!.Value;
            var afterId = position?.ElementAtOrDefault(0);
            var window = heads.Where(h => afterId is null || string.CompareOrdinal(h.Id, afterId) > 0).Take(limit + 1).ToList();
            var page = window.Take(limit).ToList();
            var next = window.Count > limit ? cursors.Encode(begin.Scope.SnapshotId, scopeKey, page[^1].Id) : null;
            var items = page.Select(h => new ModelSummary(h.Id, h.Profile == "ce.models.v1" ? "authored" : "candidate",
                h.RepositoryId, h.CommitSha, h.UnitPath, h.ModelKind, h.Coverage, h.DiagnosticCount,
                new ModelSourceStatus(h.SourceStatus, h.SourceSha256))).ToList();
            // A head scan capped at the source's own bound means the true total is unknown, never fabricated.
            object total = result.Truncated ? "unknown" : heads.Count;
            var status = begin.Denied.Count > 0 || result.Truncated ? TaskQueryStatus.Partial : TaskQueryStatus.Ok;
            return (status, (string?)null, (object?)items, new TaskQueryPage(limit, next, total));
        }, ct);

    public Task<TaskQueryEnvelope> DecisionReferencesAsync(TaskQueryRequest request, CancellationToken ct) =>
        RunAsync(TaskQueryOperations.DecisionReferences, request, async (self, begin, token) =>
        {
            var modelId = request.ModelId!;
            // Metadata-only rights resolution BEFORE any product-content read: look the model up by id among the
            // cheap (unnarrowed) heads first, so a denied repository is rejected (403) without ever touching the
            // model's XML/source content - not after a 404-causing full fetch.
            var fullHeads = await source.ModelsAsync(begin.Scope, null, token);
            var targetHead = await source.ModelHeadAsync(begin.Scope, modelId, token);
            if (targetHead is null) return (TaskQueryStatus.Missing, "model-missing", null, null);
            if (begin.Denied.Contains(targetHead.RepositoryId)) return (TaskQueryStatus.Denied, "repository-not-granted", null, null);
            if (fullHeads.Truncated) return (TaskQueryStatus.Partial, "model-coverage-limited", null, null);

            var narrowed = source.Narrow(begin.Scope, begin.Allowed);
            var model = await source.ModelAsync(narrowed, modelId, token);
            if (model is null) return (TaskQueryStatus.Missing, "model-missing", null, null);
            if (model.SourceStatus == "denied") return (TaskQueryStatus.Denied, "source-denied", null, null);
            if (model.SourceStatus != "available" || model.Source is not { } sourceEnvelope)
                return (TaskQueryStatus.Missing, "source-missing", null, null);
            byte[] bytes;
            try { bytes = CapabilityExplorer.Contracts.ModelSource.Decode(sourceEnvelope); }
            catch (Exception e) when (e is FormatException or System.Xml.XmlException) { return (TaskQueryStatus.Unavailable, "source-invalid", null, null); }
            var xml = new System.Text.UTF8Encoding(false, true).GetString(bytes);
            IReadOnlyList<DecisionReferenceResolver.Reference> parsed;
            try { parsed = DecisionReferenceResolver.Read(xml); }
            catch (FormatException) { return (TaskQueryStatus.Invalid, "decision-reference-source-invalid", null, null); }

            var candidates = fullHeads.Heads.Where(h => !begin.Denied.Contains(h.RepositoryId))
                .Select(h => new ModelSummaryLike(h.Id, h.Profile == "ce.models.v1", h.ModelKind, h.RepositoryId, h.CommitSha, h.UnitPath, h.SourceStatus))
                .ToList();
            var items = new List<TaskQueryDecisionReference>();
            foreach (var reference in parsed)
            {
                foreach (var rawRef in reference.References)
                {
                    var (status, target) = DecisionReferenceResolver.Resolve(rawRef, model.UnitPath, model.RepositoryId, model.CommitSha, candidates);
                    items.Add(new TaskQueryDecisionReference(modelId, reference.ElementId, reference.Label, rawRef, status,
                        target is null ? null : new TaskQueryDecisionTarget(target.ModelId, target.DecisionId), "authored-model-reference", false));
                }
            }

            return (fullHeads.Truncated || items.Any(i => i.Status is "missing" or "ambiguous") ? TaskQueryStatus.Partial : TaskQueryStatus.Ok, (string?)null, (object?)items, (TaskQueryPage?)null);
        }, ct);

    public Task<TaskQueryEnvelope> ContractConsumersAsync(TaskQueryRequest request, CancellationToken ct) =>
        RunAsync(TaskQueryOperations.ContractConsumers, request, async (self, begin, token) =>
        {
            var limit = BoundedLimit(request.Limit);
            var integrations = await source.IntegrationsAsync(source.Narrow(begin.Scope, begin.Allowed), token);
            // Consumers are derived ONLY from existing HttpLink candidates for the queried provider endpoint key:
            // Candidates holds the resolved provider target(s); the consumer identity is the calling side, Request.From.
            var rows = integrations.Http.SelectMany(link => link.Candidates.Where(c => c.Key == request.Key).Select(candidate => (link, candidate)))
                .OrderBy(x => x.link.Request.RepositoryId, StringComparer.Ordinal)
                .ThenBy(x => x.link.SourcePath ?? "", StringComparer.Ordinal)
                .ThenBy(x => EdgeId(begin.Scope.SnapshotId, x.link), StringComparer.Ordinal)
                .ToList();
            var filterDigest = TaskQueryCursorCodec.CanonicalDigest(
                new { request.Key, limit, Selection = begin.Allowed.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray() });
            var scopeKey = TaskQueryCursorCodec.ScopeDigest(TaskQueryOperations.ContractConsumers, policy.Digest, filterDigest);
            if (!self.DecodeCursor(begin.Scope.SnapshotId, scopeKey, request.Cursor, out var position, out var cursorError)) return cursorError!.Value;
            var (offset, expectedDigest) = TaskQueryPaging.DecodePosition(position);
            var slice = TaskQueryPaging.Take(rows, offset, expectedDigest, limit,
                row => TaskQueryCursorCodec.CanonicalDigest(new { row.link.Request.RepositoryId, Unit = row.link.SourcePath ?? "", EdgeId = EdgeId(begin.Scope.SnapshotId, row.link) }));
            var items = slice.Items.Select(row =>
            {
                var edgeId = EdgeId(begin.Scope.SnapshotId, row.link);
                var consumer = new TaskQueryConsumer(row.link.Request.From.Key, row.link.Request.From.Name,
                    "symbol", row.link.Request.RepositoryId, row.link.Request.CommitSha);
                var edge = new TaskQueryConsumerEdge(edgeId, row.link.Request.Relation, "candidate", null);
                var connection = new TaskQueryConsumerConnection("evidenced", "not-established",
                    row.link.Match == "binding" ? "evidenced" : "not-established", "no-observation-in-inputs");
                object configuration = row.link.Request.Props is { } props ? props : "not-extracted";
                return new TaskQueryConsumerDto(consumer, edge, connection, configuration, row.link.Request.Evidence.Take(8).ToList(), "candidate");
            }).ToList();
            var next = slice.More ? cursors.Encode(begin.Scope.SnapshotId, scopeKey, slice.NextOffset.ToString(CultureInfo.InvariantCulture), slice.LastDigest) : null;
            return (begin.Denied.Count > 0 ? TaskQueryStatus.Partial : TaskQueryStatus.Ok, (string?)null, (object?)items, new TaskQueryPage(limit, next, rows.Count));
        }, ct);

    private static string EdgeId(string snapshotId, HttpLink link) => TaskQueryCursorCodec.CanonicalDigest(new
    {
        snapshotId, link.Request.RepositoryId, link.Request.CommitSha, link.Request.From.Key, link.Request.Relation, link.SourcePath
    });

    public Task<TaskQueryEnvelope> EvidenceListAsync(TaskQueryRequest request, CancellationToken ct) =>
        RunAsync(TaskQueryOperations.EvidenceList, request, async (self, begin, token) =>
        {
            var limit = BoundedLimit(request.Limit);
            var fetched = await source.EvidenceAsync(source.Narrow(begin.Scope, begin.Allowed), request.Key!, token);
            var all = fetched.Items.Where(e => begin.Allowed.ContainsKey(e.RepositoryId))
                .OrderBy(e => e.Path, StringComparer.Ordinal).ThenBy(e => e.StartLine).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
            var filterDigest = TaskQueryCursorCodec.CanonicalDigest(
                new { request.Key, limit, Selection = begin.Allowed.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray() });
            var scopeKey = TaskQueryCursorCodec.ScopeDigest(TaskQueryOperations.EvidenceList, policy.Digest, filterDigest);
            if (!self.DecodeCursor(begin.Scope.SnapshotId, scopeKey, request.Cursor, out var position, out var cursorError)) return cursorError!.Value;
            var (offset, expectedDigest) = TaskQueryPaging.DecodePosition(position);
            var slice = TaskQueryPaging.Take(all, offset, expectedDigest, limit, e => TaskQueryCursorCodec.CanonicalDigest(e));
            var next = slice.More ? cursors.Encode(begin.Scope.SnapshotId, scopeKey, slice.NextOffset.ToString(CultureInfo.InvariantCulture), slice.LastDigest) : null;
            var status = begin.Denied.Count > 0 || fetched.Truncated || all.Count != fetched.Items.Count ? TaskQueryStatus.Partial : TaskQueryStatus.Ok;
            return (status, (string?)null, (object?)slice.Items, new TaskQueryPage(limit, next, fetched.Truncated ? (object)"unknown" : all.Count));
        }, ct);

    public Task<TaskQueryEnvelope> SourceExcerptAsync(TaskQueryRequest request, CancellationToken ct) =>
        RunAsync(TaskQueryOperations.SourceExcerpt, request, async (_, begin, token) =>
        {
            var repo = request.Repo!;
            var commit = request.Commit!;
            var path = request.Path!;
            if (begin.Denied.Contains(repo)) return (TaskQueryStatus.Denied, "repository-not-granted", null, null);
            if (!begin.Allowed.TryGetValue(repo, out var pinnedCommit) || pinnedCommit != commit)
                return (TaskQueryStatus.Denied, "commit-outside-selection", null, null);
            if (!await source.IsCitedAsync(begin.Scope, repo, commit, path, token))
                return (TaskQueryStatus.Denied, "path-not-cited", null, null);
            string? text;
            try { text = await source.ReadCitedFileAsync(begin.Scope, repo, commit, path, token); }
            catch (GitSourceContentLimitException)
            {
                return (TaskQueryStatus.Limit, "source-content-too-large", null, null);
            }
            if (text is null) return (TaskQueryStatus.Missing, "source-missing", null, null);
            TaskQuerySourceExcerpt excerpt;
            try { excerpt = SourceExcerpt.Build(repo, commit, path, text, request.Start!.Value, request.End!.Value); }
            catch (ArgumentException) { return (TaskQueryStatus.Invalid, "excerpt-out-of-range", null, null); }
            return (TaskQueryStatus.Ok, (string?)null, (object?)excerpt, (TaskQueryPage?)null);
        }, ct);

    private static int BoundedLimit(int? requested) => requested is { } l && l is >= 1 and <= MaxLimit ? l : DefaultLimit;
    private static bool IsCommitSha(string value) =>
        value.Length is 40 or 64 && value.All(c => char.IsAsciiHexDigit(c) && !char.IsAsciiLetterUpper(c));

    /// <summary>Parses a caller-supplied <c>repo:commit[,repo:commit...]</c> view override (same wire shape as
    /// <c>ModelApi.cs</c>'s own, independently reimplemented, never shared): at most 100 pairs, each commit a
    /// full 40-hex sha. Rejects anything else outright rather than guessing intent.</summary>
    private static Dictionary<string, string> ParseView(string? value)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = part.IndexOf(':');
            if (colon < 1 || colon > 100 || !GitSource.IsSafeSegment(part[..colon]) || result.Count >= 100 ||
                !result.TryAdd(part[..colon], part[(colon + 1)..]) || !IsCommitSha(part[(colon + 1)..]))
                throw new ArgumentException("invalid view");
        }

        return result;
    }

    private bool DecodeCursor(string snapshotId, string scopeKey, string? cursor, out string[]? position,
        out (TaskQueryStatus Status, string? Reason, object? Data, TaskQueryPage? Page)? error)
    {
        try
        {
            position = cursors.Decode(snapshotId, scopeKey, cursor);
            error = null;
            return true;
        }
        catch (TaskQueryCursorException e)
        {
            position = null;
            error = (e.Reason.StartsWith("stale", StringComparison.Ordinal) ? TaskQueryStatus.Stale : TaskQueryStatus.Invalid, e.Reason, null, null);
            return false;
        }
    }

    private readonly record struct Begin(TaskSnapshotScope Scope, IReadOnlyDictionary<string, string> Allowed, IReadOnlyList<string> Denied);

    private static string? TargetError(string operation, TaskQueryRequest request)
    {
        if (operation == TaskQueryOperations.DecisionReferences &&
            (request.ModelId is not { Length: 70 } modelId || !modelId.StartsWith("model:", StringComparison.Ordinal) ||
             modelId[6..].Any(c => !char.IsAsciiHexDigit(c) || char.IsAsciiLetterUpper(c))))
            return "invalid-model-id";
        if (operation is TaskQueryOperations.ContractConsumers or TaskQueryOperations.EvidenceList &&
            (string.IsNullOrEmpty(request.Key) || request.Key.Length > 1024)) return "invalid-key";
        if (operation == TaskQueryOperations.SourceExcerpt &&
            (string.IsNullOrEmpty(request.Repo) || request.Repo.Length > 100 || !GitSource.IsSafeSegment(request.Repo) ||
             string.IsNullOrEmpty(request.Commit) || !IsCommitSha(request.Commit) ||
             string.IsNullOrEmpty(request.Path) || request.Path.Length > 4096 || request.Path.StartsWith('/') ||
             request.Path.Contains('\\') || request.Path.Split('/').Any(part => part is "" or "." or "..") ||
             request.Start is null or < 1 || request.End is null || request.End < request.Start ||
             (long)request.End - request.Start + 1 > SourceExcerpt.MaxSpan))
            return "invalid-excerpt-request";
        return null;
    }

    private async Task<TaskQueryEnvelope> RunAsync(string operation, TaskQueryRequest request,
        Func<TaskQueryService, Begin, CancellationToken, Task<(TaskQueryStatus Status, string? Reason, object? Data, TaskQueryPage? Page)>> handle,
        CancellationToken ct)
    {
        if (operation != TaskQueryOperations.SnapshotResolve)
        {
            if (string.IsNullOrEmpty(request.SnapshotId)) return Envelope(operation, TaskQueryStatus.Invalid, "snapshot-required", null, null, null, null);
            if (request.Generation is null) return Envelope(operation, TaskQueryStatus.Invalid, "generation-required", null, null, null, null);
        }
        if (request.SnapshotId is { Length: > 128 })
            return Envelope(operation, TaskQueryStatus.Invalid, "invalid-snapshot-id", null, null, null, null);
        if (request.Selection is { } selected && (selected.Count > 100 ||
            selected.Any(pair => !GitSource.IsSafeSegment(pair.Key) || pair.Key.Length > 100 ||
                !IsCommitSha(pair.Value))))
            return Envelope(operation, TaskQueryStatus.Invalid, "invalid-selection", null, null, null, null);
        if (TargetError(operation, request) is { } targetError)
            return Envelope(operation, TaskQueryStatus.Invalid, targetError, null, null, null, null);
        if (request.Limit is { } limit && limit is < 1 or > MaxLimit) return Envelope(operation, TaskQueryStatus.Invalid, "limit-out-of-bounds", null, null, null, null);
        if (request.Cursor is { Length: > TaskQueryCursorCodec.MaxCursorLength })
            return Envelope(operation, TaskQueryStatus.Invalid, "cursor-too-long", null, null, null, null);
        TaskSnapshotScope? scope;
        IReadOnlyDictionary<string, string> specifiedView;
        var cap = string.IsNullOrEmpty(request.Cap) ? "*" : request.Cap;
        if (cap.Length > 100) return Envelope(operation, TaskQueryStatus.Invalid, "invalid-capability", null, null, null, null);
        try { specifiedView = ParseView(request.View); }
        catch (ArgumentException) { return Envelope(operation, TaskQueryStatus.Invalid, "invalid-view", null, null, null, null); }
        if (!policy.Configured) return Envelope(operation, TaskQueryStatus.Denied, "task-queries-not-configured", null, null, null, null);
        if (!policy.AllowsOperation(operation)) return Envelope(operation, TaskQueryStatus.Denied, "operation-not-granted", null, null, null, null);
        try { scope = await source.ResolveAsync(cap, specifiedView, request.SnapshotId, request.Generation, ct); }
        catch (TaskQueryScopeDeniedException e) { return Envelope(operation, TaskQueryStatus.Denied, e.Reason, null, null, null, null); }
        catch (TaskQueryInvalidRequestException e) { return Envelope(operation, TaskQueryStatus.Invalid, e.Reason, null, null, null, null); }
        catch (ArgumentException) { return Envelope(operation, TaskQueryStatus.Invalid, "invalid-snapshot-id", null, null, null, null); }
        catch (Exception e) when (e is IOException or FormatException or Npgsql.NpgsqlException)
        {
            LogStorageUnavailable(logger, operation);
            return Envelope(operation, TaskQueryStatus.Unavailable, "storage-unavailable", null, null, null, null);
        }
        if (scope is null) return Envelope(operation, TaskQueryStatus.Missing, "snapshot-missing", null, null, null, null);
        if (scope.Selection.Count > 100)
            return Envelope(operation, TaskQueryStatus.Limit, "snapshot-selection-too-large", null, null, null, null);
        if (request.Generation is { } g && g != scope.Generation)
            return Envelope(operation, TaskQueryStatus.Stale, "generation-mismatch", Snapshot(scope),
                Coverage(scope.Selection, [], [], TaskQueryStatus.Stale), null, null);
        if (source.Mode == "files" && request.SnapshotId is not null && !scope.IsCurrent)
            return Envelope(operation, TaskQueryStatus.Stale, "historical-not-retained", Snapshot(scope),
                Coverage(scope.Selection, [], [], TaskQueryStatus.Stale), null, null);

        var (allowed, denied) = policy.Partition(scope.Selection);
        var missing = new List<string>();
        if (request.Selection is { Count: > 0 } requested)
        {
            // A caller may narrow further than the operator grant, but never widen it. A requested repo that is
            // not part of the resolved snapshot's own selection at all is honestly "missing" (it was never
            // resolved), distinct from "denied" (resolved, but the operator grant excludes it).
            foreach (var repo in requested.Keys)
            {
                if (!allowed.ContainsKey(repo) && !denied.Contains(repo) && !scope.Selection.ContainsKey(repo)) missing.Add(repo);
                else if (!allowed.ContainsKey(repo) && !denied.Contains(repo)) denied = [.. denied, repo];
            }

            allowed = allowed.Where(kv => requested.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            if (allowed.Any(pair => requested[pair.Key] != pair.Value))
                return Envelope(operation, TaskQueryStatus.Stale, "selection-revision-mismatch", Snapshot(scope), null, null, null);
        }

        var begin = new Begin(scope, allowed, denied);
        TaskQueryStatus status; string? reason; object? data; TaskQueryPage? page;
        try
        {
            (status, reason, data, page) = await handle(this, begin, ct);
        }
        catch (TaskQueryCursorException e)
        {
            status = e.Reason.StartsWith("stale", StringComparison.Ordinal) ? TaskQueryStatus.Stale : TaskQueryStatus.Invalid;
            reason = e.Reason; data = null; page = null;
        }
        catch (Exception e) when (e is IOException or FormatException or Npgsql.NpgsqlException)
        {
            // Sanitized: never leak exception message/stack/paths into the envelope, only a fixed reason code.
            LogStorageUnavailable(logger, operation);
            status = TaskQueryStatus.Unavailable; reason = "storage-unavailable"; data = null; page = null;
        }

        if ((denied.Count > 0 || missing.Count > 0) && status == TaskQueryStatus.Ok) status = TaskQueryStatus.Partial;
        var requestedMissing = missing.ToDictionary(repo => repo, repo => request.Selection![repo], StringComparer.Ordinal);
        return Envelope(operation, status, reason, Snapshot(scope), Coverage(scope.Selection, denied, missing, status, requestedMissing), page, data);
    }

    private static TaskQuerySnapshotInfo Snapshot(TaskSnapshotScope scope) => new(
        scope.SnapshotId, scope.Generation,
        scope.Selection.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new TaskQuerySnapshotSelection(kv.Key, kv.Value)).ToList(),
        TaskQueryCursorCodec.CanonicalDigest(scope.Selection.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToArray()),
        scope.IsCurrent ? "current" : "superseded", scope.CurrentSnapshotId);

    /// <summary>Per-repository status is <c>denied</c> (resolved into the snapshot but excluded by the operator
    /// grant), <c>missing</c> (requested but never part of the resolved snapshot selection at all), <c>partial</c>
    /// (allowed, but this particular result is not known to be exhaustive - an operation-level truncation, a
    /// partial fetch, or an error already reflected in <paramref name="status"/>), or otherwise <c>full</c>.
    /// Completeness can never claim <c>within-selection-complete</c> when any repo is denied/missing or the
    /// operation's own status is already <c>partial</c> (errors, truncation, incomplete extraction). Finding #6 fix.</summary>
    private static TaskQueryCoverage Coverage(IReadOnlyDictionary<string, string> selection, IReadOnlyList<string> denied,
        List<string> missing, TaskQueryStatus status, Dictionary<string, string>? requestedMissing = null)
    {
        var repositories = selection.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new TaskQueryCoverageRepository(kv.Key, kv.Value,
                denied.Contains(kv.Key) ? "denied" : status == TaskQueryStatus.Ok ? "full" : "partial"))
            .Concat(missing.OrderBy(r => r, StringComparer.Ordinal).Select(r => new TaskQueryCoverageRepository(r, requestedMissing![r], "missing")))
            .ToList();
        var complete = denied.Count == 0 && missing.Count == 0 && status == TaskQueryStatus.Ok;
        return new TaskQueryCoverage(repositories, complete ? "within-selection-complete" : "not-established");
    }

    private TaskQueryEnvelope Envelope(string operation, TaskQueryStatus status, string? reason, TaskQuerySnapshotInfo? snapshot,
        TaskQueryCoverage? coverage, TaskQueryPage? page, object? data)
    {
        IReadOnlyList<TaskQueryDiagnostic> diagnostics = reason is null
            ? Array.Empty<TaskQueryDiagnostic>()
            : new[] { new TaskQueryDiagnostic(reason, $"{status.Wire()}: {reason}") };
        return new TaskQueryEnvelope(operation, status.Wire(), source.Mode, snapshot, coverage ?? new TaskQueryCoverage([], "not-established"),
            page, data, diagnostics, reason);
    }
}
