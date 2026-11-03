using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace CapabilityExplorer.Store;

public sealed record PublicationArtifactExpectation(int Index, string RepositoryId, string CommitSha,
    string UnitPath, string ExtractorName, string ExtractorVersion, string ConfigHash,
    string ContentSha256, string SourceSha256);

public sealed record PublicationAttemptDescriptor(string RunId, string AttemptId, string CapabilityId,
    string AcceptedInputsHash, string ManifestHash, long ExpectedGeneration,
    IReadOnlyList<PublicationArtifactExpectation> Artifacts);

public sealed record PublicationAttemptPublishResult(bool Published, string Reason,
    Guid? SnapshotId, long? Generation, long ObservedGeneration);

public enum PublicationCommitBoundary { BeforeCommit, AfterCommit }

public sealed record PublicationArtifactImport(string RunId, string AttemptId, int Index, string SourceSha256);

public sealed partial class SnapshotStore
{
    internal Func<PublicationCommitBoundary, CancellationToken, Task>? PublicationCommitBarrier { get; set; }

    public async Task BeginPublicationAttemptAsync(PublicationAttemptDescriptor attempt,
        CancellationToken cancellationToken = default)
    {
        ValidateAttempt(attempt);
        var selection = SelectionHash(attempt.Artifacts);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var insert = new NpgsqlCommand("""
            insert into analysis_publication_attempt(run_id,attempt_id,capability_id,accepted_inputs_hash,
                manifest_hash,expected_generation,expected_artifact_count,selection_sha256)
            values($1,$2,$3,$4,$5,$6,$7,$8)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue(attempt.RunId);
            insert.Parameters.AddWithValue(attempt.AttemptId);
            insert.Parameters.AddWithValue(attempt.CapabilityId);
            insert.Parameters.AddWithValue(attempt.AcceptedInputsHash);
            insert.Parameters.AddWithValue(attempt.ManifestHash);
            insert.Parameters.AddWithValue(attempt.ExpectedGeneration);
            insert.Parameters.AddWithValue(attempt.Artifacts.Count);
            insert.Parameters.AddWithValue(selection);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var artifact in attempt.Artifacts.OrderBy(a => a.Index))
        {
            await using var insert = new NpgsqlCommand("""
                insert into analysis_publication_attempt_artifact(run_id,attempt_id,artifact_index,repository_id,
                    commit_sha,unit_path,extractor_name,extractor_version,config_hash,content_sha256,
                    source_sha256,import_state)
                values($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,'pending')
                """, connection, transaction);
            insert.Parameters.AddWithValue(attempt.RunId);
            insert.Parameters.AddWithValue(attempt.AttemptId);
            insert.Parameters.AddWithValue(artifact.Index);
            insert.Parameters.AddWithValue(artifact.RepositoryId);
            insert.Parameters.AddWithValue(artifact.CommitSha);
            insert.Parameters.AddWithValue(artifact.UnitPath);
            insert.Parameters.AddWithValue(artifact.ExtractorName);
            insert.Parameters.AddWithValue(artifact.ExtractorVersion);
            insert.Parameters.AddWithValue(artifact.ConfigHash);
            insert.Parameters.AddWithValue(artifact.ContentSha256);
            insert.Parameters.AddWithValue(artifact.SourceSha256);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RecordPublicationArtifactAsync(string runId, string attemptId, int index,
        string state, long? artifactId = null, string? observedContentSha256 = null,
        CancellationToken cancellationToken = default)
    {
        if (state is not ("imported" or "reused" or "conflict" or "missing" or "invalid"))
            throw new ArgumentException("unsupported artifact outcome", nameof(state));
        if (state is "imported" or "reused")
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(artifactId ?? 0);
            if (observedContentSha256 is null || !Hash(observedContentSha256))
                throw new ArgumentException("successful import requires its exact content hash", nameof(observedContentSha256));
        }
        if (state == "conflict" && (observedContentSha256 is null || !Hash(observedContentSha256)))
            throw new ArgumentException("conflict requires the observed content hash", nameof(observedContentSha256));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAttemptAsync(connection, transaction, runId, attemptId, cancellationToken);
        await EnsureNoResultAsync(connection, transaction, runId, attemptId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            update analysis_publication_attempt_artifact
            set import_state=$4,artifact_id=$5,observed_content_sha256=$6
            where run_id=$1 and attempt_id=$2 and artifact_index=$3 and import_state='pending'
            """, connection, transaction);
        command.Parameters.AddWithValue(runId);
        command.Parameters.AddWithValue(attemptId);
        command.Parameters.AddWithValue(index);
        command.Parameters.AddWithValue(state);
        command.Parameters.AddWithValue((object?)artifactId ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)observedContentSha256 ?? DBNull.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("publication artifact receipt is missing, conflicting, or already final");
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task BindPublicationCandidateAsync(string runId, string attemptId, Guid candidateId,
        string inputIdentity, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputIdentity);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string capability;
        int expectedCount;
        await using (var attempt = new NpgsqlCommand("""
            select capability_id,expected_artifact_count,candidate_snapshot_id
            from analysis_publication_attempt where run_id=$1 and attempt_id=$2 for update
            """, connection, transaction))
        {
            attempt.Parameters.AddWithValue(runId);
            attempt.Parameters.AddWithValue(attemptId);
            await using var reader = await attempt.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("publication attempt receipt is missing");
            capability = reader.GetString(0).TrimEnd();
            expectedCount = reader.GetInt32(1);
            if (!reader.IsDBNull(2)) throw new InvalidOperationException("publication candidate is already bound");
        }
        var actualIds = new List<long>();
        await using (var artifacts = new NpgsqlCommand("""
            select artifact_id from analysis_publication_attempt_artifact
            where run_id=$1 and attempt_id=$2 and import_state in ('imported','reused')
            order by artifact_index
            """, connection, transaction))
        {
            artifacts.Parameters.AddWithValue(runId);
            artifacts.Parameters.AddWithValue(attemptId);
            await using var reader = await artifacts.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) actualIds.Add(reader.GetInt64(0));
        }
        if (actualIds.Count != expectedCount || actualIds.Distinct().Count() != expectedCount)
            throw new InvalidOperationException("candidate cannot bind an incomplete or duplicate artifact selection");
        string candidateCapability;
        string candidateIdentity;
        await using (var candidate = new NpgsqlCommand("""
            select capability_id,input_identity,status from analysis_snapshot where id=$1
            """, connection, transaction))
        {
            candidate.Parameters.AddWithValue(candidateId);
            await using var reader = await candidate.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("candidate snapshot is missing");
            candidateCapability = reader.GetString(0);
            candidateIdentity = reader.GetString(1);
            if (reader.GetString(2) != "candidate") throw new InvalidOperationException("publication candidate is not unpublished");
        }
        if (candidateCapability != capability || candidateIdentity != inputIdentity)
            throw new InvalidOperationException("candidate scope or content identity differs from the durable attempt");
        await using (var membership = new NpgsqlCommand("""
            select array_agg(artifact_id order by artifact_id) from analysis_snapshot_artifact where snapshot_id=$1
            """, connection, transaction))
        {
            membership.Parameters.AddWithValue(candidateId);
            var selected = (long[]?)await membership.ExecuteScalarAsync(cancellationToken) ?? [];
            if (!selected.Order().SequenceEqual(actualIds.Order()))
                throw new InvalidOperationException("candidate selection differs from the durable artifact receipt");
        }
        await using (var bind = new NpgsqlCommand("""
            update analysis_publication_attempt set candidate_snapshot_id=$3,input_identity=$4
            where run_id=$1 and attempt_id=$2 and candidate_snapshot_id is null
            """, connection, transaction))
        {
            bind.Parameters.AddWithValue(runId);
            bind.Parameters.AddWithValue(attemptId);
            bind.Parameters.AddWithValue(candidateId);
            bind.Parameters.AddWithValue(inputIdentity);
            if (await bind.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("publication candidate receipt conflicts");
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RecordPrepublicationFailureAsync(string runId, string attemptId, Guid? candidateId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAttemptAsync(connection, transaction, runId, attemptId, cancellationToken);
        await EnsureNoResultAsync(connection, transaction, runId, attemptId, cancellationToken);
        await using (var pending = new NpgsqlCommand("""
            select count(*) from analysis_publication_attempt_artifact
            where run_id=$1 and attempt_id=$2 and import_state='pending'
            """, connection, transaction))
        {
            pending.Parameters.AddWithValue(runId);
            pending.Parameters.AddWithValue(attemptId);
            if ((long)(await pending.ExecuteScalarAsync(cancellationToken))! != 0)
                throw new InvalidOperationException("incomplete artifact evidence cannot establish nonpublication");
        }
        await InsertResultAsync(connection, transaction, runId, attemptId, "not_published",
            "prepublication_failure", candidateId, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<PublicationAttemptPublishResult> PublishAttemptAsync(string runId, string attemptId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string capability, inputIdentity;
        long expectedGeneration;
        Guid candidateId;
        await using (var attempt = new NpgsqlCommand("""
            select capability_id,expected_generation,candidate_snapshot_id,input_identity
            from analysis_publication_attempt where run_id=$1 and attempt_id=$2 for update
            """, connection, transaction))
        {
            attempt.Parameters.AddWithValue(runId);
            attempt.Parameters.AddWithValue(attemptId);
            await using var reader = await attempt.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(2) || reader.IsDBNull(3))
                throw new InvalidOperationException("publication attempt has no durable candidate");
            capability = reader.GetString(0).TrimEnd();
            expectedGeneration = reader.GetInt64(1);
            candidateId = reader.GetGuid(2);
            inputIdentity = reader.GetString(3);
        }
        await EnsureNoResultAsync(connection, transaction, runId, attemptId, cancellationToken);
        await LockAsync(connection, transaction, "publish:" + capability, cancellationToken);
        string candidateStatus;
        string candidateIdentity;
        await using (var candidate = new NpgsqlCommand("""
            select status,input_identity from analysis_snapshot where id=$1 and capability_id=$2
            """, connection, transaction))
        {
            candidate.Parameters.AddWithValue(candidateId);
            candidate.Parameters.AddWithValue(capability);
            await using var reader = await candidate.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("durable publication candidate is missing");
            candidateStatus = reader.GetString(0);
            candidateIdentity = reader.GetString(1);
        }
        if (candidateIdentity != inputIdentity)
            throw new InvalidOperationException("publication candidate identity conflicts with its receipt");
        long currentGeneration = 0;
        Guid? currentSnapshot = null;
        await using (var pointer = new NpgsqlCommand("select snapshot_id,generation from published_snapshot where capability_id=$1", connection, transaction))
        {
            pointer.Parameters.AddWithValue(capability);
            await using var reader = await pointer.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                currentSnapshot = reader.GetGuid(0);
                currentGeneration = reader.GetInt64(1);
            }
        }
        if (candidateStatus != "candidate" || currentSnapshot == candidateId)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(false, "candidate_publication_observed", candidateId, null, currentGeneration);
        }
        if (currentGeneration != expectedGeneration)
        {
            const string reason = "stale_generation";
            await InsertResultAsync(connection, transaction, runId, attemptId, "not_published", reason, candidateId, null, cancellationToken);
            if (PublicationCommitBarrier is { } rejectedBarrier)
                await rejectedBarrier(PublicationCommitBoundary.BeforeCommit, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if (PublicationCommitBarrier is { } rejectedAfterBarrier)
                await rejectedAfterBarrier(PublicationCommitBoundary.AfterCommit, cancellationToken);
            return new(false, reason, candidateId, null, currentGeneration);
        }
        var generation = checked(currentGeneration + 1);
        await using (var publish = new NpgsqlCommand("""
            update analysis_snapshot set status='published',generation=$2,published_at=now()
            where id=$1 and status='candidate'
            """, connection, transaction))
        {
            publish.Parameters.AddWithValue(candidateId);
            publish.Parameters.AddWithValue(generation);
            if (await publish.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new SnapshotConflictException("publication candidate changed before the compare-and-swap");
        }
        await ExecuteAsync(connection, transaction, """
            insert into published_snapshot(capability_id,snapshot_id,generation) values($1,$2,$3)
            on conflict(capability_id) do update set snapshot_id=excluded.snapshot_id,generation=excluded.generation
            """, cancellationToken, capability, candidateId, generation);
        await ExecuteAsync(connection, transaction, "select pg_notify('ce_snapshot_published',$1)", cancellationToken, capability);
        await InsertResultAsync(connection, transaction, runId, attemptId, "published", "committed", candidateId, generation, cancellationToken);
        if (PublicationCommitBarrier is { } commitBarrier)
            await commitBarrier(PublicationCommitBoundary.BeforeCommit, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (PublicationCommitBarrier is { } afterCommitBarrier)
            await afterCommitBarrier(PublicationCommitBoundary.AfterCommit, cancellationToken);
        return new(true, "committed", candidateId, generation, generation);
    }

    private static async Task InsertResultAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string runId, string attemptId, string outcome, string reason, Guid? candidateId, long? generation,
        CancellationToken cancellationToken)
    {
        await using var result = new NpgsqlCommand("""
            insert into analysis_publication_attempt_result(run_id,attempt_id,outcome,reason,snapshot_id,publication_generation)
            values($1,$2,$3,$4,$5,$6)
            """, connection, transaction);
        result.Parameters.AddWithValue(runId);
        result.Parameters.AddWithValue(attemptId);
        result.Parameters.AddWithValue(outcome);
        result.Parameters.AddWithValue(reason);
        result.Parameters.AddWithValue((object?)candidateId ?? DBNull.Value);
        result.Parameters.AddWithValue((object?)generation ?? DBNull.Value);
        await result.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureNoResultAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string runId, string attemptId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            select 1 from analysis_publication_attempt_result where run_id=$1 and attempt_id=$2
            """, connection, transaction);
        command.Parameters.AddWithValue(runId);
        command.Parameters.AddWithValue(attemptId);
        if (await command.ExecuteScalarAsync(cancellationToken) is not null)
            throw new InvalidOperationException("publication attempt already has a durable result");
    }

    private static async Task LockAttemptAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string runId, string attemptId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            select 1 from analysis_publication_attempt where run_id=$1 and attempt_id=$2 for update
            """, connection, transaction);
        command.Parameters.AddWithValue(runId);
        command.Parameters.AddWithValue(attemptId);
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
            throw new InvalidOperationException("publication attempt receipt is missing");
    }

    private static void ValidateAttempt(PublicationAttemptDescriptor attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (!Id(attempt.RunId) || !Id(attempt.AttemptId) || !Hash(attempt.AcceptedInputsHash) || !Hash(attempt.ManifestHash))
            throw new ArgumentException("invalid run, attempt or frozen-input identity", nameof(attempt));
        ArgumentException.ThrowIfNullOrWhiteSpace(attempt.CapabilityId);
        ArgumentOutOfRangeException.ThrowIfNegative(attempt.ExpectedGeneration);
        if (attempt.Artifacts.Count is < 1 or > MaximumArtifacts ||
            attempt.Artifacts.Select(a => a.Index).Distinct().Count() != attempt.Artifacts.Count)
            throw new ArgumentException("invalid publication artifact selection", nameof(attempt));
        foreach (var artifact in attempt.Artifacts)
            if (artifact.Index < 0 || !Hash(artifact.ContentSha256) || !Hash(artifact.SourceSha256) ||
                !Hash(artifact.ConfigHash) || artifact.CommitSha.Length != 40 ||
                artifact.RepositoryId.Length is < 1 or > 100 || artifact.UnitPath.Length is < 1 or > 4096 ||
                string.IsNullOrWhiteSpace(artifact.ExtractorName) || string.IsNullOrWhiteSpace(artifact.ExtractorVersion))
                throw new ArgumentException("invalid publication artifact identity", nameof(attempt));
    }

    private static string SelectionHash(IReadOnlyList<PublicationArtifactExpectation> artifacts)
    {
        var canonical = artifacts.OrderBy(a => a.Index).Select(a => new
        {
            a.Index, a.RepositoryId, a.CommitSha, a.UnitPath, a.ExtractorName, a.ExtractorVersion,
            a.ConfigHash, a.ContentSha256, a.SourceSha256
        });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical))));
    }

    private static bool Id(string value) => value.Length == 32 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Hash(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
