using System.Data;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace CapabilityExplorer.Store;

public sealed record PublicationArtifactReconciliation(int Index, string RepositoryId, string CommitSha,
    string UnitPath, string ContentSha256, string Outcome, long? ArtifactId, string? ObservedContentSha256);

public sealed record PublicationReconciliationReport(string Outcome, string ReceiptState, string RunId,
    string AttemptId, string? CapabilityId, string? AcceptedInputsHash, string? ManifestHash,
    long? ExpectedGeneration, Guid? CandidateSnapshotId, string? CandidateInputIdentity,
    string? CandidatePublicationState, long? CandidatePublishedGeneration,
    Guid? CurrentSnapshotId, long CurrentGeneration, string? AttemptResult,
    string? AttemptResultReason, long? AttemptPublishedGeneration,
    IReadOnlyList<PublicationArtifactReconciliation> Artifacts);

public sealed partial class SnapshotStore
{
    public async Task<PublicationReconciliationReport> ReconcilePublicationAttemptAsync(string runId,
        string attemptId, CancellationToken cancellationToken = default)
    {
        if (runId.Length != 32 || attemptId.Length != 32 ||
            runId.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')) ||
            attemptId.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("run and attempt identities must be lowercase 32-character hexadecimal values");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await using (var readOnly = new NpgsqlCommand("set transaction read only", connection, transaction))
            await readOnly.ExecuteNonQueryAsync(cancellationToken);

        string? capability = null, accepted = null, manifest = null, candidateIdentity = null, candidateCapability = null;
        long? expectedGeneration = null, candidateGeneration = null, resultGeneration = null;
        Guid? candidateId = null, currentId = null, resultSnapshotId = null;
        string? candidateStatus = null, result = null, resultReason = null;
        long currentGeneration = 0;
        int expectedCount = 0;
        string? expectedSelection = null;
        await using (var query = new NpgsqlCommand("""
            select a.capability_id,a.accepted_inputs_hash,a.manifest_hash,a.expected_generation,
                a.expected_artifact_count,a.selection_sha256,a.candidate_snapshot_id,a.input_identity,
                s.capability_id,s.status,s.generation,p.snapshot_id,p.generation,r.outcome,r.reason,r.snapshot_id,r.publication_generation
            from analysis_publication_attempt a
            left join analysis_snapshot s on s.id=a.candidate_snapshot_id
            left join published_snapshot p on p.capability_id=a.capability_id
            left join analysis_publication_attempt_result r on r.run_id=a.run_id and r.attempt_id=a.attempt_id
            where a.run_id=$1 and a.attempt_id=$2
            """, connection, transaction))
        {
            query.Parameters.AddWithValue(runId);
            query.Parameters.AddWithValue(attemptId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.CommitAsync(cancellationToken);
                return EmptyReport(runId, attemptId);
            }
            capability = reader.GetString(0).TrimEnd();
            accepted = reader.GetString(1).TrimEnd();
            manifest = reader.GetString(2).TrimEnd();
            expectedGeneration = reader.GetInt64(3);
            expectedCount = reader.GetInt32(4);
            expectedSelection = reader.GetString(5).TrimEnd();
            candidateId = reader.IsDBNull(6) ? null : reader.GetGuid(6);
            candidateIdentity = reader.IsDBNull(7) ? null : reader.GetString(7);
            candidateCapability = reader.IsDBNull(8) ? null : reader.GetString(8);
            candidateStatus = reader.IsDBNull(9) ? null : reader.GetString(9);
            candidateGeneration = reader.IsDBNull(10) ? null : reader.GetInt64(10);
            currentId = reader.IsDBNull(11) ? null : reader.GetGuid(11);
            currentGeneration = reader.IsDBNull(12) ? 0 : reader.GetInt64(12);
            result = reader.IsDBNull(13) ? null : reader.GetString(13);
            resultReason = reader.IsDBNull(14) ? null : reader.GetString(14);
            resultSnapshotId = reader.IsDBNull(15) ? null : reader.GetGuid(15);
            resultGeneration = reader.IsDBNull(16) ? null : reader.GetInt64(16);
        }

        var artifacts = new List<PublicationArtifactReconciliation>();
        var expectations = new List<PublicationArtifactExpectation>();
        var receiptConflict = false;
        await using (var query = new NpgsqlCommand("""
            select i.artifact_index,i.repository_id,i.commit_sha,i.unit_path,i.extractor_name,
                i.extractor_version,i.config_hash,i.content_sha256,i.source_sha256,i.import_state,
                i.artifact_id,i.observed_content_sha256,a.content_sha256,a.repository_id,a.commit_sha,
                a.unit_path,a.extractor_name,a.extractor_version,a.config_hash,
                existing.content_sha256
            from analysis_publication_attempt_artifact i
            left join artifact a on a.id=i.artifact_id
            left join artifact existing on existing.repository_id=i.repository_id and existing.commit_sha=i.commit_sha
                and existing.unit_path=i.unit_path and existing.extractor_name=i.extractor_name
                and existing.extractor_version=i.extractor_version and existing.config_hash=i.config_hash
            where i.run_id=$1 and i.attempt_id=$2 order by i.artifact_index
            """, connection, transaction))
        {
            query.Parameters.AddWithValue(runId);
            query.Parameters.AddWithValue(attemptId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var index = reader.GetInt32(0);
                var repository = reader.GetString(1);
                var commit = reader.GetString(2);
                var unit = reader.GetString(3);
                var extractor = reader.GetString(4);
                var extractorVersion = reader.GetString(5);
                var configHash = reader.GetString(6);
                var contentHash = reader.GetString(7).TrimEnd();
                var sourceHash = reader.GetString(8).TrimEnd();
                var state = reader.GetString(9);
                long? artifactId = reader.IsDBNull(10) ? null : reader.GetInt64(10);
                var observedHash = reader.IsDBNull(11) ? null : reader.GetString(11).TrimEnd();
                expectations.Add(new(index, repository, commit, unit, extractor, extractorVersion, configHash, contentHash, sourceHash));
                var actualHash = reader.IsDBNull(12) ? null : reader.GetString(12).TrimEnd();
                var valid = state switch
                {
                    "imported" or "reused" => artifactId is not null && actualHash == contentHash &&
                        reader.GetString(13) == repository && reader.GetString(14) == commit &&
                        reader.GetString(15) == unit && reader.GetString(16) == extractor &&
                        reader.GetString(17) == extractorVersion && reader.GetString(18) == configHash &&
                        observedHash == contentHash,
                    "conflict" => artifactId is null && observedHash is not null &&
                        observedHash != contentHash && reader.GetString(19) == observedHash,
                    "missing" or "invalid" or "pending" => artifactId is null,
                    _ => false
                };
                if (!valid) receiptConflict = true;
                var reportOutcome = !valid ? "conflicting-receipt" : state == "pending" ? "uncertain" : state;
                artifacts.Add(new(index, repository, commit, unit, contentHash, reportOutcome, artifactId, observedHash));
            }
        }
        if (artifacts.Count != expectedCount || artifacts.Select(a => a.Index).Distinct().Count() != expectedCount ||
            SelectionHash(expectations) != expectedSelection || candidateId.HasValue != (candidateIdentity is not null))
            receiptConflict = true;

        if (candidateId is not null && (candidateStatus is null || candidateCapability != capability)) receiptConflict = true;
        if (candidateStatus == "candidate" && candidateGeneration is not null) receiptConflict = true;
        if (candidateStatus == "published" && candidateGeneration is null) receiptConflict = true;
        if (resultSnapshotId is not null && resultSnapshotId != candidateId) receiptConflict = true;
        if (result == "published" &&
            (resultReason != "committed" || candidateId is null || candidateStatus != "published" ||
             resultGeneration is null || candidateGeneration != resultGeneration)) receiptConflict = true;
        if (result == "not_published" &&
            (resultReason is null or "committed" || resultGeneration is not null)) receiptConflict = true;
        if (result is not (null or "published" or "not_published")) receiptConflict = true;
        if (currentId is null && currentGeneration != 0 || currentId is not null && currentGeneration < 1)
            receiptConflict = true;

        var outcome = receiptConflict ? "unresolved" : result switch
        {
            "published" => "confirmed-published",
            "not_published" => "confirmed-not-published",
            _ => "unresolved"
        };
        var report = new PublicationReconciliationReport(outcome, receiptConflict ? "conflicting" : "present",
            runId, attemptId, capability, accepted, manifest, expectedGeneration, candidateId, candidateIdentity,
            candidateStatus, candidateGeneration, currentId, currentGeneration, result, resultReason,
            resultGeneration, artifacts);
        await transaction.CommitAsync(cancellationToken);
        return report;
    }

    private static PublicationReconciliationReport EmptyReport(string runId, string attemptId) =>
        new("unresolved", "missing", runId, attemptId, null, null, null, null, null, null, null,
            null, null, 0, null, null, null, []);
}
