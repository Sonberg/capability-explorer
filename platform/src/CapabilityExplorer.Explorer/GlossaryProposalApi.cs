using System.Text.Json;
using CapabilityExplorer.Interpretation;
using CapabilityExplorer.Store;

namespace CapabilityExplorer.Explorer;

public sealed record GlossaryProposeRequest(string Candidate);
public sealed record GlossaryProposalLinkSelection(string OccurrenceId, string Role);
public sealed record GlossaryProposalReviewRequest(
    Guid Id, string Candidate, string InputHash, string Decision, bool Reviewed, string Reviewer,
    IReadOnlyList<GlossaryProposalLinkSelection> Links);

public static class GlossaryProposalApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapGlossaryProposalApi(this WebApplication app)
    {
        app.MapPost("/api/glossary/propose", async (
            GlossaryProposeRequest body, ExplorerState state, InterpretationLayer interpretation, HttpRequest request,
            string? cap, string? view, CancellationToken ct) =>
        {
            if (!GlossaryWriteGuard.Allowed(request))
                return Error(403, "Proposal generation requires a trusted local same-origin JSON request and X-CE-Glossary-Write: 1.");
            return await GlossaryReviewApi.Safe(async () =>
            {
                var input = BuildInput(GlossaryReviewApi.Selected(state.Current, cap, view), body.Candidate, cap);
                if (interpretation.Glossary is not { } service)
                    return Error(409, "Glossary proposals are disabled without a configured model. Deterministic candidates remain available.");
                try { return Results.Ok(await service.ProposeAsync(input, ct)); }
                catch (InterpretationUnavailableException e) { return Error(503, e.Message); }
            });
        });
        app.MapPost("/api/glossary/proposals/review", async (
            GlossaryProposalReviewRequest body, ExplorerState state, InterpretationLayer interpretation, HttpRequest request,
            GlossaryReviewService reviews, string? cap, string? view, CancellationToken ct) =>
            await GlossaryReviewApi.Write(request, reviews, async store =>
            {
                if (!body.Reviewed || body.Decision is not ("accepted" or "rejected"))
                    throw new ArgumentException("An explicit reviewed acceptance or rejection is required.");
                if (body.Links is null || body.Links.Count > 200)
                    throw new ArgumentException("A bounded selection of reviewed links is required.");
                var selected = GlossaryReviewApi.Selected(state.Current, cap, view);
                var input = BuildInput(selected, body.Candidate, cap);
                var proposal = interpretation.Glossary?.Cached(input, body.InputHash);
                if (proposal?.Draft is not { } draft)
                    return Error(409, "The proposal is unavailable or its model, scope or source input changed. Propose again and explicitly review the current source.");
                if (body.Decision == "rejected" && body.Links.Count != 0)
                    throw new ArgumentException("Rejection cannot create reviewed links.");
                if (body.Decision == "accepted" && (body.Links.Count == 0
                    || body.Links.Select(l => l?.OccurrenceId).Distinct(StringComparer.Ordinal).Count() != body.Links.Count))
                    throw new ArgumentException("Acceptance requires distinct explicitly reviewed occurrences.");
                var citations = draft.Occurrences.Select(o => o.OccurrenceId).ToHashSet(StringComparer.Ordinal);
                var links = new List<GlossaryLinkWrite>();
                foreach (var link in body.Links)
                {
                    if (link is null || link.OccurrenceId is null || !citations.Contains(link.OccurrenceId))
                        throw new ArgumentException("Reviewed links must refer to the proposal's grounded occurrences.");
                    var observed = GlossaryReviewService.Observe(selected, link.OccurrenceId);
                    links.Add(new(Guid.NewGuid(), body.Id, observed.RepositoryId, observed.UnitPath, observed.SymbolKey,
                        link.Role, $"Reviewed {proposal.PromptVersion} proposal {proposal.InputHash}; model {proposal.Model}.",
                        observed, body.Reviewer));
                }
                var review = new GlossaryProposalReviewWrite(body.Id, proposal.InputHash, body.Decision, body.Reviewer,
                    JsonSerializer.SerializeToElement(new { proposal, scope = input.Scope, links = body.Links, reviewStatus = "reviewed" }, Json));
                var term = body.Decision == "accepted"
                    ? new GlossaryTermWrite(body.Id, draft.CanonicalTerm, draft.Definition, draft.Group, body.Reviewer) : null;
                var result = await store.ReviewProposalAsync(review, term, links, ct);
                return result.Status == GlossaryMutationStatus.Conflict
                    ? Error(409, "This review identifier already records a different decision. Reload reviewed data.")
                    : Results.Ok(result);
            }, ct));
    }

    public static GlossaryInput BuildInput(ViewIndex view, string candidate, string? cap)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 2000)
            throw new ArgumentException("An exact candidate label is required.", nameof(candidate));
        var found = Glossary.Build(view).SingleOrDefault(c => c.Term == candidate)
            ?? throw new ArgumentException("Candidate is not present in the selected capability and revisions.", nameof(candidate));
        if (found.Occurrences.Count > 200)
            throw new ArgumentException("Select a candidate with at most 200 occurrences.", nameof(candidate));
        var sources = found.Occurrences.Select(o =>
        {
            var artifact = view.Artifacts.Single(a => a.RepositoryId == o.RepositoryId && a.CommitSha == o.CommitSha && a.Artifact.Header.Unit.Path == o.UnitPath).Artifact;
            var node = artifact.Nodes.First(n => n.SymbolKey == o.Key);
            var attributes = artifact.Calculations.Where(c => c.SymbolKey == o.Key)
                .OrderBy(c => c.Id, StringComparer.Ordinal).Select(c =>
                    c.Raw.TryGetProperty("attributes", out var attrs) ? attrs.Clone() : (JsonElement?)null)
                .Where(a => a.HasValue).Select(a => a!.Value).ToArray();
            return new GlossarySource(o.Id, o.Name, o.Kind, o.Role, o.Type, o.RepositoryId, o.CommitSha, o.UnitPath, o.Key,
                artifact.ContentSha256, node.Props?.Clone(), attributes, o.Evidence.Select(e => e.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        }).ToArray();
        var scope = JsonSerializer.Serialize(new
        {
            capability = cap ?? "",
            revisions = view.Selection.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => new { repository = s.Key, revision = s.Value }),
        });
        return new(scope, found.Term, sources);
    }

    private static IResult Error(int status, string message) => Results.Json(new { error = message }, statusCode: status);
}
