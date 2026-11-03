using System.Collections.Concurrent;

namespace CapabilityExplorer.Capabilities.Analysis;

public sealed record AnalysisSettings(bool IncludeTests = false, bool Packages = true, bool Locked = false, string? RuntimeIdentity = null,
    string GitHubAuth = "auto", string NugetPassEnv = "", string? NugetAllowedOrigins = null, int ExtractionJobs = 2, int CapabilityWorkers = 2);
/// <summary>Last configured nonsecret worker policy, without any liveness or execution authority.</summary>
public sealed record WorkerDefaults(AnalysisSettings Settings, bool? IncludeTestsOverride);
public sealed record AttemptIdentity(string RunId, string AttemptId);
public sealed record ResolvedManifest(string AcceptedInputsHash, IReadOnlyDictionary<string, string> Inputs);
public sealed record PublicationEffect(string Surface, string Destination, string State, string? EvidenceHash);
public sealed record AnalysisRun(string RunId, string CapabilityId, string State, DateTimeOffset AcceptedAt,
    string AcceptedInputsHash, string ConfigurationPath, string? BindingsPath, AnalysisSettings Settings,
    string? AttemptId = null, string? ManifestHash = null, bool CancellationRequested = false,
    string? Detail = null, IReadOnlyList<PublicationEffect>? Effects = null, bool ExecutionCompleted = false,
    string? EnrichmentOutcome = null, string? EnrichmentDetail = null);
public sealed record AdmissionResult(bool Accepted, AnalysisRun Run);
public sealed record CancelResult(string Outcome, AnalysisRun Run);
public sealed record RecoveryReport(bool Blocked, IReadOnlyList<AnalysisRun> Runs);

public sealed class WorkerOwnership : IDisposable
{
    private readonly FileStream _stream;
    private readonly ConcurrentDictionary<string, byte> _attempts = new(StringComparer.Ordinal);
    internal FileRunStore Store { get; }
    internal bool Disposed { get; private set; }
    internal WorkerOwnership(FileRunStore store, FileStream stream) { Store = store; _stream = stream; }
    internal void Register(AttemptIdentity attempt) => _attempts.TryAdd(Key(attempt), 0);
    internal bool Owns(AnalysisRun run) => run.AttemptId is not null && _attempts.ContainsKey(Key(new(run.RunId, run.AttemptId)));
    internal void Release(AttemptIdentity attempt) => _attempts.TryRemove(Key(attempt), out _);
    internal void ClearAttempts() => _attempts.Clear();
    private static string Key(AttemptIdentity attempt) => attempt.RunId + "/" + attempt.AttemptId;
    public void Dispose() { Disposed = true; _attempts.Clear(); _stream.Dispose(); }
}
