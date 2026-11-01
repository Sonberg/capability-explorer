using System.Text.Json;
using CapabilityExplorer.Binding;
using CapabilityExplorer.Contracts;

namespace CapabilityExplorer.Explorer;

public static class BindingPreviewApi
{
    private static readonly JsonSerializerOptions ExportJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static void MapBindingPreviewApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var api = app.MapGroup("/api/binding-preview");
        api.MapGet("", (ExplorerState state, string? cap, string? view) => Respond(() =>
        {
            var (artifacts, bindings) = Selected(state, cap, view);
            return Results.Ok(BindingPreview.Catalog(artifacts, bindings));
        }));
        api.MapPost("", (ExplorerState state, BindingPreviewRequest body, HttpRequest request, string? cap, string? view) => Respond(() =>
        {
            if (!Allowed(request)) return Results.Json(new { error = "Preview requires a local same-origin JSON request." }, statusCode: 403);
            var (artifacts, bindings) = Selected(state, cap, view);
            return Results.Ok(BindingPreview.Preview(artifacts, bindings, body));
        }));
        api.MapPost("/export", (ExplorerState state, BindingPreviewRequest body, HttpRequest request, string? cap, string? view) => Respond(() =>
        {
            if (!Allowed(request)) return Results.Json(new { error = "Export requires a local same-origin JSON request." }, statusCode: 403);
            var (artifacts, bindings) = Selected(state, cap, view);
            var document = BindingPreview.Export(artifacts, bindings, body);
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

    private static (IReadOnlyList<(LoadedFrom, Artifact)> Artifacts, BindingFile Bindings) Selected(ExplorerState state, string? cap, string? view)
    {
        state.Refresh();
        var snapshot = state.Current;
        if (snapshot.Index.Problems.Count > 0) throw new ArgumentException("Resolve artifact or binding-file load problems before previewing mappings.");
        if (!string.IsNullOrEmpty(cap) && snapshot.Capabilities.Get(cap) is null) throw new ArgumentException("Unknown capability.");
        var scope = snapshot.Capabilities.Scope(cap);
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
                if (snapshot.Index.Revisions.Count(r => r.RepositoryId == repo && (r.CommitSha == revision || r.Tags.Contains(revision)
                    || revision.Length >= 7 && r.CommitSha.StartsWith(revision, StringComparison.Ordinal))) != 1)
                    throw new ArgumentException("Unknown or ambiguous revision.");
            }
        }
        var selected = snapshot.Index.View(view, scope);
        return (selected.Artifacts.Select(a => (new LoadedFrom(a.RepositoryId, a.CommitSha, a.Artifact.Header.Unit.Path), a.Artifact)).ToList(), snapshot.Index.Bindings);
    }
}
