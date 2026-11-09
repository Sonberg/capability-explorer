using System.Text.Json;

namespace CapabilityExplorer.Explorer;

/// <summary>The one wire-serialization and deadline policy shared by both adapters (HTTP <see cref="TaskQueryApi"/>,
/// MCP <see cref="TaskQueryMcpHost"/>), so neither can drift from the other: envelope size is capped at the same
/// 1 MiB in both, MCP additionally accounts for the text+structured duplicate when bounding its full result,
/// and both apply the same 10-second per-call deadline linked to the caller's own cancellation.
/// Finding #2/#5 fix: previously only the MCP side had a deadline/gate, and neither adapter actually bounded the
/// serialized envelope size - a long cited source excerpt or evidence list could in principle grow past 1 MiB with
/// no typed response at all (just however large System.Text.Json happened to make it).</summary>
public static class TaskQueryJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public const int MaxEnvelopeBytes = 1024 * 1024;
    public const int McpResponseFramingBytes = 3 * BoundedLineInputStream.DefaultMaxLineBytes + 128;

    /// <summary>Serializes <paramref name="envelope"/> and, if the result would exceed <see cref="MaxEnvelopeBytes"/>,
    /// replaces it with a small fixed <c>limit</c> envelope instead (never a giant, truncated, or partially-written
    /// body): the caller gets a typed, well-formed response either way.</summary>
    public static byte[] SerializeBounded(TaskQueryEnvelope envelope)
    {
        return BoundedJsonResult.TrySerialize(envelope, Options, MaxEnvelopeBytes, out var bytes)
            ? bytes : JsonSerializer.SerializeToUtf8Bytes(LimitEnvelope(envelope), Options);
    }

    /// <summary>The one <c>limit</c> envelope both adapters substitute for an oversized result: a <see cref="BoundedJsonResult"/>
    /// (HTTP) or the full duplicated MCP result each reach this same shape rather than inventing their own oversized-response body.</summary>
    public static TaskQueryEnvelope LimitEnvelope(TaskQueryEnvelope envelope) => new(envelope.Operation, TaskQueryStatus.Limit.Wire(),
        envelope.Mode, envelope.Snapshot, new TaskQueryCoverage([], "not-established"), null, null,
        [new TaskQueryDiagnostic("envelope-too-large", "the result envelope exceeds the one-mebibyte bound and was not returned")],
        "envelope-too-large");

    /// <summary>
    /// MCP exposes the same envelope as both a text block and structured JSON, so the complete tool result can
    /// be almost twice the envelope size. Measure that full result (including JSON escaping of the text copy)
    /// and reserve three times the maximum accepted input-line size for worst-case JSON escaping of a string
    /// response id, plus fixed framing. This keeps the actual stdio response under the same one-mebibyte wire
    /// bound without reducing HTTP's envelope limit.
    /// </summary>
    public static bool McpResultFitsWireBound<T>(T result)
    {
        var resultBytes = JsonSerializer.SerializeToUtf8Bytes(result, Options).Length;
        return resultBytes <= MaxEnvelopeBytes - McpResponseFramingBytes;
    }
}

/// <summary>Up to four concurrent calls (MCP-specific; HTTP relies on Kestrel's own connection limits), each under
/// its own 10-second deadline linked to the caller's own cancellation: one slow call cannot starve the others, and
/// a caller abort never produces a response. <see cref="TaskQueryDeadline.RunAsync"/> carries the shared
/// deadline/cancellation policy both adapters apply identically.</summary>
public static class TaskQueryDeadline
{
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>Runs <paramref name="body"/> under the shared 10-second deadline linked to <paramref name="caller"/>:
    /// returns a <c>cancelled</c> envelope when the deadline (not the caller) elapses, and otherwise rethrows a
    /// caller abort unchanged so neither adapter ever writes a body for it.</summary>
    public static async Task<TaskQueryEnvelope> RunAsync(string operation, string mode,
        Func<CancellationToken, Task<TaskQueryEnvelope>> body, CancellationToken caller)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller);
        deadline.CancelAfter(Budget);
        try
        {
            return await body(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            return Reject(operation, mode, TaskQueryStatus.Cancelled, "time-budget", "task-query call exceeded its ten-second budget");
        }
    }

    public static TaskQueryEnvelope Reject(string operation, string mode, TaskQueryStatus status, string reason, string message) => new(
        operation, status.Wire(), mode, null, new TaskQueryCoverage([], "not-established"), null, null,
        [new TaskQueryDiagnostic(reason, message)], reason);
}
