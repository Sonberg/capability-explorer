using System.Text.Json;
using CapabilityExplorer.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace CapabilityExplorer.Store;

public enum ImportOutcome
{
    Imported,
    AlreadyImported,
}

public sealed record ImportResult(long ArtifactId, ImportOutcome Outcome);

/// <summary>The artifact failed contract validation; nothing was written.</summary>
public sealed class InvalidArtifactException(IReadOnlyList<Problem> problems)
    : Exception($"artifact is invalid: {string.Join("; ", problems.Take(5))}{(problems.Count > 5 ? $" (+{problems.Count - 5} more)" : "")}")
{
    public IReadOnlyList<Problem> Problems { get; } = problems;
}

/// <summary>
/// The analysis key is already stored with different content. Extractors must be deterministic for a given
/// revision, unit, extractor version and configuration, so this is an error, never an overwrite.
/// </summary>
public sealed class ArtifactConflictException(string message) : Exception(message);

/// <summary>
/// Imports validated artifacts. All rows of one artifact are written in a single transaction: either the whole
/// artifact is visible or none of it is. Re-importing identical content is a no-op, so retries are safe.
/// </summary>
public sealed partial class ArtifactImporter(NpgsqlDataSource dataSource)
{
    public Task<ImportResult> ImportAsync(string jsonl, CancellationToken cancellationToken = default)
    {
        var report = ArtifactValidator.Validate(jsonl);
        if (!report.IsValid)
        {
            throw new InvalidArtifactException(report.Problems);
        }

        return ImportAsync(report.Artifact!, cancellationToken);
    }

    public async Task<ImportResult> ImportAsync(Artifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        NodeSearchNormalization.CheckRuntime();
        var h = artifact.Header;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        await Exec(connection, tx, "insert into repository (id) values ($1) on conflict do nothing", cancellationToken, h.RepositoryId);
        await Exec(connection, tx, "insert into revision (repository_id, commit_sha) values ($1, $2) on conflict do nothing", cancellationToken, h.RepositoryId, h.CommitSha);

        long? inserted;
        await using (var insert = new NpgsqlCommand(
            """
            insert into artifact (repository_id, commit_sha, unit_path, unit_name, language, extractor_name, extractor_version,
                                  config_hash, input_hash, status, content_sha256, header, complete)
            values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13)
            on conflict on constraint artifact_analysis_key do nothing
            returning id
            """,
            connection,
            tx))
        {
            foreach (var value in new object[]
            {
                h.RepositoryId, h.CommitSha, h.Unit.Path, h.Unit.Name, h.Language, h.Extractor.Name, h.Extractor.Version,
                h.ConfigHash, h.InputHash, artifact.Complete.Status, artifact.ContentSha256,
            })
            {
                insert.Parameters.AddWithValue(value);
            }

            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = JsonSerializer.Serialize(h, JsonOptions) });
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = JsonSerializer.Serialize(artifact.Complete, JsonOptions) });
            inserted = (long?)await insert.ExecuteScalarAsync(cancellationToken);
        }

        if (inserted is null)
        {
            // Same analysis key exists (possibly committed by a concurrent import we waited on).
            await using var existing = new NpgsqlCommand(
                """
                select id, content_sha256 from artifact
                where repository_id = $1 and commit_sha = $2 and unit_path = $3
                  and extractor_name = $4 and extractor_version = $5 and config_hash = $6
                """,
                connection,
                tx);
            foreach (var value in new object[] { h.RepositoryId, h.CommitSha, h.Unit.Path, h.Extractor.Name, h.Extractor.Version, h.ConfigHash })
            {
                existing.Parameters.AddWithValue(value);
            }

            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            var id = reader.GetInt64(0);
            var sha = reader.GetString(1);
            await reader.DisposeAsync();
            await tx.RollbackAsync(cancellationToken);
            if (sha == artifact.ContentSha256)
            {
                await NodeSearchNormalization.EnsureOnConnectionAsync(connection,[id],cancellationToken);
                return new ImportResult(id, ImportOutcome.AlreadyImported);
            }
            throw new ArtifactConflictException(
                    $"artifact {id} already holds {h.RepositoryId}@{h.CommitSha[..12]} {h.Unit.Path} ({h.Extractor.Name} {h.Extractor.Version}) with different content; extractor output is not deterministic");
        }

        var artifactId = inserted.Value;
        await CopyEvidence(connection, artifactId, artifact.Evidence, cancellationToken);
        await CopyNodes(connection, artifactId, artifact.Nodes, cancellationToken);
        await NodeSearchNormalization.WriteImportedAsync(connection,tx,artifactId,artifact.Nodes,cancellationToken);
        await CopyEdges(connection, artifactId, artifact.Edges, cancellationToken);
        await CopyCalculations(connection, artifactId, artifact.Calculations, cancellationToken);
        await CopyDiagnostics(connection, artifactId, artifact.Diagnostics, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new ImportResult(artifactId, ImportOutcome.Imported);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static async Task Exec(NpgsqlConnection c, NpgsqlTransaction tx, string sql, CancellationToken ct, params object[] args)
    {
        await using var command = new NpgsqlCommand(sql, c, tx);
        foreach (var a in args)
        {
            command.Parameters.AddWithValue(a);
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    private static string? Json(JsonElement? element) => element?.GetRawText();

    private static async Task CopyEvidence(NpgsqlConnection c, long id, IReadOnlyList<EvidenceRecord> rows, CancellationToken ct)
    {
        await using var w = await c.BeginBinaryImportAsync(
            "copy evidence (artifact_id, local_id, repository_id, commit_sha, path, start_line, start_column, end_line, end_column, snippet_sha256, extractor) from stdin (format binary)", ct);
        foreach (var e in rows)
        {
            await w.StartRowAsync(ct);
            await w.WriteAsync(id, NpgsqlDbType.Bigint, ct);
            await w.WriteAsync(e.Id, NpgsqlDbType.Text, ct);
            await w.WriteAsync(e.RepositoryId, NpgsqlDbType.Text, ct);
            await w.WriteAsync(e.CommitSha, NpgsqlDbType.Char, ct);
            await w.WriteAsync(e.Path, NpgsqlDbType.Text, ct);
            await w.WriteAsync(e.Span.StartLine, NpgsqlDbType.Integer, ct);
            await w.WriteAsync(e.Span.StartColumn, NpgsqlDbType.Integer, ct);
            await w.WriteAsync(e.Span.EndLine, NpgsqlDbType.Integer, ct);
            await w.WriteAsync(e.Span.EndColumn, NpgsqlDbType.Integer, ct);
            await w.WriteAsync(e.SnippetSha256, NpgsqlDbType.Char, ct);
            await w.WriteAsync(JsonSerializer.Serialize(e.Extractor, JsonOptions), NpgsqlDbType.Jsonb, ct);
        }

        await w.CompleteAsync(ct);
    }

    private static async Task CopyNodes(NpgsqlConnection c, long id, IReadOnlyList<NodeRecord> rows, CancellationToken ct)
    {
        await using var w = await c.BeginBinaryImportAsync(
            "copy node (artifact_id, local_id, symbol_key, node_kind, name, signature, generated, props, evidence) from stdin (format binary)", ct);
        foreach (var n in rows)
        {
            await w.StartRowAsync(ct);
            await w.WriteAsync(id, NpgsqlDbType.Bigint, ct);
            await w.WriteAsync(n.Id, NpgsqlDbType.Text, ct);
            await w.WriteAsync(n.SymbolKey, NpgsqlDbType.Text, ct);
            await w.WriteAsync(n.NodeKind, NpgsqlDbType.Text, ct);
            await w.WriteAsync(n.Name, NpgsqlDbType.Text, ct);
            await WriteNullable(w, n.Signature, NpgsqlDbType.Text, ct);
            await w.WriteAsync(n.Generated ?? false, NpgsqlDbType.Boolean, ct);
            await WriteNullable(w, Json(n.Props), NpgsqlDbType.Jsonb, ct);
            await w.WriteAsync(n.Evidence.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text, ct);
        }

        await w.CompleteAsync(ct);
    }

    private static async Task CopyEdges(NpgsqlConnection c, long id, IReadOnlyList<EdgeRecord> rows, CancellationToken ct)
    {
        await using var w = await c.BeginBinaryImportAsync(
            "copy edge (artifact_id, local_id, from_key, relation, to_key, derivation, resolution, unresolved_reason, rule, props, evidence) from stdin (format binary)", ct);
        foreach (var e in rows)
        {
            await w.StartRowAsync(ct);
            await w.WriteAsync(id, NpgsqlDbType.Bigint, ct);
            await w.WriteAsync(e.Id, NpgsqlDbType.Text, ct);
            await w.WriteAsync(e.From, NpgsqlDbType.Text, ct);
            await w.WriteAsync(e.Relation, NpgsqlDbType.Text, ct);
            await WriteNullable(w, e.To, NpgsqlDbType.Text, ct);
            await w.WriteAsync(e.Derivation, NpgsqlDbType.Text, ct);
            await w.WriteAsync(e.Resolution, NpgsqlDbType.Text, ct);
            await WriteNullable(w, e.UnresolvedReason, NpgsqlDbType.Text, ct);
            await WriteNullable(w, e.Rule is null ? null : JsonSerializer.Serialize(e.Rule, JsonOptions), NpgsqlDbType.Jsonb, ct);
            await WriteNullable(w, Json(e.Props), NpgsqlDbType.Jsonb, ct);
            await w.WriteAsync(e.Evidence.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text, ct);
        }

        await w.CompleteAsync(ct);
    }

    private static async Task CopyCalculations(NpgsqlConnection c, long id, IReadOnlyList<CalculationRecord> rows, CancellationToken ct)
    {
        await using var w = await c.BeginBinaryImportAsync(
            "copy calculation (artifact_id, local_id, symbol_key, ir_hash, body) from stdin (format binary)", ct);
        foreach (var calc in rows)
        {
            await w.StartRowAsync(ct);
            await w.WriteAsync(id, NpgsqlDbType.Bigint, ct);
            await w.WriteAsync(calc.Id, NpgsqlDbType.Text, ct);
            await w.WriteAsync(calc.SymbolKey, NpgsqlDbType.Text, ct);
            await WriteNullable(w, calc.Raw.TryGetProperty("irHash", out var h) ? h.GetString() : null, NpgsqlDbType.Char, ct);
            await w.WriteAsync(calc.Raw.GetRawText(), NpgsqlDbType.Jsonb, ct);
        }

        await w.CompleteAsync(ct);
    }

    private static async Task CopyDiagnostics(NpgsqlConnection c, long id, IReadOnlyList<DiagnosticRecord> rows, CancellationToken ct)
    {
        await using var w = await c.BeginBinaryImportAsync(
            "copy diagnostic (artifact_id, local_id, severity, code, message, path, affects_coverage, evidence) from stdin (format binary)", ct);
        foreach (var d in rows)
        {
            await w.StartRowAsync(ct);
            await w.WriteAsync(id, NpgsqlDbType.Bigint, ct);
            await w.WriteAsync(d.Id, NpgsqlDbType.Text, ct);
            await w.WriteAsync(d.Severity, NpgsqlDbType.Text, ct);
            await w.WriteAsync(d.Code, NpgsqlDbType.Text, ct);
            await w.WriteAsync(d.Message, NpgsqlDbType.Text, ct);
            await WriteNullable(w, d.Path, NpgsqlDbType.Text, ct);
            await w.WriteAsync(d.AffectsCoverage ?? false, NpgsqlDbType.Boolean, ct);
            if (d.Evidence is null)
            {
                await w.WriteNullAsync(ct);
            }
            else
            {
                await w.WriteAsync(d.Evidence.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text, ct);
            }
        }

        await w.CompleteAsync(ct);
    }

    private static async Task WriteNullable(NpgsqlBinaryImporter w, string? value, NpgsqlDbType type, CancellationToken ct)
    {
        if (value is null)
        {
            await w.WriteNullAsync(ct);
        }
        else
        {
            await w.WriteAsync(value, type, ct);
        }
    }
}
