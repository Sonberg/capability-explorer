using CapabilityExplorer.Capabilities.Analysis;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class AnalysisRunTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ce-analysis-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _configuration;
    private readonly FileRunStore _store;
    public AnalysisRunTests()
    {
        // macOS exposes /var through a symlink; use its physical temporary directory.
        _directory = Path.GetFullPath(_directory.Replace("/var/", "/private/var/", StringComparison.Ordinal));
        Directory.CreateDirectory(_directory);
        _configuration = Path.Combine(_directory, "capabilities.json");
        File.WriteAllText(_configuration, """{"capabilities":[{"id":"sales","repositories":[]},{"id":"other","repositories":[]}]}""");
        _store = new(Path.Combine(_directory, "requests"));
    }
    private AnalysisRun Admit(string id = "sales") => _store.Admit(id, _configuration, null, new()).Run;
    private static AttemptIdentity Identity(AnalysisRun run) => new(run.RunId, run.AttemptId!);
    private string Resolve(WorkerOwnership owner, AnalysisRun run)
    {
        var hash = _store.BindResolved(owner, Identity(run), new(run.AcceptedInputsHash,
            new Dictionary<string, string> { ["repository.sales"] = new('a', 40), ["runtime"] = "test-runtime" }));
        _store.FinishExecution(owner, Identity(run), 0);
        return hash;
    }

    [Fact]
    public async Task Concurrent_admission_accepts_once_and_preserves_prior_history()
    {
        using var ready = new Barrier(2);
        Task<AdmissionResult> Request() => Task.Run(() => { ready.SignalAndWait(); return new FileRunStore(Path.Combine(_directory, "requests")).Admit("sales", _configuration, null, new()); });
        var requests = new[] { Request(), Request() };
        var results = await Task.WhenAll(requests);
        Assert.Single(results, result => result.Accepted);
        Assert.Equal(results[0].Run.RunId, results[1].Run.RunId);
        _store.Cancel(results[0].Run.RunId);
        Assert.NotEqual(results[0].Run.RunId, Admit().RunId);
        Assert.Equal("cancelled", _store.ReadRun(results[0].Run.RunId).State);
    }
    [Fact]
    public void Worker_lock_refuses_second_owner_and_recovery_blocks_unknown_survivors()
    {
        var accepted = Admit();
        using (var owner = _store.AcquireWorker())
        {
            var claimed = _store.ClaimNext(owner)!;
            Assert.Throws<IOException>(() => new FileRunStore(Path.Combine(_directory, "requests")).AcquireWorker());
            Assert.Equal("running", _store.ReadRun(claimed.RunId).State);
        }
        using var replacement = _store.AcquireWorker();
        var recovery = _store.Recover(replacement);
        Assert.True(recovery.Blocked);
        Assert.Equal("unknown", _store.ReadRun(accepted.RunId).State);
        Assert.Throws<InvalidOperationException>(() => _store.ClaimNext(replacement));
    }
    [Fact]
    public void Queued_cancellation_cannot_be_claimed()
    {
        var accepted = Admit();
        Assert.Equal("accepted", _store.Cancel(accepted.RunId).Outcome);
        using var owner = _store.AcquireWorker();
        Assert.Null(_store.ClaimNext(owner));
        Assert.Equal("already-cancelled", _store.Cancel(accepted.RunId).Outcome);
    }
    [Fact]
    public void Cancellation_wins_before_publication_and_requires_confirmed_termination()
    {
        Admit();
        using var owner = _store.AcquireWorker();
        var run = _store.ClaimNext(owner)!;
        var manifest = Resolve(owner, run);
        _store.Cancel(run.RunId);
        Assert.Throws<InvalidOperationException>(() => _store.BeginPublication(owner, Identity(run), manifest));
        Assert.Equal("cancelling", _store.ReadRun(run.RunId).State);
        Assert.Equal("unknown", _store.FinishExecution(owner, Identity(run), 130, false).State);
    }
    [Fact]
    public void Publication_wins_and_terminal_results_are_monotonic()
    {
        Admit();
        using var owner = _store.AcquireWorker();
        var run = _store.ClaimNext(owner)!;
        var manifest = Resolve(owner, run);
        _store.FinishExecution(owner, Identity(run), 0);
        using (var publication = _store.BeginPublication(owner, Identity(run), manifest))
        {
            Assert.Equal("too-late", _store.Cancel(run.RunId).Outcome);
            publication.Intent("filesystem", "sales/sha");
            publication.Confirm("filesystem", "sales/sha", new('b', 64));
            Assert.Equal("done", publication.Complete().State);
        }
        Assert.Equal("too-late", _store.Cancel(run.RunId).Outcome);
        Assert.Throws<InvalidOperationException>(() => _store.FinishExecution(owner, Identity(run), 1));
        Assert.Equal("done", _store.ReadRun(run.RunId).State);
    }
    [Fact]
    public void Commit_without_terminal_receipt_remains_unknown_with_effect_evidence()
    {
        Admit();
        using var owner = _store.AcquireWorker();
        var run = _store.ClaimNext(owner)!;
        var hash = Resolve(owner, run);
        using (var publication = _store.BeginPublication(owner, Identity(run), hash))
        {
            publication.Intent("filesystem", "sales/sha");
            publication.Confirm("filesystem", "sales/sha", new('b', 64));
            publication.Intent("database", "sales");
        }
        var unknown = _store.ReadRun(run.RunId);
        Assert.Equal("unknown", unknown.State);
        Assert.Collection(unknown.Effects!, effect => Assert.Equal("confirmed", effect.State), effect => Assert.Equal("pending", effect.State));
        Assert.NotNull(_store.ReadBlockReason());
        Assert.Throws<InvalidOperationException>(() => _store.BeginPublication(owner, Identity(run), hash));
    }
    [Fact]
    public void Frozen_configuration_survives_external_mutation_but_rejects_internal_mutation()
    {
        var accepted = Admit();
        File.WriteAllText(_configuration, "{}");
        using var owner = _store.AcquireWorker();
        var run = _store.ClaimNext(owner)!;
        Assert.Contains("sales", File.ReadAllText(run.ConfigurationPath), StringComparison.Ordinal);
        Assert.Equal(accepted.AcceptedInputsHash, run.AcceptedInputsHash);
        File.WriteAllText(run.ConfigurationPath, "{}");
        Assert.Throws<InvalidDataException>(() => Resolve(owner, run));
    }
    [Fact]
    public void Mismatched_attempt_manifest_and_tampered_publication_cannot_complete()
    {
        Admit();
        using var owner = _store.AcquireWorker();
        var run = _store.ClaimNext(owner)!;
        Assert.Throws<InvalidOperationException>(() => _store.BindResolved(owner, new(run.RunId, new('0', 32)), new(run.AcceptedInputsHash, new Dictionary<string, string> { ["sha"] = "x" })));
        var hash = Resolve(owner, run);
        Assert.Throws<InvalidOperationException>(() => _store.BeginPublication(owner, Identity(run), new('0', 64)));
        using (var publication = _store.BeginPublication(owner, Identity(run), hash))
        {
            publication.Intent("filesystem", "sales/sha");
            publication.Confirm("filesystem", "sales/sha", new('b', 64));
            File.WriteAllText(run.ConfigurationPath, "{}");
            Assert.Throws<InvalidDataException>(() => publication.Complete());
        }
        Assert.Equal("unknown", _store.ReadRun(run.RunId).State);
    }
    [Fact]
    public void Legacy_evidence_blocks_without_deletion_and_identifiers_cannot_traverse()
    {
        Admit();
        var legacy = Path.Combine(_directory, "requests", "other.running");
        File.WriteAllText(legacy, "preserve");
        using var owner = _store.AcquireWorker();
        Assert.True(_store.Recover(owner).Blocked);
        Assert.Throws<InvalidOperationException>(() => _store.ClaimNext(owner));
        Assert.Equal("preserve", File.ReadAllText(legacy));
        Assert.Throws<ArgumentException>(() => _store.ReadRun("../other"));
        Assert.Throws<ArgumentException>(() => _store.ReadLatest("../other"));
        Assert.Throws<KeyNotFoundException>(() => _store.ReadRun(new('0', 32)));
    }
    [Fact]
    public void Logs_are_bounded_and_journal_permissions_are_private()
    {
        var run = Admit();
        File.WriteAllText(_store.LogPath(run.RunId), new string('x', 100_000) + "\nlast");
        Assert.EndsWith("last", _store.ReadLog(run.RunId), StringComparison.Ordinal);
        Assert.True(_store.ReadLog(run.RunId)!.Length <= 65536);
        if (OperatingSystem.IsWindows()) return;
        var permissions = File.GetUnixFileMode(Path.Combine(_store.RunDirectory(run.RunId), "run.json"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, permissions);
    }
    [Fact]
    public void Retention_refuses_new_records_without_removing_evidence()
    {
        var bounded = new FileRunStore(Path.Combine(_directory, "requests"), 1);
        var first = bounded.Admit("sales", _configuration, null, new()).Run;
        bounded.Cancel(first.RunId);
        Assert.Throws<InvalidOperationException>(() => bounded.Admit("other", _configuration, null, new()));
        Assert.Equal("cancelled", bounded.ReadRun(first.RunId).State);
    }
    [Fact]
    public void Symlinked_lifecycle_files_are_rejected()
    {
        if (OperatingSystem.IsWindows()) return;
        var run = Admit();
        var alias = Path.Combine(_directory, "alias");
        Directory.CreateSymbolicLink(alias, Path.Combine(_directory, "requests"));
        Assert.Throws<IOException>(() => new FileRunStore(alias));
        File.CreateSymbolicLink(_store.LogPath(run.RunId), _configuration);
        Assert.Throws<IOException>(() => _store.ReadLog(run.RunId));
    }
    public void Dispose() => Directory.Delete(_directory, true);
}
