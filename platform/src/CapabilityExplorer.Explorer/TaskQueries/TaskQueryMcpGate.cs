namespace CapabilityExplorer.Explorer;

/// <summary>At most 4 concurrent task-query calls over stdio; no queue. A 5th caller is told immediately, never
/// held. Each admitted call gets its own 10-second budget linked to the caller's own cancellation, so one slow
/// call cannot starve the others and a caller abort never produces a response.</summary>
public sealed class TaskQueryMcpGate : IDisposable
{
    public const int MaxConcurrent = 4;

    private readonly SemaphoreSlim _gate = new(MaxConcurrent, MaxConcurrent);
    private bool _disposed;

    /// <summary>Runs <paramref name="body"/> under the gate and the 10-second deadline. Returns a
    /// <c>limit/concurrency-exceeded</c> envelope immediately when the gate is full, a <c>cancelled</c> envelope
    /// when the service's own 10-second deadline (not the caller) elapses, and otherwise rethrows a caller abort
    /// so the stdio host sends no response at all.</summary>
    public async Task<TaskQueryEnvelope> RunAsync(string operation, string mode,
        Func<CancellationToken, Task<TaskQueryEnvelope>> body, CancellationToken caller)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _gate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
        {
            return Reject(operation, mode, TaskQueryStatus.Limit, "concurrency-exceeded",
                "no more than four concurrent task-query calls are served at once");
        }

        try
        {
            return await TaskQueryDeadline.RunAsync(operation, mode, body, caller).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static TaskQueryEnvelope Reject(string operation, string mode, TaskQueryStatus status, string reason, string message) =>
        TaskQueryDeadline.Reject(operation, mode, status, reason, message);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }
}
