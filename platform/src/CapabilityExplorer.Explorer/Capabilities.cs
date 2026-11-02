using CapabilityExplorer.Capabilities;
using CapabilityExplorer.Capabilities.Analysis;

namespace CapabilityExplorer.Explorer;

public sealed record RepositoryStatus(
    string Id,
    string Location,
    string Kind,
    string Ref,
    bool Available,
    string? LockedSha,
    string? LockedSubject,
    DateTimeOffset? LockedAt,
    string? Error,
    bool Analysed,
    IReadOnlyList<string> AnalysedRevisions);

public sealed record CapabilityStatus(string Id, string Name, string? Description, DateTimeOffset? CheckedAt, IReadOnlyList<RepositoryStatus> Repositories,
    SourceCheckStatus? Check = null, bool CheckStale = false)
{
    public int Analysed => Repositories.Count(r => r.Analysed);
}

public sealed record CreateCapabilityRequest(string Name, string? Description, string Repositories);

/// <summary>Capabilities as the explorer sees them: definitions, their last check, and which revisions have artifacts.</summary>
public sealed class CapabilityService(CapabilityStore store, ExplorerOptions options, ExplorerIndex index)
{
    public CapabilityStore Store => store;
    public FileRunStore Checks => new(options.RequestsDirectory);

    public IReadOnlyList<CapabilityStatus> List() => store.Capabilities.Select(Status).ToList();

    public CapabilityStatus? Get(string id) => store.Find(id) is { } c ? Status(c) : null;

    public ViewScope? Scope(string? capabilityId)
    {
        if (string.IsNullOrEmpty(capabilityId) || store.Find(capabilityId) is not { } capability)
        {
            return null;
        }

        var preferred = CurrentSnapshot(capabilityId)?.Repositories
            .Where(r => r.CommitSha is not null && index.FindRevision(r.Id, r.CommitSha) is not null)
            .ToDictionary(r => r.Id, r => r.CommitSha!, StringComparer.Ordinal) ?? [];
        return new ViewScope(capability.Id, capability.Repositories.Select(r => r.Id).ToHashSet(StringComparer.Ordinal), preferred);
    }

    public SourceCheckStatus CheckStatus(SourceCheck check, bool duplicate = false) =>
        SourceCheckStatus.Project(check, !Checks.CheckMatches(check, options.CapabilitiesFile), duplicate);

    public SourceCheckAdmission AdmitCheck(string id) => Checks.AdmitCheck(id, options.CapabilitiesFile);

    private CapabilitySnapshot? CurrentSnapshot(string id)
    {
        var check = Checks.ReadLatestCheck(id);
        return check is { State: "done", CancellationRequested: false } && Checks.CheckMatches(check, options.CapabilitiesFile)
            ? check.Snapshot : null;
    }

    private CapabilityStatus Status(CapabilityDefinition c)
    {
        var check = Checks.ReadLatestCheck(c.Id);
        var status = check is null ? null : CheckStatus(check);
        var snapshot = status is { State: "done", Stale: false, CancellationRequested: false } ? check?.Snapshot : null;
        return new CapabilityStatus(c.Id, c.Name, c.Description, snapshot?.CheckedAt, c.Repositories.Select(r =>
        {
            var locked = snapshot?.Repositories.FirstOrDefault(l => l.Id == r.Id);
            var revisions = index.Revisions.Where(x => x.RepositoryId == r.Id).ToList();
            var dir = SourcePathResolver.GitDirectory(r, options.Workspace);
            return new RepositoryStatus(
                r.Id, r.Location, r.Kind == SourceKind.GitHub ? "github" : "local", r.Ref,
                Directory.Exists(dir),
                locked?.CommitSha, locked?.Subject, locked?.CommitTime, locked?.Error,
                locked?.CommitSha is { } sha && revisions.Any(x => x.CommitSha == sha),
                revisions.Select(x => x.Label).ToList());
        }).ToList(), status, status?.Stale ?? false);
    }
}

public sealed record SourceCheckStatus(string RequestId, string CapabilityId, string ConfigurationHash, string State,
    DateTimeOffset AcceptedAt, string? AttemptId, bool CancellationRequested, bool Stale, string StatusUrl, bool Duplicate)
{
    public static SourceCheckStatus Project(SourceCheck check, bool stale, bool duplicate = false) =>
        new(check.RequestId, check.CapabilityId, check.ConfigurationHash, check.State, check.AcceptedAt,
            check.AttemptId, check.CancellationRequested, stale, $"/api/source-checks/{Uri.EscapeDataString(check.RequestId)}", duplicate);
}

public static class SourceCheckApi
{
    public static void MapSourceChecks(this RouteGroupBuilder api)
    {
        api.MapPost("/capabilities/{id}/check", (CapabilityService caps, string id) => Respond(() =>
        {
            if (caps.Store.Find(id) is null) return Results.NotFound(new { error = $"no capability '{id}'" });
            var admission = caps.AdmitCheck(id);
            var check = caps.CheckStatus(admission.Check, !admission.Accepted);
            return Results.Accepted(check.StatusUrl, check);
        }));

        api.MapGet("/source-checks/{requestId}", (CapabilityService caps, string requestId) => Respond(() =>
            caps.Checks.ReadCheck(requestId) is { } check ? Results.Ok(caps.CheckStatus(check))
                : Results.NotFound(new { error = "No source check with this request ID." })));

        api.MapPost("/source-checks/{requestId}/cancel", (CapabilityService caps, string requestId) => Respond(() =>
        {
            var result = caps.Checks.CancelCheck(requestId);
            var check = caps.CheckStatus(result.Check);
            return Results.Ok(new { result.Outcome, check });
        }));
    }

    public static IResult Respond(Func<IResult> action)
    {
        try { return action(); }
        catch (FormatException) { return Results.BadRequest(new { error = "Invalid source-check identifier or configuration." }); }
        catch (ArgumentException) { return Results.BadRequest(new { error = "Invalid source-check identifier or configuration." }); }
        catch (KeyNotFoundException) { return Results.NotFound(new { error = "No source check with this request ID." }); }
        catch (InvalidOperationException) { return Results.Conflict(new { error = "Source checks are blocked pending host worker reconciliation." }); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or PlatformNotSupportedException)
        {
            return Results.Json(new { error = "Source-check journal is unavailable or requires reconciliation." }, statusCode: 503);
        }
    }
}
