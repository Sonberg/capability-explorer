using System.Diagnostics;
using System.Text.Json;

namespace CapabilityExplorer.Cli;

internal sealed class AnalysisTelemetryStage(Action<string> write, string runId, string attemptId,
    string stage, string? repositoryId, string? language) : IDisposable
{
    private readonly Stopwatch _timer = Stopwatch.StartNew();
    private string _outcome = "failed";
    private int? _downloaded;
    private int? _alreadyPresent;
    private int? _installed;
    private int? _unavailable;
    private IReadOnlyList<string>? _failureCategories;
    private bool _disposed;

    internal void Complete() => _outcome = "completed";

    internal void Packages(int? downloaded = null, int? alreadyPresent = null, int? installed = null,
        int? unavailable = null, IReadOnlyList<string>? failureCategories = null)
    {
        _downloaded = downloaded;
        _alreadyPresent = alreadyPresent;
        _installed = installed;
        _unavailable = unavailable;
        _failureCategories = failureCategories;
        _outcome = unavailable > 0 ? "partial" : "completed";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        write("-- telemetry: " + JsonSerializer.Serialize(new
        {
            schema = "ce.analysis.telemetry.v1", runId, attemptId, stage, repositoryId, language,
            elapsedMilliseconds = Math.Round(_timer.Elapsed.TotalMilliseconds, 3), outcome = _outcome,
            downloaded = _downloaded, alreadyPresent = _alreadyPresent, installed = _installed,
            unavailable = _unavailable, failureCategories = _failureCategories
        }) + "\n");
    }
}