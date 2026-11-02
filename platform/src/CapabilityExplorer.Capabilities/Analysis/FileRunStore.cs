using System.Text.Json;

namespace CapabilityExplorer.Capabilities.Analysis;

/// <summary>Trusted single-host lifecycle journal. Local filesystems only; unknown execution blocks reuse.</summary>
public sealed partial class FileRunStore
{
    private readonly string _root;
    private readonly string _runs;
    private readonly int _maxRunRecords;
    internal static bool Terminal(AnalysisRun run) => run.State is "done" or "failed" or "cancelled";
    public FileRunStore(string root, int maxRunRecords = 1000)
    {
        if (maxRunRecords is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(maxRunRecords));
        _maxRunRecords = maxRunRecords;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("analysis lifecycle requires Linux or macOS");
        _root = Path.GetFullPath(root);
        AnalysisPaths.DirectoryPrivate(_root);
        CoordinationDomain.Initialize(_root);
        _runs = Path.Combine(_root, "runs");
        AnalysisPaths.DirectoryPrivate(_runs);
    }
    public string RunDirectory(string runId)
    {
        CoordinationDomain.Validate(_root);
        AnalysisPaths.Id(runId);
        var path = Path.Combine(_runs, runId);
        AnalysisPaths.NoLinks(path);
        return path;
    }
    public string LogPath(string runId) => SafePath(runId, "analysis.log");
    public string AttemptDirectory(AttemptIdentity attempt)
    {
        AnalysisPaths.Id(attempt.AttemptId);
        return SafePath(attempt.RunId, "attempt-" + attempt.AttemptId);
    }
    private string SafePath(string id, string name)
    {
        var path = Path.Combine(RunDirectory(id), name);
        AnalysisPaths.NoLinks(path);
        return path;
    }
    private FileStream Gate()
    {
        CoordinationDomain.Validate(_root);
        return AnalysisPaths.Lock(Path.Combine(_root, "metadata.lock"), true);
    }
    internal void Owner(WorkerOwnership owner)
    {
        CoordinationDomain.Validate(_root);
        if (owner.Disposed || !ReferenceEquals(owner.Store, this)) throw new InvalidOperationException("invalid worker ownership");
    }
    public WorkerOwnership AcquireWorker()
    {
        CoordinationDomain.Validate(_root);
        return new(this, AnalysisPaths.Lock(Path.Combine(_root, "worker.lock"), false));
    }
    internal void Save(AnalysisRun run) => AnalysisPaths.Save(SafePath(run.RunId, "run.json"),
        run with { ConfigurationPath = "capabilities.json", BindingsPath = run.BindingsPath is null ? null : "bindings.json" });
    private AnalysisRun Load(string runId)
    {
        var record = SafePath(runId, "run.json");
        if (!File.Exists(record)) throw new KeyNotFoundException("analysis run does not exist");
        var run = AnalysisPaths.Read<AnalysisRun>(record);
        if (run.RunId != runId) throw new InvalidDataException("run identity mismatch");
        AnalysisPaths.Capability(run.CapabilityId);
        if (run.AttemptId is not null) AnalysisPaths.Id(run.AttemptId);
        if (run.State is not ("queued" or "running" or "cancelling" or "publishing" or "done" or "failed" or "cancelled" or "unknown" or "interrupted" or "partial"))
            throw new InvalidDataException("unknown lifecycle state");
        if (run.ConfigurationPath != "capabilities.json" ||
            (run.BindingsPath is not null && run.BindingsPath != "bindings.json"))
            throw new InvalidDataException("untrusted frozen input path");
        if (run.AcceptedInputsHash?.Length != 64 || run.Settings is null) throw new InvalidDataException("invalid accepted inputs");
        return run with { ConfigurationPath = SafePath(runId, "capabilities.json"), BindingsPath = run.BindingsPath is null ? null : SafePath(runId, "bindings.json") };
    }
    private List<AnalysisRun> All()
    {
        CoordinationDomain.Validate(_root);
        return Directory.EnumerateDirectories(_runs).Select(path => Load(Path.GetFileName(path))).ToList();
    }
    public string? ReadBlockReason()
    {
        CoordinationDomain.Validate(_root);
        if (Legacy()) return "legacy lifecycle evidence requires reconciliation";
        try
        {
            return All().Any(run => run.State is "unknown" or "interrupted" or "partial")
                ? "unreconciled execution or publication blocks analysis" : null;
        }
        catch (Exception error) when (error is IOException or KeyNotFoundException or JsonException or ArgumentException)
        { return "incomplete or malformed lifecycle evidence requires reconciliation"; }
    }
    public string? ReadLog(string runId, int maxBytes = 65536, int maxLines = 30)
    {
        if (maxBytes is < 1 or > 65536 || maxLines is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var path = LogPath(runId);
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(Math.Max(0, stream.Length - maxBytes), SeekOrigin.Begin);
        var bytes = new byte[maxBytes];
        var count = stream.Read(bytes, 0, maxBytes);
        var text = System.Text.Encoding.UTF8.GetString(bytes, 0, count);
        return string.Join('\n', text.Split('\n').TakeLast(maxLines));
    }
    public AnalysisRun ReadRun(string runId) => Load(runId);
    public AnalysisRun? ReadLatest(string capId)
    {
        AnalysisPaths.Capability(capId);
        return All().Where(run => run.CapabilityId == capId).OrderByDescending(run => run.AcceptedAt).FirstOrDefault();
    }
    public AdmissionResult Admit(string capId, string configurationPath, string? bindingsPath, AnalysisSettings settings)
    {
        AnalysisPaths.Capability(capId);
        ArgumentNullException.ThrowIfNull(settings);
        using var gate = Gate();
        var records = All();
        var existing = records.Where(run => run.CapabilityId == capId && !Terminal(run)).OrderByDescending(run => run.AcceptedAt).FirstOrDefault();
        if (existing is not null) return new(false, existing);
        if (records.Count >= _maxRunRecords) throw new InvalidOperationException("lifecycle retention limit reached; archive verified terminal history before admission");
        var configuration = CaptureConfiguration(configurationPath);
        using (var parsed = JsonDocument.Parse(configuration))
        {
            if (!parsed.RootElement.TryGetProperty("capabilities", out var capabilities) ||
                !capabilities.EnumerateArray().Any(cap => cap.GetProperty("id").GetString() == capId))
                throw new InvalidDataException("capability is absent from accepted configuration");
        }
        var bindings = bindingsPath is null ? null : AnalysisPaths.ReadBounded(Path.GetFullPath(bindingsPath));
        var id = Guid.NewGuid().ToString("N");
        AnalysisPaths.DirectoryPrivate(RunDirectory(id));
        var configCopy = SafePath(id, "capabilities.json");
        var bindingCopy = bindings is null ? null : SafePath(id, "bindings.json");
        AnalysisPaths.Atomic(configCopy, configuration);
        if (bindingCopy is not null) AnalysisPaths.Atomic(bindingCopy, bindings!);
        var accepted = new { configuration = AnalysisPaths.Hash(configuration), bindings = bindings is null ? null : AnalysisPaths.Hash(bindings), settings };
        var hash = AnalysisPaths.Hash(JsonSerializer.Serialize(accepted, AnalysisPaths.Json));
        AnalysisPaths.Save(SafePath(id, "accepted.json"), accepted);
        var run = new AnalysisRun(id, capId, "queued", DateTimeOffset.UtcNow, hash, configCopy, bindingCopy, settings);
        Save(run);
        return new(true, run);
    }
    public CancelResult Cancel(string runId)
    {
        var observed = Load(runId);
        if (observed.State is "publishing" or "done") return new("too-late", observed);
        using var gate = Gate();
        var run = Load(runId);
        if (run.State == "done" || run.State == "publishing") return new("too-late", run);
        if (Terminal(run)) return new(run.State == "cancelled" ? "already-cancelled" : "terminal", run);
        run = run with { CancellationRequested = true, State = run.State == "queued" ? "cancelled" : run.State == "running" ? "cancelling" : run.State };
        Save(run);
        return new("accepted", run);
    }
    private bool Legacy()
    {
        CoordinationDomain.Validate(_root);
        return Directory.EnumerateFiles(_root).Any(path => Path.GetExtension(path) is ".request" or ".running" or ".cancel");
    }
    public RecoveryReport Recover(WorkerOwnership owner)
    {
        Owner(owner);
        using var gate = Gate();
        var runs = All();
        for (var index = 0; index < runs.Count; index++)
        {
            if (runs[index].State is not ("running" or "cancelling" or "publishing")) continue;
            runs[index] = runs[index] with { State = "unknown", Detail = "prior execution/publication requires positive reconciliation; no automatic retry" };
            Save(runs[index]);
        }
        return new(Legacy() || runs.Any(run => !Terminal(run) && run.State != "queued"), runs);
    }
    public AnalysisRun? ClaimNext(WorkerOwnership owner, string? runId = null)
    {
        Owner(owner);
        using var gate = Gate();
        var all = All();
        if (Legacy() || all.Any(run => !Terminal(run) && run.State != "queued"))
            throw new InvalidOperationException("unreconciled execution or legacy evidence blocks execution");
        if (runId is not null) AnalysisPaths.Id(runId);
        var next = all.Where(run => run.State == "queued" && (runId is null || run.RunId == runId)).OrderBy(run => run.AcceptedAt).ThenBy(run => run.RunId, StringComparer.Ordinal).FirstOrDefault();
        if (next is null) return null;
        VerifyAccepted(next);
        var claimed = next with { State = "running", AttemptId = Guid.NewGuid().ToString("N") };
        AnalysisPaths.DirectoryPrivate(AttemptDirectory(new(claimed.RunId, claimed.AttemptId!)));
        Save(claimed);
        return claimed;
    }
    internal void VerifyAccepted(AnalysisRun run)
    {
        var acceptedBytes = AnalysisPaths.ReadBounded(SafePath(run.RunId, "accepted.json"));
        using var document = JsonDocument.Parse(acceptedBytes);
        var accepted = document.RootElement;
        if ((run.BindingsPath is null) != (accepted.GetProperty("bindings").ValueKind == JsonValueKind.Null) ||
            AnalysisPaths.Hash(CaptureConfiguration(run.ConfigurationPath)) != accepted.GetProperty("configuration").GetString() ||
            (run.BindingsPath is not null && AnalysisPaths.Hash(AnalysisPaths.ReadBounded(run.BindingsPath)) != accepted.GetProperty("bindings").GetString()) ||
            AnalysisPaths.Hash(JsonSerializer.Serialize(accepted, AnalysisPaths.Json)) != run.AcceptedInputsHash ||
            JsonSerializer.Serialize(run.Settings, AnalysisPaths.Json) != JsonSerializer.Serialize(accepted.GetProperty("settings"), AnalysisPaths.Json))
            throw new InvalidDataException("accepted inputs changed");
    }
    internal AnalysisRun Attempt(AttemptIdentity attempt)
    {
        AnalysisPaths.Id(attempt.AttemptId);
        var run = Load(attempt.RunId);
        if (run.AttemptId != attempt.AttemptId || Terminal(run) || run.State is "unknown" or "interrupted" or "partial")
            throw new InvalidOperationException("stale, terminal or unreconciled attempt");
        return run;
    }
    public string BindResolved(WorkerOwnership owner, AttemptIdentity attempt, ResolvedManifest manifest)
    {
        Owner(owner);
        using var gate = Gate();
        var run = Attempt(attempt);
        VerifyAccepted(run);
        if (run.State != "running" || run.ManifestHash is not null || manifest.AcceptedInputsHash != run.AcceptedInputsHash || manifest.Inputs is null || manifest.Inputs.Count == 0)
            throw new InvalidOperationException("invalid resolved-input binding");
        var ordered = new ResolvedManifest(manifest.AcceptedInputsHash, new SortedDictionary<string, string>(manifest.Inputs.ToDictionary(pair => pair.Key, pair => pair.Value), StringComparer.Ordinal));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(ordered, AnalysisPaths.Json);
        var hash = AnalysisPaths.Hash(bytes);
        AnalysisPaths.Atomic(SafePath(run.RunId, "resolved.json"), bytes);
        Save(run with { ManifestHash = hash });
        return hash;
    }
    public AnalysisRun FinishExecution(WorkerOwnership owner, AttemptIdentity attempt, int exitCode, bool terminationConfirmed = true)
    {
        Owner(owner);
        using var gate = Gate();
        var run = Attempt(attempt);
        if (run.State is not ("running" or "cancelling")) throw new InvalidOperationException("execution cannot finish in this state");
        var state = !terminationConfirmed ? "unknown" : run.CancellationRequested ? "cancelled" : exitCode != 0 ? "failed" : "running";
        run = run with { State = state, ExecutionCompleted = terminationConfirmed && exitCode == 0 && !run.CancellationRequested, Detail = state == "running" ? "execution completed; publication pending" : $"execution exit {exitCode}; termination confirmed: {terminationConfirmed}" };
        Save(run);
        return run;
    }
    public AnalysisRun RecordEnrichment(WorkerOwnership owner, AttemptIdentity attempt, string outcome, string? detail = null)
    {
        Owner(owner);
        AnalysisPaths.Id(attempt.AttemptId);
        if (outcome is not ("done" or "failed" or "not-configured")) throw new ArgumentException("invalid enrichment outcome", nameof(outcome));
        if (detail?.Length > 4096) throw new ArgumentException("enrichment detail exceeds bounds", nameof(detail));
        using var gate = Gate();
        var run = Load(attempt.RunId);
        if (run.State != "done" || run.AttemptId != attempt.AttemptId || run.EnrichmentOutcome is not null)
            throw new InvalidOperationException("enrichment requires the current completed attempt and cannot overwrite an outcome");
        VerifyAccepted(run);
        if (run.ManifestHash is null || AnalysisPaths.Hash(AnalysisPaths.ReadBounded(SafePath(run.RunId, "resolved.json"))) != run.ManifestHash)
            throw new InvalidDataException("enrichment input identity mismatch");
        run = run with { EnrichmentOutcome = outcome, EnrichmentDetail = detail };
        Save(run);
        return run;
    }
    public PublicationPermit BeginPublication(WorkerOwnership owner, AttemptIdentity attempt, string manifestHash)
    {
        Owner(owner);
        var gate = Gate();
        try
        {
            var run = Attempt(attempt);
            VerifyAccepted(run);
            if (run.State != "running" || !run.ExecutionCompleted || run.CancellationRequested || run.ManifestHash is null || manifestHash != run.ManifestHash ||
                AnalysisPaths.Hash(AnalysisPaths.ReadBounded(SafePath(run.RunId, "resolved.json"))) != manifestHash)
                throw new InvalidOperationException("publication rejected: cancellation or immutable-input mismatch");
            run = run with { State = "publishing", Effects = [] };
            Save(run);
            return new(this, owner, run, gate);
        }
        catch { gate.Dispose(); throw; }
    }
}
