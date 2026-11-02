using CapabilityExplorer.Capabilities.Analysis;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class AnalysisCoordinationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath().Replace("/var/", "/private/var/", StringComparison.Ordinal), "ce-domain-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_directory, "requests");
    private string Marker => Path.Combine(Root, "coordination-domain");
    private readonly string _configuration;
    public AnalysisCoordinationTests()
    {
        Directory.CreateDirectory(_directory);
        _configuration = Path.Combine(_directory, "capabilities.json");
        File.WriteAllText(_configuration, """{"capabilities":[{"id":"sales"}]}""");
    }
    [Fact]
    public void Coordination_domain_is_checked_before_every_access_and_never_overwritten()
    {
        var store = new FileRunStore(Root);
        var run = store.Admit("sales", _configuration, null, new()).Run;
        var record = Path.Combine(store.RunDirectory(run.RunId), "run.json");
        var originalRecord = File.ReadAllText(record);
        File.WriteAllText(Marker, "other-domain");
        Assert.Throws<IOException>(() => new FileRunStore(Root));
        Assert.Throws<IOException>(() => store.ReadLatest("sales"));
        Assert.Throws<IOException>(() => store.ReadRun(run.RunId));
        Assert.Throws<IOException>(() => store.ReadLog(run.RunId));
        Assert.Throws<IOException>(() => store.ReadBlockReason());
        Assert.Throws<IOException>(() => store.AcquireWorker());
        Assert.Throws<IOException>(() => store.Cancel(run.RunId));
        Assert.Throws<IOException>(() => store.Admit("sales", _configuration, null, new()));
        Assert.Equal(originalRecord, File.ReadAllText(record));
        Assert.Equal("other-domain", File.ReadAllText(Marker));
    }
    [Theory]
    [InlineData("")]
    [InlineData("linux:")]
    [InlineData("macos")]
    public void Partial_or_invalid_marker_refuses_before_creating_run_storage(string marker)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Marker, marker);
        Assert.Throws<IOException>(() => new FileRunStore(Root));
        Assert.False(Directory.Exists(Path.Combine(Root, "runs")));
        Assert.Equal(marker, File.ReadAllText(Marker));
    }
    [Fact]
    public void Symlink_marker_is_rejected()
    {
        if (OperatingSystem.IsWindows()) return;
        _ = new FileRunStore(Root);
        var target = Path.Combine(_directory, "target-marker");
        File.Move(Marker, target);
        File.CreateSymbolicLink(Marker, target);
        Assert.Throws<IOException>(() => new FileRunStore(Root));
    }
    [Fact]
    public void Frozen_input_paths_are_relative_on_disk_and_rebound_to_current_root()
    {
        var store = new FileRunStore(Root);
        var run = store.Admit("sales", _configuration, null, new()).Run;
        var record = File.ReadAllText(Path.Combine(store.RunDirectory(run.RunId), "run.json"));
        Assert.Contains("\"configurationPath\": \"capabilities.json\"", record, StringComparison.Ordinal);
        Assert.DoesNotContain(Root, record, StringComparison.Ordinal);
        var relocated = Path.Combine(_directory, "relocated");
        Directory.Move(Root, relocated);
        var moved = new FileRunStore(relocated).ReadRun(run.RunId);
        Assert.Equal(Path.Combine(relocated, "runs", run.RunId, "capabilities.json"), moved.ConfigurationPath);
        Assert.Equal(run.AcceptedInputsHash, moved.AcceptedInputsHash);
        Assert.True(File.Exists(moved.ConfigurationPath));
    }
    [Fact]
    public void Incomplete_admission_returns_sanitized_global_block_without_removing_evidence()
    {
        var store = new FileRunStore(Root);
        var orphan = Path.Combine(Root, "runs", new string('a', 32));
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "capabilities.json"), "interrupted write");
        Assert.Equal("incomplete or malformed lifecycle evidence requires reconciliation", store.ReadBlockReason());
        Assert.True(File.Exists(Path.Combine(orphan, "capabilities.json")));
        Assert.Throws<KeyNotFoundException>(() => store.Admit("sales", _configuration, null, new()));
    }
    [Fact]
    public void Publication_requires_confirmed_execution_and_enrichment_preserves_core_success()
    {
        var store = new FileRunStore(Root);
        store.Admit("sales", _configuration, null, new());
        using var owner = store.AcquireWorker();
        var run = store.ClaimNext(owner)!;
        var attempt = new AttemptIdentity(run.RunId, run.AttemptId!);
        var hash = store.BindResolved(owner, attempt, new(run.AcceptedInputsHash, new Dictionary<string, string> { ["sha"] = new('a', 40) }));
        Assert.Throws<InvalidOperationException>(() => store.BeginPublication(owner, attempt, hash));
        store.FinishExecution(owner, attempt, 0);
        using (var permit = store.BeginPublication(owner, attempt, hash))
        {
            permit.Intent("filesystem", "sales");
            permit.Confirm("filesystem", "sales", new('b', 64));
            permit.Complete();
        }
        Assert.Throws<InvalidOperationException>(() => store.RecordEnrichment(owner, new(run.RunId, new('0', 32)), "failed"));
        var enriched = store.RecordEnrichment(owner, attempt, "failed", "optional model unavailable");
        Assert.Equal("done", enriched.State);
        Assert.Equal("failed", enriched.EnrichmentOutcome);
        Assert.Equal(run.AcceptedInputsHash, enriched.AcceptedInputsHash);
        Assert.Equal(hash, enriched.ManifestHash);
        Assert.Throws<InvalidOperationException>(() => store.RecordEnrichment(owner, attempt, "done"));
    }
    [Fact]
    public void Lifecycle_identifiers_reject_trailing_newline_and_null()
    {
        var store = new FileRunStore(Root);
        Assert.Throws<ArgumentException>(() => store.ReadRun(new string('a', 32) + "\n"));
        Assert.Throws<ArgumentException>(() => store.ReadLatest("sales\n"));
        Assert.Throws<ArgumentNullException>(() => store.ReadRun(null!));
        Assert.Throws<ArgumentNullException>(() => store.ReadLatest(null!));
        Assert.Throws<ArgumentException>(() => store.Admit("sales\n", _configuration, null, new()));
    }
    [Fact]
    public void Publication_permit_rejects_another_run_attempt_or_input_receipt()
    {
        var store = new FileRunStore(Root);
        store.Admit("sales", _configuration, null, new());
        using var owner = store.AcquireWorker();
        var run = store.ClaimNext(owner)!;
        var attempt = new AttemptIdentity(run.RunId, run.AttemptId!);
        var manifest = store.BindResolved(owner, attempt, new(run.AcceptedInputsHash, new Dictionary<string, string> { ["sha"] = new('a', 40) }));
        store.FinishExecution(owner, attempt, 0);
        using (var permit = store.BeginPublication(owner, attempt, manifest))
        {
            permit.RequireIdentity(attempt, run.AcceptedInputsHash, manifest);
            Assert.Throws<InvalidOperationException>(() => permit.RequireIdentity(new(new('0', 32), attempt.AttemptId), run.AcceptedInputsHash, manifest));
            Assert.Throws<InvalidOperationException>(() => permit.RequireIdentity(new(attempt.RunId, new('0', 32)), run.AcceptedInputsHash, manifest));
            Assert.Throws<InvalidOperationException>(() => permit.RequireIdentity(attempt, new('0', 64), manifest));
            Assert.Throws<InvalidOperationException>(() => permit.RequireIdentity(attempt, run.AcceptedInputsHash, new('0', 64)));
            Assert.Empty(store.ReadRun(run.RunId).Effects!);
        }
        Assert.Equal("unknown", store.ReadRun(run.RunId).State);
    }
    public void Dispose() => Directory.Delete(_directory, true);
}
