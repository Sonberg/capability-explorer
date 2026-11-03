using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Store;
using Xunit;

namespace CapabilityExplorer.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class PublicationReconciliationTests(PostgresFixture db)
{
    [Fact]
    public async Task Commit_succeeds_but_acknowledgement_is_lost_and_exact_attempt_is_confirmed()
    {
        var prepared = await Prepare();
        prepared.Store.PublicationCommitBarrier = (boundary, _) =>
            boundary == PublicationCommitBoundary.AfterCommit
                ? Task.FromException(new IOException("deterministic lost-ack barrier"))
                : Task.CompletedTask;

        await Assert.ThrowsAsync<IOException>(() => prepared.Store.PublishAttemptAsync(
            prepared.Attempt.RunId, prepared.Attempt.AttemptId));

        var report = await prepared.Store.ReconcilePublicationAttemptAsync(
            prepared.Attempt.RunId, prepared.Attempt.AttemptId);
        Assert.Equal("confirmed-published", report.Outcome);
        Assert.Equal("published", report.AttemptResult);
        Assert.Equal(prepared.CandidateId, report.CandidateSnapshotId);
        Assert.Equal("published", report.CandidatePublicationState);
        Assert.Equal(prepared.Expectation.ContentSha256, Assert.Single(report.Artifacts).ContentSha256);
        Assert.Contains(report.Artifacts[0].Outcome, new[] { "imported", "reused" });
    }

    [Fact]
    public async Task Historical_publication_remains_evidence_after_a_newer_pointer_advances()
    {
        var prepared = await Prepare();
        var first = await prepared.Store.PublishAttemptAsync(prepared.Attempt.RunId, prepared.Attempt.AttemptId);
        Assert.True(first.Published);
        var later = await NewArtifact();
        var laterImport = await new ArtifactImporter(db.DataSource).ImportAsync(later.Jsonl);
        var next = await prepared.Store.CreateAsync(prepared.Attempt.CapabilityId, "later-" + Guid.NewGuid().ToString("N"),
            [laterImport.ArtifactId]);
        var advanced = await prepared.Store.PublishAsync(next.Id, 1);

        var report = await prepared.Store.ReconcilePublicationAttemptAsync(prepared.Attempt.RunId, prepared.Attempt.AttemptId);

        Assert.Equal("confirmed-published", report.Outcome);
        Assert.Equal(prepared.CandidateId, report.CandidateSnapshotId);
        Assert.Equal(1L, report.CandidatePublishedGeneration);
        Assert.Equal(next.Id, report.CurrentSnapshotId);
        Assert.Equal(advanced.Generation, report.CurrentGeneration);
    }

    [Fact]
    public async Task Independent_publication_of_same_candidate_does_not_attribute_it_to_this_attempt()
    {
        var prepared = await Prepare();
        await prepared.Store.PublishAsync(prepared.CandidateId, 0);

        var result = await prepared.Store.PublishAttemptAsync(prepared.Attempt.RunId, prepared.Attempt.AttemptId);
        var report = await prepared.Store.ReconcilePublicationAttemptAsync(prepared.Attempt.RunId, prepared.Attempt.AttemptId);

        Assert.False(result.Published);
        Assert.Equal("candidate_publication_observed", result.Reason);
        Assert.Equal("unresolved", report.Outcome);
        Assert.Null(report.AttemptResult);
        Assert.Equal("published", report.CandidatePublicationState);
        Assert.Equal(prepared.CandidateId, report.CurrentSnapshotId);
    }

    [Fact]
    public async Task Independently_published_historical_candidate_does_not_confirm_this_attempt_as_not_published()
    {
        var prepared = await Prepare();
        await prepared.Store.PublishAsync(prepared.CandidateId, 0);
        var later = await NewArtifact();
        var laterImport = await new ArtifactImporter(db.DataSource).ImportAsync(later.Jsonl);
        var next = await prepared.Store.CreateAsync(prepared.Attempt.CapabilityId, "later-" + Guid.NewGuid().ToString("N"),
            [laterImport.ArtifactId]);
        await prepared.Store.PublishAsync(next.Id, 1);

        var result = await prepared.Store.PublishAttemptAsync(prepared.Attempt.RunId, prepared.Attempt.AttemptId);
        var report = await prepared.Store.ReconcilePublicationAttemptAsync(prepared.Attempt.RunId, prepared.Attempt.AttemptId);

        Assert.False(result.Published);
        Assert.Equal("candidate_publication_observed", result.Reason);
        Assert.Equal("unresolved", report.Outcome);
        Assert.Null(report.AttemptResult);
        Assert.Equal("published", report.CandidatePublicationState);
        Assert.Equal(prepared.CandidateId, report.CandidateSnapshotId);
        Assert.Equal(next.Id, report.CurrentSnapshotId);
    }

    [Fact]
    public async Task Stale_generation_has_durable_attempt_rejection_not_a_pointer_inference()
    {
        var store = new SnapshotStore(db.DataSource);
        var prior = await store.CreateAsync(Scope(), "prior-" + Guid.NewGuid().ToString("N"), []);
        await store.PublishAsync(prior.Id, 0);
        var prepared = await Prepare(prior.CapabilityId, expectedGeneration: 0);

        var result = await prepared.Store.PublishAttemptAsync(prepared.Attempt.RunId, prepared.Attempt.AttemptId);
        var report = await prepared.Store.ReconcilePublicationAttemptAsync(prepared.Attempt.RunId, prepared.Attempt.AttemptId);

        Assert.False(result.Published);
        Assert.Equal("stale_generation", result.Reason);
        Assert.Equal("confirmed-not-published", report.Outcome);
        Assert.Equal("not_published", report.AttemptResult);
        Assert.Equal(prior.Id, report.CurrentSnapshotId);
        Assert.Equal(1L, report.CurrentGeneration);
    }

    [Fact]
    public async Task Cancellation_before_commit_leaves_in_flight_attempt_unresolved()
    {
        var prepared = await Prepare();
        var reachedCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        prepared.Store.PublicationCommitBarrier = async (boundary, token) =>
        {
            if (boundary == PublicationCommitBoundary.BeforeCommit)
            {
                reachedCommit.TrySetResult();
                await releaseCommit.Task.WaitAsync(token);
            }
        };
        var pending = prepared.Store.PublishAttemptAsync(prepared.Attempt.RunId,
            prepared.Attempt.AttemptId, cancellation.Token);
        await reachedCommit.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var duringCommit = await prepared.Store.ReconcilePublicationAttemptAsync(
            prepared.Attempt.RunId, prepared.Attempt.AttemptId);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var afterCancellation = await prepared.Store.ReconcilePublicationAttemptAsync(
            prepared.Attempt.RunId, prepared.Attempt.AttemptId);

        Assert.Equal("unresolved", duringCommit.Outcome);
        Assert.Equal("unresolved", afterCancellation.Outcome);
        Assert.Null(afterCancellation.AttemptResult);
        Assert.Null(afterCancellation.CurrentSnapshotId);
    }

    [Fact]
    public async Task Conflict_receipt_is_exact_and_partial_import_failure_is_terminal_without_candidate()
    {
        var original = await NewArtifact();
        await new ArtifactImporter(db.DataSource).ImportAsync(original.Jsonl);
        var conflictingJsonl = ChangeNodeName(original.Jsonl);
        var conflicting = Describe(conflictingJsonl);
        var attempt = Descriptor(Scope(), 0, [conflicting.Expectation]);
        var store = new SnapshotStore(db.DataSource);
        await store.BeginPublicationAttemptAsync(attempt);

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(conflictingJsonl));
        var error = await Assert.ThrowsAsync<ArtifactConflictException>(() =>
            new ArtifactImporter(db.DataSource).ImportForPublicationAsync(stream,
                new PublicationArtifactImport(attempt.RunId, attempt.AttemptId, 0, conflicting.Expectation.SourceSha256)));
        Assert.NotNull(error.ExistingContentSha256);
        await store.RecordPrepublicationFailureAsync(attempt.RunId, attempt.AttemptId, null);

        var report = await store.ReconcilePublicationAttemptAsync(attempt.RunId, attempt.AttemptId);
        Assert.Equal("confirmed-not-published", report.Outcome);
        Assert.Equal("prepublication_failure", report.AttemptResultReason);
        Assert.Null(report.CandidateSnapshotId);
        Assert.Equal("conflict", Assert.Single(report.Artifacts).Outcome);
        Assert.Equal(original.Expectation.ContentSha256, report.Artifacts[0].ObservedContentSha256);
    }

    [Fact]
    public async Task Reused_artifact_is_recorded_and_reconciliation_is_read_only()
    {
        var artifact = await NewArtifact();
        await new ArtifactImporter(db.DataSource).ImportAsync(artifact.Jsonl);
        var attempt = Descriptor(Scope(), 0, [artifact.Expectation]);
        var store = new SnapshotStore(db.DataSource);
        await store.BeginPublicationAttemptAsync(attempt);
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(artifact.Jsonl));
        await new ArtifactImporter(db.DataSource).ImportForPublicationAsync(stream,
            new PublicationArtifactImport(attempt.RunId, attempt.AttemptId, 0, artifact.Expectation.SourceSha256));

        var before = await StateCounts();
        var report = await store.ReconcilePublicationAttemptAsync(attempt.RunId, attempt.AttemptId);
        var after = await StateCounts();

        Assert.Equal("reused", Assert.Single(report.Artifacts).Outcome);
        Assert.Equal(before, after);
        Assert.Empty(await Migrator.MigrateAsync(db.DataSource));
    }

    [Fact]
    public async Task Known_missing_artifact_is_reported_without_claiming_the_global_pointer_is_unchanged()
    {
        var artifact = await NewArtifact();
        var attempt = Descriptor(Scope(), 0, [artifact.Expectation]);
        var store = new SnapshotStore(db.DataSource);
        await store.BeginPublicationAttemptAsync(attempt);
        await store.RecordPublicationArtifactAsync(attempt.RunId, attempt.AttemptId, 0, "missing");
        await store.RecordPrepublicationFailureAsync(attempt.RunId, attempt.AttemptId, null);

        var report = await store.ReconcilePublicationAttemptAsync(attempt.RunId, attempt.AttemptId);

        Assert.Equal("confirmed-not-published", report.Outcome);
        Assert.Equal("missing", Assert.Single(report.Artifacts).Outcome);
        Assert.Equal(0L, report.CurrentGeneration);
    }

    [Fact]
    public async Task Cli_reports_missing_attempt_as_unresolved_without_migration_or_writes()
    {
        var run = Guid.NewGuid().ToString("N");
        var attempt = Guid.NewGuid().ToString("N");
        var hostRunPath = Path.Combine(Fixtures.RepositoryRoot, ".sandbox-cache", "requests", "runs", run);
        Assert.False(Directory.Exists(hostRunPath));
        var before = await StateCounts();
        var dll = Path.Combine(Fixtures.RepositoryRoot, "platform", "src", "CapabilityExplorer.Cli",
            "bin", "Debug", "net10.0", "ce.dll");
        Assert.True(File.Exists(dll), "Build the CLI before running its subprocess tests");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            WorkingDirectory = Fixtures.RepositoryRoot
        };
        start.ArgumentList.Add(dll);
        start.ArgumentList.Add("reconcile-publication");
        start.ArgumentList.Add("--run"); start.ArgumentList.Add(run);
        start.ArgumentList.Add("--attempt"); start.ArgumentList.Add(attempt);
        start.Environment["CE_DATABASE"] = db.ConnectionString;
        using var process = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        await Task.WhenAll(output, error, process.WaitForExitAsync(deadline.Token));
        var after = await StateCounts();

        Assert.Equal(1, process.ExitCode);
        Assert.Contains("\"outcome\": \"unresolved\"", await output);
        Assert.Contains("\"authorization\": \"none;", await output);
        Assert.Equal(before, after);
        Assert.False(Directory.Exists(hostRunPath));
    }

    [Theory]
    [InlineData("candidate-identity")]
    [InlineData("candidate-membership")]
    [InlineData("selection-hash")]
    public async Task Contradictory_durable_receipts_remain_unresolved_without_repair(string corruption)
    {
        var prepared = await Prepare();
        await prepared.Store.PublishAttemptAsync(prepared.Attempt.RunId, prepared.Attempt.AttemptId);
        var sql = corruption switch
        {
            "candidate-identity" => "update analysis_snapshot set input_identity='contradictory' where id=$1",
            "candidate-membership" => "delete from analysis_snapshot_artifact where snapshot_id=$1",
            _ => "update analysis_publication_attempt set selection_sha256=repeat('0',64) where candidate_snapshot_id=$1"
        };
        await using var corrupt = db.DataSource.CreateCommand(sql);
        corrupt.Parameters.AddWithValue(prepared.CandidateId);
        await corrupt.ExecuteNonQueryAsync();
        var before = await StateCounts();

        var report = await prepared.Store.ReconcilePublicationAttemptAsync(prepared.Attempt.RunId, prepared.Attempt.AttemptId);

        Assert.Equal("unresolved", report.Outcome);
        Assert.Equal("conflicting", report.ReceiptState);
        Assert.Equal(before, await StateCounts());
    }

    [Fact]
    public async Task Partial_import_keeps_success_and_uncertainty_separate_and_cannot_bind_candidate()
    {
        var first = await NewArtifact();
        var second = await NewArtifact();
        var attempt = Descriptor(Scope(), 0, [first.Expectation, second.Expectation with { Index = 1 }]);
        var store = new SnapshotStore(db.DataSource);
        await store.BeginPublicationAttemptAsync(attempt);
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(first.Jsonl));
        var imported = await new ArtifactImporter(db.DataSource).ImportForPublicationAsync(stream,
            new PublicationArtifactImport(attempt.RunId, attempt.AttemptId, 0, first.Expectation.SourceSha256));
        var candidate = await store.CreateAsync(attempt.CapabilityId, "partial-" + attempt.RunId, [imported.ArtifactId]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.BindPublicationCandidateAsync(attempt.RunId, attempt.AttemptId, candidate.Id, candidate.InputIdentity));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.RecordPrepublicationFailureAsync(attempt.RunId, attempt.AttemptId, null));

        var report = await store.ReconcilePublicationAttemptAsync(attempt.RunId, attempt.AttemptId);

        Assert.Equal("unresolved", report.Outcome);
        Assert.Equal("imported", report.Artifacts[0].Outcome);
        Assert.Equal("uncertain", report.Artifacts[1].Outcome);
        Assert.Null(report.AttemptResult);
        Assert.Null(report.CandidateSnapshotId);
        Assert.Null(report.CurrentSnapshotId);
    }

    private async Task<(SnapshotStore Store, PublicationAttemptDescriptor Attempt,
        PublicationArtifactExpectation Expectation, Guid CandidateId, string Jsonl)> Prepare(
        string? scope = null, long expectedGeneration = 0)
    {
        var artifact = await NewArtifact();
        var actualScope = scope ?? Scope();
        var attempt = Descriptor(actualScope, expectedGeneration, [artifact.Expectation]);
        var store = new SnapshotStore(db.DataSource);
        await store.BeginPublicationAttemptAsync(attempt);
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(artifact.Jsonl));
        var imported = await new ArtifactImporter(db.DataSource).ImportForPublicationAsync(stream,
            new PublicationArtifactImport(attempt.RunId, attempt.AttemptId, 0, artifact.Expectation.SourceSha256));
        var identity = "candidate-" + attempt.RunId;
        var candidate = await store.CreateAsync(actualScope, identity, [imported.ArtifactId]);
        await store.BindPublicationCandidateAsync(attempt.RunId, attempt.AttemptId, candidate.Id, identity);
        return (store, attempt, artifact.Expectation, candidate.Id, artifact.Jsonl);
    }

    private static PublicationAttemptDescriptor Descriptor(string scope, long expectedGeneration,
        IReadOnlyList<PublicationArtifactExpectation> artifacts) =>
        new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), scope,
            HashText(Guid.NewGuid().ToString("N")), HashText(Guid.NewGuid().ToString("N")),
            expectedGeneration, artifacts);

    private static async Task<(string Jsonl, PublicationArtifactExpectation Expectation)> NewArtifact()
    {
        var rows = Fixtures.Golden("web-ts/v1-base/_fixture_web.jsonl").TrimEnd('\n')
            .Split('\n').Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
        rows[0]["configHash"] = HashText(Guid.NewGuid().ToString("N"));
        return Describe(string.Join('\n', rows.Select(row => row.ToJsonString())) + "\n");
    }

    private static (string Jsonl, PublicationArtifactExpectation Expectation) Describe(string jsonl)
    {
        var validation = ArtifactValidator.Validate(jsonl);
        Assert.True(validation.IsValid);
        var artifact = validation.Artifact!;
        var bytes = Encoding.UTF8.GetBytes(jsonl);
        var header = artifact.Header;
        return (jsonl, new PublicationArtifactExpectation(0, header.RepositoryId, header.CommitSha,
            header.Unit.Path, header.Extractor.Name, header.Extractor.Version, header.ConfigHash,
            artifact.ContentSha256, Convert.ToHexStringLower(SHA256.HashData(bytes))));
    }

    private static string ChangeNodeName(string jsonl)
    {
        var rows = jsonl.TrimEnd('\n').Split('\n').Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
        var node = rows.First(row => (string?)row["kind"] == "node");
        node["name"] = ((string?)node["name"] ?? "node") + " conflicting";
        return string.Join('\n', rows.Select(row => row.ToJsonString())) + "\n";
    }

    private static string Scope() => "issue61-" + Guid.NewGuid().ToString("N");
    private static string HashText(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private async Task<(long Attempts, long Artifacts, long Results, long Imported, long Schemas)> StateCounts()
    {
        return (await db.CountAsync("analysis_publication_attempt"),
            await db.CountAsync("analysis_publication_attempt_artifact"),
            await db.CountAsync("analysis_publication_attempt_result"),
            await db.CountAsync("artifact"),
            await db.CountAsync("schema_migration"));
    }
}
