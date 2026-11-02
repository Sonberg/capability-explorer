using System.Text.Json;
using CapabilityExplorer.Capabilities;
using CapabilityExplorer.Capabilities.Analysis;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class SourceCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Environment.CurrentDirectory, ".sandbox-cache", "check-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FileRunStore _store;
    private readonly string _configuration;
    public SourceCheckTests()
    {
        Directory.CreateDirectory(_root);
        _configuration = Path.Combine(_root, "capabilities.json");
        File.WriteAllText(_configuration, """{"capabilities":[{"id":"sales","name":"Sales","repositories":[{"id":"repo","location":"/fixture","kind":"local","ref":"HEAD"}]}]}""");
        _store = new(Path.Combine(_root, "requests"));
    }
    private SourceCheck Admit() => _store.AdmitCheck("sales", _configuration).Check;
    private static AttemptIdentity Identity(SourceCheck check) => new(check.RequestId, check.AttemptId!);
    private static CapabilitySnapshot Snapshot(string id = "repo", string reference = "HEAD", string? sha = null) =>
        new("sales", DateTimeOffset.UtcNow, [new(id, reference, sha ?? new string('a', 40), "untrusted subject", null, null)]);

    [Fact]
    public async Task Concurrent_duplicate_admission_is_atomic_and_terminal_history_is_retained()
    {
        using var barrier = new Barrier(2);
        Task<SourceCheckAdmission> Request() => Task.Run(() =>
        {
            barrier.SignalAndWait();
            return new FileRunStore(Path.Combine(_root, "requests")).AdmitCheck("sales", _configuration);
        });
        var admissions = await Task.WhenAll(Request(), Request());
        Assert.Single(admissions, admission => admission.Accepted);
        Assert.Equal(admissions[0].Check.RequestId, admissions[1].Check.RequestId);
        Assert.Equal("accepted", _store.CancelCheck(admissions[0].Check.RequestId).Outcome);
        var next = Admit();
        Assert.NotEqual(admissions[0].Check.RequestId, next.RequestId);
        Assert.Equal(next.RequestId, _store.ReadLatestCheck("sales")!.RequestId);
        Assert.Equal("cancelled", _store.ReadCheck(admissions[0].Check.RequestId).State);
    }

    [Fact]
    public void Checks_and_analysis_share_owner_exclusion_and_uncertain_recovery()
    {
        var check = Admit();
        _store.Admit("sales", _configuration, null, new());
        using (var owner = _store.AcquireWorker())
        {
            var claimed = _store.ClaimCheck(owner)!;
            Assert.NotEqual(claimed.RequestId, claimed.AttemptId);
            Assert.Throws<IOException>(() => new FileRunStore(Path.Combine(_root, "requests")).AcquireWorker());
            Assert.Throws<InvalidOperationException>(() => _store.ClaimNext(owner));
        }
        using var replacement = _store.AcquireWorker();
        Assert.True(_store.Recover(replacement).Blocked);
        Assert.Equal("unknown", _store.ReadCheck(check.RequestId).State);
        Assert.NotNull(_store.ReadBlockReason());
        Assert.Throws<InvalidOperationException>(() => _store.ClaimCheck(replacement));
        Assert.Throws<InvalidOperationException>(() => _store.ClaimNext(replacement));
    }

    [Fact]
    public void Analysis_uncertainty_also_blocks_checks()
    {
        Admit();
        _store.Admit("sales", _configuration, null, new());
        using (var owner = _store.AcquireWorker())
        {
            Assert.NotNull(_store.ClaimNext(owner));
            Assert.Throws<InvalidOperationException>(() => _store.ClaimCheck(owner));
        }
        using var replacement = _store.AcquireWorker();
        Assert.True(_store.Recover(replacement).Blocked);
        Assert.Throws<InvalidOperationException>(() => _store.ClaimCheck(replacement));
    }

    [Fact]
    public void Cancellation_is_intent_and_no_late_results_or_wrong_attempts_can_publish()
    {
        Admit();
        using var owner = _store.AcquireWorker();
        var check = _store.ClaimCheck(owner)!;
        Assert.Throws<InvalidOperationException>(() => _store.CompleteCheck(owner, new(check.RequestId, Guid.NewGuid().ToString("N")), Snapshot()));
        Assert.Equal("accepted", _store.CancelCheck(check.RequestId).Outcome);
        Assert.Equal("cancelling", _store.ReadCheck(check.RequestId).State);
        Assert.Throws<InvalidOperationException>(() => _store.CompleteCheck(owner, Identity(check), Snapshot()));
        Assert.Equal("unknown", _store.FailCheck(owner, Identity(check), true).State);
        Assert.Throws<InvalidOperationException>(() => _store.FailCheck(owner, Identity(check), true));
    }

    [Fact]
    public void Completion_is_monotonic_and_sanitized_and_queued_cancel_is_not_claimed()
    {
        var first = Admit();
        _store.CancelCheck(first.RequestId);
        Assert.Equal("already-cancelled", _store.CancelCheck(first.RequestId).Outcome);
        using var owner = _store.AcquireWorker();
        Assert.Null(_store.ClaimCheck(owner));
        Admit();
        var check = _store.ClaimCheck(owner)!;
        var completed = _store.CompleteCheck(owner, Identity(check), Snapshot());
        Assert.Null(completed.Snapshot!.Repositories[0].Subject);
        Assert.Equal("too-late", _store.CancelCheck(check.RequestId).Outcome);
        Assert.Throws<InvalidOperationException>(() => _store.CompleteCheck(owner, Identity(check), Snapshot()));
    }

    [Theory]
    [InlineData("other", "HEAD", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("repo", "main", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("repo", "HEAD", "not-a-sha")]
    public void Completion_rejects_mismatched_repository_ref_or_revision(string id, string reference, string sha)
    {
        Admit();
        using var owner = _store.AcquireWorker();
        var check = _store.ClaimCheck(owner)!;
        Assert.Throws<InvalidDataException>(() => _store.CompleteCheck(owner, Identity(check), Snapshot(id, reference, sha)));
        Assert.Equal("working", _store.ReadCheck(check.RequestId).State);
    }

    [Fact]
    public void Accepted_inputs_are_frozen_and_configuration_auth_policy_and_integrity_are_bound()
    {
        var check = Admit();
        Assert.True(_store.CheckMatches(check, _configuration));
        File.WriteAllText(_configuration, """{"capabilities":[{"id":"sales","name":"Changed","repositories":[]}]}""");
        Assert.False(_store.CheckMatches(check, _configuration));
        Assert.Contains("/fixture", File.ReadAllText(check.ConfigurationPath), StringComparison.Ordinal);
        using var owner = _store.AcquireWorker();
        _store.PublishWorkerDefaults(owner, new(GitHubAuth: "none"));
        Assert.False(_store.CheckMatches(check, check.ConfigurationPath));
        File.AppendAllText(check.ConfigurationPath, " ");
        Assert.Throws<InvalidDataException>(() => _store.ClaimCheck(owner));
    }

    [Fact]
    public void Path_ids_links_malformed_records_and_retention_fail_closed()
    {
        Assert.Throws<ArgumentException>(() => _store.ReadCheck("../escape"));
        Assert.Throws<ArgumentException>(() => _store.AdmitCheck("../escape", _configuration));
        var check = Admit();
        _store.CancelCheck(check.RequestId);
        Assert.Throws<InvalidOperationException>(() => new FileRunStore(Path.Combine(_root, "requests"), 1).AdmitCheck("sales", _configuration));
        var record = Path.Combine(_root, "requests/checks", check.RequestId, "check.json");
        File.WriteAllText(record, JsonSerializer.Serialize(check with { RequestId = Guid.NewGuid().ToString("N") }));
        Assert.Throws<InvalidDataException>(() => _store.ReadCheck(check.RequestId));
        Assert.NotNull(_store.ReadBlockReason());
    }

    [Fact]
    public void Linked_or_oversized_check_records_and_frozen_local_override_are_rejected()
    {
        var check = Admit();
        using var owner = _store.AcquireWorker();
        var local = Path.ChangeExtension(check.ConfigurationPath, ".local.json");
        File.WriteAllText(local, """{"capabilities":[{"id":"sales","repositories":[]}]}""");
        Assert.Throws<InvalidDataException>(() => _store.ClaimCheck(owner));
        File.Delete(local);
        var record = Path.Combine(_root, "requests/checks", check.RequestId, "check.json");
        var contents = File.ReadAllText(record);
        File.Delete(record);
        File.CreateSymbolicLink(record, _configuration);
        Assert.Throws<IOException>(() => _store.ReadCheck(check.RequestId));
        File.Delete(record);
        File.WriteAllText(record, contents + new string(' ', 1024 * 1024));
        Assert.Throws<InvalidDataException>(() => _store.ReadCheck(check.RequestId));
    }
    public void Dispose() => Directory.Delete(_root, true);
}
