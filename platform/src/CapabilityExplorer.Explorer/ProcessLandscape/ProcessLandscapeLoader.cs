using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CapabilityExplorer.Explorer;

/// <summary>Bounded, server-configuration-only reader for the single <c>ce.process-landscape.v1</c> snapshot file.
/// The path comes exclusively from <see cref="ProcessLandscapeOptions.SnapshotPath"/> (never request data); callers
/// only ever select "the one configured snapshot", with no filesystem path accepted from a client.
///
/// <para><b>Trusted-directory invariant, and the precise race this does and does not resist.</b> The file is
/// opened first; the byte-length bound is then read from the already-open <see cref="FileStream"/> itself (not a
/// separate pre-open stat), so the length check can never be invalidated by anything that happens to the path
/// after the open succeeds. The directory/reparse-point/device-attributes check still uses a path-based
/// <see cref="File.GetAttributes(string)"/> call issued immediately after the open (there is no portable,
/// dependency-free way to read file-type attributes from an open <see cref="FileStream"/> handle in .NET), so a
/// narrow window remains between the open and that attributes check - this reorder only collapses the much larger
/// prior window between the original stat-before-open and the read. True elimination of the remaining window needs
/// a platform-specific no-follow open (<c>O_NOFOLLOW</c> on Unix, <c>FILE_FLAG_OPEN_REPARSE_POINT</c> plus manual
/// reparse-point inspection on Windows), which is deliberately not implemented here (it would require new native
/// interop, disproportionate for a path that is never attacker-controlled request data). This mitigation only
/// provides any benefit at all if the trusted-directory invariant holds: <see cref="ProcessLandscapeOptions.SnapshotPath"/>
/// and every directory component above it must be writable only by the same trust boundary that controls this
/// server's own configuration, never by any co-located principal with lower trust. If that invariant does not
/// hold, no amount of check reordering here resists a deliberately racing attacker, and this comment should not be
/// read as claiming otherwise.</para></summary>
public static class ProcessLandscapeLoader
{
    private const int MaxBytes = ProcessLandscapeSizeLimits.MaxSnapshotFileBytes;

    /// <summary><see cref="JsonSerializerOptions.RespectNullableAnnotations"/> makes the deserializer itself
    /// reject an explicit JSON <c>null</c> assigned to any non-nullable reference-typed property/field/constructor
    /// parameter in the DTOs below (for example top-level <c>sources: null</c>, <c>processes: null</c>,
    /// <c>overlays: null</c>, or a nested non-nullable record such as an endpoint's <c>Source</c>/<c>Target</c>),
    /// surfacing as a <see cref="JsonException"/> (mapped below to the typed <c>invalid</c> rejection) instead of
    /// a later <see cref="NullReferenceException"/>/500 once validation dereferences the field. This flag has one
    /// documented limitation: it cannot distinguish a collection's element-type nullability (for example
    /// <c>List&lt;T&gt;</c> vs <c>List&lt;T?&gt;</c>), so an explicit <c>null</c> *item inside* an otherwise
    /// present array (<c>sources: [null]</c>, <c>processes: [null]</c>) still deserializes successfully; those are
    /// guarded explicitly in <see cref="ProcessLandscapeValidation"/> instead.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 64,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    public static async Task<ProcessLandscapeSnapshot> LoadAsync(ProcessLandscapeOptions options, CancellationToken ct)
    {
        if (options.SnapshotPath is not { Length: > 0 } path || !Path.IsPathFullyQualified(path))
            throw new ProcessLandscapeRejectedException("denied", "process-landscape-not-configured");

        byte[] raw;
        try
        {
            // Open before checking length: FileStream.Length reflects the already-open handle, so it can never be
            // invalidated by a path swap after the open succeeds. The attributes check just below still uses a
            // path-based stat (see the type-level doc comment for why, and for the resulting narrowed-not-eliminated
            // race window).
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new ProcessLandscapeRejectedException("unavailable", "process-landscape-snapshot-missing");
            if (file.Length > MaxBytes) throw new ProcessLandscapeRejectedException("limit", "process-landscape-snapshot-too-large");

            using var buffer = new MemoryStream(checked((int)file.Length));
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = await file.ReadAsync(chunk, ct).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + read > MaxBytes) throw new ProcessLandscapeRejectedException("limit", "process-landscape-snapshot-too-large");
                buffer.Write(chunk, 0, read);
            }
            raw = buffer.ToArray();
        }
        catch (FileNotFoundException)
        {
            throw new ProcessLandscapeRejectedException("unavailable", "process-landscape-snapshot-missing");
        }
        catch (DirectoryNotFoundException)
        {
            throw new ProcessLandscapeRejectedException("unavailable", "process-landscape-snapshot-missing");
        }
        catch (UnauthorizedAccessException)
        {
            throw new ProcessLandscapeRejectedException("unavailable", "process-landscape-snapshot-unreadable");
        }
        catch (IOException)
        {
            throw new ProcessLandscapeRejectedException("unavailable", "process-landscape-snapshot-unreadable");
        }


        ProcessLandscapeSnapshot snapshot;
        try
        {
            using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 64 });
            RejectDuplicateMembers(document.RootElement);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Invalid snapshot object.");
            if (document.RootElement.TryGetProperty("documents", out var documents))
            {
                if (documents.ValueKind != JsonValueKind.Array) throw new JsonException("Invalid documents collection.");
                ValidateDocumentJsonLimit(raw, documents.GetArrayLength());
            }
            snapshot = document.RootElement.Deserialize<ProcessLandscapeSnapshot>(Json)
                ?? throw new ProcessLandscapeRejectedException("invalid", "process-landscape-snapshot-null");
        }
        catch (JsonException)
        {
            throw new ProcessLandscapeRejectedException("invalid", "process-landscape-snapshot-malformed");
        }
        catch (NotSupportedException)
        {
            throw new ProcessLandscapeRejectedException("invalid", "process-landscape-snapshot-malformed");
        }

        ProcessLandscapeValidation.Validate(snapshot, options.AllowModelContent);
        return snapshot;
    }

    private static void ValidateDocumentJsonLimit(byte[] raw, int documentCount)
    {
        // The producer sums individual document objects, excluding the enclosing array and separators.
        var maximumBytes = (long)ProcessLandscapeDocumentLimits.MaxJsonBytes + Math.Max(2, documentCount + 1);
        var reader = new Utf8JsonReader(raw, new JsonReaderOptions { MaxDepth = 64 });
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 ||
                !reader.ValueTextEquals("documents")) continue;
            reader.Read();
            var start = reader.TokenStartIndex;
            reader.Skip();
            // CLI snapshots may be indented. Count compact JSON bytes without copying private data or
            // charging insignificant whitespace against the producer's compact document-JSON budget.
            if (reader.BytesConsumed - start > maximumBytes)
                ValidateCompactDocumentBytes(raw.AsSpan((int)start, checked((int)(reader.BytesConsumed - start))), maximumBytes);
            break;
        }
    }

    private static void ValidateCompactDocumentBytes(ReadOnlySpan<byte> raw, long maximumBytes)
    {
        var quoted = false;
        var escaped = false;
        long length = 0;
        foreach (var value in raw)
        {
            if (quoted)
            {
                length++;
                if (escaped) escaped = false;
                else if (value == '\\') escaped = true;
                else if (value == '"') quoted = false;
            }
            else if (value == '"') { quoted = true; length++; }
            else if (value is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) length++;
            if (length > maximumBytes)
                throw new ProcessLandscapeRejectedException("limit", "document-json-limit");
        }
    }

    /// <summary>Strict JSON (<see cref="JsonUnmappedMemberHandling.Disallow"/>) already rejects unknown members,
    /// but duplicate keys on the same object are otherwise silently resolved to "last wins" by both
    /// <see cref="JsonDocument"/> and <see cref="JsonSerializer"/>; reject them outright instead.</summary>
    private static void RejectDuplicateMembers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate process-landscape snapshot member.");
                RejectDuplicateMembers(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) RejectDuplicateMembers(item);
        }
    }
}
