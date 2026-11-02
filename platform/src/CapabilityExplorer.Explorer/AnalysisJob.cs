using CapabilityExplorer.Capabilities.Analysis;

namespace CapabilityExplorer.Explorer;

/// <summary>Public compatibility projection of the host-local run journal. No engine access belongs here.</summary>
public sealed record AnalysisJob(string State, DateTimeOffset? Since, string? Log,
    string? RunId = null, string? AttemptId = null, string? Stage = null,
    string? Outcome = null, bool Blocked = false, string? BlockReason = null)
{
    public static AnalysisJob Read(FileRunStore store, string capabilityId, string? runId = null)
    {
        var run = runId is null ? store.ReadLatest(capabilityId) : store.ReadRun(runId);
        if (run is not null && run.CapabilityId != capabilityId)
            throw new KeyNotFoundException("No analysis run for this capability.");
        return Project(store, run);
    }

    public static AnalysisJob Project(FileRunStore store, AnalysisRun? run, string? outcome = null)
    {
        var reason = store.ReadBlockReason();
        if (run is null) return new(reason is null ? "idle" : "unknown", null, null,
            Blocked: reason is not null, BlockReason: reason);
        var state = run.State switch { "cancelled" => "stopped", "cancelling" => "stopping", _ => run.State };
        if (run.CancellationRequested && state is "queued" or "running") state = "stopping";
        return new(state, run.AcceptedAt, store.ReadLog(run.RunId), run.RunId, run.AttemptId,
            run.Detail ?? run.State, outcome ?? run.State,
            reason is not null || state is "interrupted" or "unknown" or "partial" or "publishing", reason);
    }

    /// <summary>Reject unsafe or unavailable journal operations without exposing host paths or exception details.</summary>
    public static IResult Respond(Func<IResult> action)
    {
        try { return action(); }
        catch (FormatException) { return Results.BadRequest(new { error = "Invalid analysis identifier." }); }
        catch (ArgumentException) { return Results.BadRequest(new { error = "Invalid analysis identifier or configuration." }); }
        catch (KeyNotFoundException) { return Results.NotFound(new { error = "No analysis run for this capability." }); }
        catch (InvalidOperationException) { return Results.Conflict(new { error = "Analysis is blocked pending host worker reconciliation." }); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or PlatformNotSupportedException)
        {
            return Results.Json(new { error = "Analysis journal is unavailable or requires reconciliation." }, statusCode: 503);
        }
    }
}
