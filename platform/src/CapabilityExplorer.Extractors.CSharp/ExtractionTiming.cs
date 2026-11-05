using System.Diagnostics;

namespace CapabilityExplorer.Extractors.CSharp;

public sealed record ExtractionTiming(string Stage, int? Unit, double ElapsedMilliseconds, string Outcome);

internal sealed class ExtractionTimer(Action<ExtractionTiming>? report, string stage, int? unit) : IDisposable
{
    private readonly Stopwatch _timer = Stopwatch.StartNew();
    private bool _completed;
    private bool _disposed;

    internal void Complete() => _completed = true;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        report?.Invoke(new(stage,unit,Math.Round(_timer.Elapsed.TotalMilliseconds,3),_completed ? "completed" : "failed"));
    }
}