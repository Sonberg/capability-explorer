using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CapabilityExplorer.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace CapabilityExplorer.Store;

public sealed partial class ArtifactImporter
{
    /// <summary>Maximum encoded size of one record, independent of artifact size.</summary>
    public const int MaximumRecordBytes = 8 * 1024 * 1024;

    /// <summary>Streams a complete artifact through transaction-local staging. Caller owns the stream.
    /// Memory is bounded by one record plus PostgreSQL's COPY buffers. No facts become visible until validation succeeds.</summary>
    public async Task<ImportResult> ImportAsync(Stream utf8Jsonl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(utf8Jsonl);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await Exec(connection, tx, """
            create temp table ce_import_record (line int not null,kind text not null,id text,body jsonb not null) on commit drop;
            """, cancellationToken);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        HeaderRecord? header = null;
        CompleteRecord? complete = null;
        var line = 0;
        await using (var writer = await connection.BeginBinaryImportAsync(
            "copy ce_import_record(line,kind,id,body) from stdin (format binary)", cancellationToken))
        {
            await foreach (var bytes in ReadLinesAsync(utf8Jsonl, hash, cancellationToken))
            {
                line++;
                JsonDocument document;
                try { document = JsonDocument.Parse(bytes); }
                catch (JsonException) { throw Invalid(line, "invalid JSON"); }
                using (document)
                {
                    var record = document.RootElement;
                    var errors = ContractSchema.Instance.Validate(record);
                    if (errors.Count > 0) throw Invalid(line, $"schema {errors[0]}");
                    var kind = record.GetProperty("kind").GetString()!;
                    if (complete is not null) throw Invalid(line, "'complete' must be the last record");
                    if (line == 1 && kind != "header") throw Invalid(line, "first record must be the header");
                    if (kind == "header")
                    {
                        if (header is not null) throw Invalid(line, "'header' may appear only once");
                        header = record.Deserialize<HeaderRecord>(JsonOptions)!;
                    }
                    else if (kind == "complete") complete = record.Deserialize<CompleteRecord>(JsonOptions)!;
                    else
                    {
                        var local = ArtifactValidator.ValidateRecordSemantics(record, header!);
                        if (local.Count > 0) throw Invalid(line, local[0]);
                    }
                    await writer.StartRowAsync(cancellationToken);
                    await writer.WriteAsync(line, NpgsqlDbType.Integer, cancellationToken);
                    await writer.WriteAsync(kind, NpgsqlDbType.Text, cancellationToken);
                    await WriteNullable(writer, record.TryGetProperty("id", out var id) ? id.GetString() : null, NpgsqlDbType.Text, cancellationToken);
                    await writer.WriteAsync(record.GetRawText(), NpgsqlDbType.Jsonb, cancellationToken);
                }
            }
            await writer.CompleteAsync(cancellationToken);
        }
        if (header is null) throw Invalid(0, "empty artifact");
        if (complete is null) throw Invalid(line, "last record must be 'complete' (artifact is truncated or unfinished)");
        await ValidateStagedAsync(connection, tx, complete, line, cancellationToken);
        var sha = Convert.ToHexStringLower(hash.GetHashAndReset());
        return await CommitStagedAsync(connection, tx, header, complete, sha, cancellationToken);
    }

    private static InvalidArtifactException Invalid(int line, string message) => new([new Problem(line, message)]);

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadLinesAsync(Stream stream, IncrementalHash hash,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var line = new MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) != 0)
        {
            hash.AppendData(buffer, 0, read);
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                if (line.Length + i - start > MaximumRecordBytes) throw Invalid(0, "record exceeds maximum encoded size");
                line.Write(buffer, start, i - start);
                yield return line.GetBuffer().AsMemory(0, checked((int)line.Length));
                line.SetLength(0);
                start = i + 1;
            }
            if (line.Length + read - start > MaximumRecordBytes) throw Invalid(0, "record exceeds maximum encoded size");
            line.Write(buffer, start, read - start);
        }
        if (line.Length != 0) yield return line.GetBuffer().AsMemory(0, checked((int)line.Length));
    }
}
