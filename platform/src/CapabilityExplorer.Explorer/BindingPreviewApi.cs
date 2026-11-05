using System.Text.Json;
using CapabilityExplorer.Binding;
using CapabilityExplorer.Contracts;

namespace CapabilityExplorer.Explorer;

public static class BindingPreviewApi
{
    private static readonly JsonSerializerOptions ExportJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public sealed record RecommendationOwner(string RepositoryId, string CapabilityId, string Name);
    public sealed record RecommendationSnapshot(string Fingerprint, string AlgorithmVersion, string SourceView, string TargetView,
        IReadOnlyList<HttpRecommendationGroup> Groups, IReadOnlyList<RecommendationOwner> Owners);
    private sealed record Selection(IReadOnlyList<(LoadedFrom From, Artifact Artifact)> Sources,
        IReadOnlyList<(LoadedFrom From, Artifact Artifact)> Targets, BindingFile Bindings, string? Context,
        string SourceView, string TargetView, IReadOnlyList<RecommendationOwner> Owners);
    public static void MapBindingPreviewApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var api = app.MapGroup("/api/binding-preview");
        api.MapGet("", (ExplorerState state, string? cap, string? view, string? targetView) => Respond(() =>
        {
            var selected = Selected(state, cap, view, targetView);
            return Results.Ok(BindingPreview.Catalog(selected.Sources, selected.Bindings, selected.Targets, selected.Context));
        }));
        api.MapGet("/recommendations", (ExplorerState state, string? cap, string? view, string? targetView) => Respond(() =>
        {
            var selected = Selected(state, cap, view, targetView, discover: true);
            var catalog = BindingPreview.Catalog(selected.Sources, selected.Bindings, selected.Targets, selected.Context);
            return Results.Ok(new RecommendationSnapshot(catalog.Fingerprint, HttpBindingRecommendations.AlgorithmVersion,
                selected.SourceView, selected.TargetView,
                HttpBindingRecommendations.Build(selected.Sources, selected.Sources.Concat(selected.Targets), selected.Bindings), selected.Owners));
        }));
        api.MapPost("", (ExplorerState state, BindingPreviewRequest body, HttpRequest request, string? cap, string? view, string? targetView) => Respond(() =>
        {
            if (!Allowed(request)) return Results.Json(new { error = "Preview requires a local same-origin JSON request." }, statusCode: 403);
            var selected = Selected(state, cap, view, targetView);
            return Results.Ok(BindingPreview.Preview(selected.Sources, selected.Bindings, body, selected.Targets, selected.Context));
        }));
        api.MapPost("/export", (ExplorerState state, BindingPreviewRequest body, HttpRequest request, string? cap, string? view, string? targetView) => Respond(() =>
        {
            if (!Allowed(request)) return Results.Json(new { error = "Export requires a local same-origin JSON request." }, statusCode: 403);
            var selected = Selected(state, cap, view, targetView);
            var document = BindingPreview.Export(selected.Sources, selected.Bindings, body, selected.Targets, selected.Context);
            return Results.File(JsonSerializer.SerializeToUtf8Bytes(document, ExportJson),
                "application/json", "bindings-reviewed-proposal.json");
        }));
    }

    private static bool Allowed(HttpRequest request) => LocalRequestGuard.Allowed(request)
        && request.HasJsonContentType() && request.Headers["X-CE-Binding-Preview"] == "1";

    private static IResult Respond(Func<IResult> action)
    {
        try { return action(); }
        catch (BindingPreviewStaleException e) { return Results.Json(new { error = e.Message }, statusCode: 409); }
        catch (Exception e) when (e is ArgumentException or FormatException) { return Results.BadRequest(new { error = e.Message }); }
    }

    private static Selection Selected(ExplorerState state, string? cap, string? view, string? targetView, bool discover = false)
    {
        state.Refresh();
        var snapshot = state.Current;
        if (snapshot.Index.Problems.Count > 0) throw new ArgumentException("Resolve artifact or binding-file load problems before previewing mappings.");
        if (!string.IsNullOrEmpty(cap) && snapshot.Capabilities.Get(cap) is null) throw new ArgumentException("Unknown capability.");
        var scope = snapshot.Capabilities.Scope(cap);
        ValidateSelection(snapshot.Index, view, scope);
        var selected = snapshot.Index.View(view, scope);
        var sources = selected.Artifacts.Select(a => (new LoadedFrom(a.RepositoryId, a.CommitSha, a.Artifact.Header.Unit.Path), a.Artifact)).ToList();
        var sourceView = Spec(selected);
        var targets = new List<(LoadedFrom, Artifact)>();
        var resolvedTargetView = "";
        if (targetView is not null || discover)
        {
            if (targetView is not null && string.IsNullOrWhiteSpace(targetView)) throw new ArgumentException("Select destination revisions explicitly.");
            ValidateSelection(snapshot.Index, targetView, null);
            var targetScope = targetView is null ? null : new ViewScope("recommendation-targets",
                targetView.Split(',').Select(part => part.Trim().Split(':')[0]).ToHashSet(StringComparer.Ordinal), new Dictionary<string, string>());
            var targetSelection = snapshot.Index.View(targetView ?? sourceView, targetScope);
            resolvedTargetView = Spec(targetSelection);
            foreach (var artifact in targetSelection.Artifacts)
            {
                var source = sources.FirstOrDefault(item => item.Item1.RepositoryId == artifact.RepositoryId);
                if (source != default && source.Item1.CommitSha != artifact.CommitSha)
                    throw new ArgumentException("Source and destination select different revisions of the same repository.");
                if (!sources.Any(item => item.Item1.RepositoryId == artifact.RepositoryId && item.Item1.UnitPath == artifact.Artifact.Header.Unit.Path))
                    targets.Add((new(artifact.RepositoryId, artifact.CommitSha, artifact.Artifact.Header.Unit.Path), artifact.Artifact));
            }
        }
        var repositories = sources.Concat(targets).Select(item => item.Item1.RepositoryId).ToHashSet(StringComparer.Ordinal);
        var owners = snapshot.Capabilities.List().SelectMany(capability => capability.Repositories
            .Where(repository => repositories.Contains(repository.Id)).Select(repository => new RecommendationOwner(repository.Id, capability.Id, capability.Name)))
            .OrderBy(owner => owner.RepositoryId, StringComparer.Ordinal).ThenBy(owner => owner.CapabilityId, StringComparer.Ordinal).ToList();
        var context = targetView is not null || discover ? JsonSerializer.Serialize(new { Algorithm = HttpBindingRecommendations.AlgorithmVersion, Owners = owners }, ExportJson) : null;
        return new(sources, targets, snapshot.Index.Bindings, context, sourceView, resolvedTargetView, owners);
    }

    private static string Spec(ViewIndex view) => string.Join(',', view.Selection.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}:{pair.Value}"));

    private static void ValidateSelection(ExplorerIndex index, string? view, ViewScope? scope)
    {
        if (!string.IsNullOrEmpty(view))
        {
            if (view.Length > 8192) throw new ArgumentException("Revision selection is too long.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var part in view.Split(',', StringSplitOptions.TrimEntries))
            {
                var colon = part.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0 || colon == part.Length - 1) throw new ArgumentException("Use repo:revision selections.");
                var repo = part[..colon]; var revision = part[(colon + 1)..];
                if (!seen.Add(repo) || scope is not null && !scope.Repositories.Contains(repo)) throw new ArgumentException("Duplicate or out-of-scope repository.");
                if (index.Revisions.Count(r => r.RepositoryId == repo && (r.CommitSha == revision || r.Tags.Contains(revision)
                    || revision.Length >= 7 && r.CommitSha.StartsWith(revision, StringComparison.Ordinal))) != 1)
                    throw new ArgumentException("Unknown or ambiguous revision.");
            }
        }
    }
}
