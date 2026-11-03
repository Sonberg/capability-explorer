using CapabilityExplorer.Store;
using Npgsql;

namespace CapabilityExplorer.Explorer;

public sealed record GlossaryTermRequest(long ExpectedVersion, string Name, string Definition, string? Group, string Reviewer);
public sealed record GlossaryLinkRequest(long ExpectedVersion, Guid TermId, string OccurrenceId, string Role, string? Note, string Reviewer, GlossaryObservedOccurrence Observed);
public sealed record GlossaryIssueRequest(long ExpectedVersion, Guid TermId, string Kind, string Summary, string? Note, string Status, string Reviewer);
public sealed record GlossaryDeleteRequest(long ExpectedVersion, string Reviewer);

public static class GlossaryReviewApi
{
    public static void MapGlossaryReviewApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/glossary");
        api.MapGet("/review", async (ExplorerState state, GlossaryReviewService service, string? cap, string? view, int? limit, CancellationToken ct) =>
            await Safe(async () =>
            {
                var maximum = limit ?? 1000;
                if (maximum is < 1 or > 1000) throw new ArgumentException("Review limit must be between 1 and 1000.");
                return Results.Ok(await service.ReadAsync(Selected(state.Current, cap, view), maximum, ct));
            }));
        api.MapGet("/observation", (ExplorerState state, string? occurrence, string? cap, string? view) =>
            Safe(() =>
            {
                if (string.IsNullOrWhiteSpace(occurrence) || occurrence.Length > 256) throw new ArgumentException("An exact occurrence ID is required.");
                return Task.FromResult<IResult>(Results.Ok(GlossaryReviewService.Observe(Selected(state.Current, cap, view), occurrence)));
            }));
        api.MapGet("/flow", async (ExplorerState state, GlossaryReviewService service, string? occurrence, string? direction, int? maxHops, int? maxNodes, int? maxEdges, bool? reviewed, string? cap, string? view, CancellationToken ct) =>
            await Safe(async () =>
            {
                if (string.IsNullOrWhiteSpace(occurrence) || occurrence.Length > 8192) throw new ArgumentException("One or more exact occurrence IDs are required (maximum 32).");
                var seeds = occurrence.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                if (seeds.Length is < 1 or > 32) throw new ArgumentException("Select between 1 and 32 occurrences.");
                var selected = Selected(state.Current, cap, view);
                var assertions = new List<GlossaryFlowHumanAssertion>();
                GlossaryReviewView? reviews = null;
                if (reviewed is true)
                {
                    reviews = await service.ReadAsync(selected, 1000, ct);
                    var terms = reviews.Snapshot.Terms.ToDictionary(t => t.Id);
                    var current = reviews.LinkStates.Where(l => l.State == "current").ToDictionary(l => l.Id);
                    foreach (var link in reviews.Snapshot.Links.Where(l => !l.Deleted))
                        if (current.TryGetValue(link.Id, out var linkState) && terms.TryGetValue(link.TermId, out var term) && !term.Deleted)
                            assertions.Add(new(link.Id.ToString(), term.Id.ToString(), term.Name, link.Role, linkState.OccurrenceId!, link.Reviewer, link.Note ?? ""));
                }
                var result = GlossaryFlow.Build(selected, seeds, new(direction ?? "both", maxHops ?? 4, maxNodes ?? 250, maxEdges ?? 500), assertions);
                if (reviews is not null)
                {
                    var reasons = result.Coverage.Reasons.ToList();
                    if (!reviews.Configured) reasons.Add(new("review-disabled", "No review database is configured; only source/binding context is shown."));
                    if (reviews.Snapshot.TermsTruncated || reviews.Snapshot.LinksTruncated || reviews.Snapshot.IssuesTruncated)
                        reasons.Add(new("review-truncated", "Review list limit reached; not all human assertions are included."));
                    if (reviews.LinkStates.Any(s => s.State != "current")) reasons.Add(new("review-not-current", "Stale, missing, ambiguous or excluded reviewed references do not contribute edges."));
                    result = result with { Coverage = result.Coverage with { Complete = result.Coverage.Complete && reasons.Count == result.Coverage.Reasons.Count, Truncated = result.Coverage.Truncated || reasons.Any(r => r.Code == "review-truncated"), Reasons = reasons } };
                }
                return Results.Ok(result);
            }));
        api.MapPut("/terms/{id:guid}", async (Guid id, GlossaryTermRequest body, HttpRequest request, GlossaryReviewService service, CancellationToken ct) =>
            await Write(request, service, async store => Result(await store.SaveTermAsync(new(id, body.Name, body.Definition, body.Group, body.Reviewer), body.ExpectedVersion, ct)), ct));
        api.MapPut("/links/{id:guid}", async (Guid id, GlossaryLinkRequest body, HttpRequest request, GlossaryReviewService service, ExplorerState state, string? cap, string? view, CancellationToken ct) =>
            await Write(request, service, async store =>
            {
                var selected = Selected(state.Current, cap, view);
                var old = await store.GetLinkAsync(id, ct);
                if (old is not null && !selected.Selection.ContainsKey(old.RepositoryId)) return Error(404, "The existing link is outside this scope.");
                var observed = GlossaryReviewService.Observe(selected, body.OccurrenceId);
                if (body.Observed is null) throw new ArgumentException("The reviewer-observed source fingerprint is required.");
                if (body.Observed != observed) return Error(409, "The selected source changed since it was viewed. Reload and review its current provenance before saving.");
                return Result(await store.SaveLinkAsync(new(id, body.TermId, observed.RepositoryId, observed.UnitPath, observed.SymbolKey, body.Role, body.Note, observed, body.Reviewer), body.ExpectedVersion, ct));
            }, ct));
        api.MapPut("/issues/{id:guid}", async (Guid id, GlossaryIssueRequest body, HttpRequest request, GlossaryReviewService service, CancellationToken ct) =>
            await Write(request, service, async store => Result(await store.SaveIssueAsync(new(id, body.TermId, body.Kind, body.Summary, body.Note, body.Status, body.Reviewer), body.ExpectedVersion, ct)), ct));
        api.MapDelete("/terms/{id:guid}", async (Guid id, [Microsoft.AspNetCore.Mvc.FromBody] GlossaryDeleteRequest body, HttpRequest request, GlossaryReviewService service, CancellationToken ct) =>
            await Write(request, service, async store => Result(await store.DeleteTermAsync(id, body.ExpectedVersion, body.Reviewer, ct)), ct));
        api.MapDelete("/links/{id:guid}", async (Guid id, [Microsoft.AspNetCore.Mvc.FromBody] GlossaryDeleteRequest body, HttpRequest request, GlossaryReviewService service, ExplorerState state, string? cap, string? view, CancellationToken ct) =>
            await Write(request, service, async store =>
            {
                var selected = Selected(state.Current, cap, view);
                var old = await store.GetLinkAsync(id, ct);
                if (old is not null && !selected.Selection.ContainsKey(old.RepositoryId)) return Error(404, "The existing link is outside this scope.");
                return Result(await store.DeleteLinkAsync(id, body.ExpectedVersion, body.Reviewer, ct));
            }, ct));
        api.MapDelete("/issues/{id:guid}", async (Guid id, [Microsoft.AspNetCore.Mvc.FromBody] GlossaryDeleteRequest body, HttpRequest request, GlossaryReviewService service, CancellationToken ct) =>
            await Write(request, service, async store => Result(await store.DeleteIssueAsync(id, body.ExpectedVersion, body.Reviewer, ct)), ct));
    }

    internal static ViewIndex Selected(ExplorerSnapshot snapshot, string? cap, string? view)
    {
        var index = snapshot.Index;
        var caps = snapshot.Capabilities;
        if (!string.IsNullOrEmpty(cap) && caps.Get(cap) is null) throw new ArgumentException("Unknown capability.");
        var scope = caps.Scope(cap);
        if (!string.IsNullOrEmpty(view))
        {
            if (view.Length > 8192) throw new ArgumentException("Revision selection is too long.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var part in view.Split(',', StringSplitOptions.TrimEntries))
            {
                var colon = part.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0 || colon == part.Length - 1) throw new ArgumentException("Invalid revision selection; use repo:revision.");
                var repo = part[..colon]; var revision = part[(colon + 1)..];
                if (!seen.Add(repo) || (scope is not null && !scope.Repositories.Contains(repo)))
                    throw new ArgumentException("Duplicate or out-of-scope repository in revision selection.");
                var matches = index.Revisions.Count(r => r.RepositoryId == repo &&
                    (r.CommitSha == revision || r.Tags.Contains(revision) || (revision.Length >= 7 && r.CommitSha.StartsWith(revision, StringComparison.Ordinal))));
                if (matches != 1) throw new ArgumentException("Unknown or ambiguous revision selection.");
            }
        }
        return index.View(view, scope);
    }

    internal static async Task<IResult> Write(HttpRequest request, GlossaryReviewService service, Func<GlossaryStore, Task<IResult>> action, CancellationToken ct)
    {
        if (!GlossaryWriteGuard.Allowed(request)) return Error(403, "Glossary writes require a trusted local same-origin JSON request and X-CE-Glossary-Write: 1.");
        if (!service.Configured) return Error(503, "Glossary review is read-only without CE_DATABASE.");
        return await Safe(async () => await action(await service.StoreAsync(ct)));
    }

    private static IResult Result<T>(GlossaryMutationResult<T> result) => result.Status switch
    {
        GlossaryMutationStatus.NotFound => Error(404, "The record or its active parent was not found."),
        GlossaryMutationStatus.Conflict => Error(409, "The record changed. Reload its current version before reviewing again; your draft has not been saved."),
        _ => Results.Ok(result),
    };

    internal static async Task<IResult> Safe(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ArgumentException e) { return Error(400, e.Message); }
        catch (Exception e) when (e is NpgsqlException or TimeoutException)
        { return Error(503, "The configured glossary database is unavailable or cannot be migrated. No review was saved. Check database configuration and server logs."); }
    }

    private static IResult Error(int status, string message) => Results.Json(new { error = message }, statusCode: status);
}
