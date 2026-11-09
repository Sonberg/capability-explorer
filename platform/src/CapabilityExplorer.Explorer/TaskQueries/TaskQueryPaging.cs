using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CapabilityExplorer.Explorer;

public sealed class TaskQueryCursorException(string reason, string message) : Exception(message)
{
    public string Reason { get; } = reason;
}

/// <summary>Opaque continuation state, bound to the immutable snapshot and the policy digest so a later policy
/// change invalidates every outstanding cursor. Position = [offset, sha256hex(last item)] (≤512 total,
/// ≤2 positions, each ≤128 chars). The digest only verifies a resume point; it never selects, so a collision
/// cannot misroute and nothing is truncated.</summary>
public sealed class TaskQueryCursorCodec(byte[]? secret = null, TimeProvider? time = null)
{
    public const int MaxCursorLength = 512;
    public const int MaxPositions = 2;
    public const int MaxPositionLength = 128;

    private readonly byte[] _secret = secret ?? RandomNumberGenerator.GetBytes(32);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private sealed record State(int Version, string Snapshot, string Scope, string[] Position, long Expires);

    public string Encode(string snapshotId, string scope, params string[] position)
    {
        if (position.Length is < 1 or > MaxPositions || position.Any(p => p.Length > MaxPositionLength))
            throw new ArgumentException("cursor position exceeds bounds", nameof(position));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new State(1, snapshotId, scope, position, _time.GetUtcNow().AddHours(1).ToUnixTimeSeconds()));
        var cursor = Convert.ToBase64String(bytes) + "." + Convert.ToBase64String(HMACSHA256.HashData(_secret, bytes));
        if (cursor.Length > MaxCursorLength) throw new InvalidOperationException("computed cursor exceeds the 512-character bound");
        return cursor;
    }

    /// <summary>Null cursor means "from the start". Rejects an oversized cursor before any decode (bound
    /// enforcement happens before work, never after).</summary>
    public string[]? Decode(string snapshotId, string scope, string? cursor)
    {
        if (cursor is null) return null;
        if (cursor.Length > MaxCursorLength) throw new TaskQueryCursorException("cursor-too-long", "continuation cursor exceeds the 512-character bound");
        State state;
        try
        {
            var parts = cursor.Split('.');
            if (parts.Length != 2) throw new FormatException();
            var bytes = Convert.FromBase64String(parts[0]);
            var mac = Convert.FromBase64String(parts[1]);
            if (!CryptographicOperations.FixedTimeEquals(mac, HMACSHA256.HashData(_secret, bytes))) throw new FormatException();
            state = JsonSerializer.Deserialize<State>(bytes) ?? throw new FormatException();
            if (state.Version != 1 || state.Scope != scope || state.Expires <= _time.GetUtcNow().ToUnixTimeSeconds()
                || state.Position.Length is < 1 or > MaxPositions || state.Position.Any(p => p.Length > MaxPositionLength))
                throw new FormatException();
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            throw new TaskQueryCursorException("invalid-cursor", "continuation cursor is invalid or no longer matches this selection");
        }
        // The snapshot moved on since the cursor was issued: this is a freshness conflict (stale), not a
        // malformed-cursor error, because the cursor itself is well-formed and was valid for its own snapshot.
        if (state.Snapshot != snapshotId) throw new TaskQueryCursorException("stale-cursor-snapshot", "continuation cursor was issued for a snapshot that is no longer current");
        return state.Position;
    }

    public static string ScopeDigest(string operation, string policyDigest, string filterDigest) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{operation}|{policyDigest}|{filterDigest}")));

    public static string CanonicalDigest<T>(T value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
}

/// <summary>Offset + last-item-digest paging over an already-bounded, deterministically-ordered sequence. The
/// digest is a consistency guard: on resume, the item that should sit at offset-1 is re-hashed and compared
/// to the cursor; a mismatch is reported as an invalid cursor rather than silently skipping or repeating rows.</summary>
public static class TaskQueryPaging
{
    public sealed record Slice<T>(IReadOnlyList<T> Items, bool More, int NextOffset, string LastDigest);

    public static Slice<T> Take<T>(IReadOnlyList<T> ordered, int offset, string? expectedDigest, int limit, Func<T, string> digestOf)
    {
        if (offset < 0 || offset > ordered.Count) throw new TaskQueryCursorException("cursor-position-mismatch", "continuation position is outside the current selection");
        if (offset > 0)
        {
            var actual = digestOf(ordered[offset - 1]);
            if (expectedDigest != actual) throw new TaskQueryCursorException("cursor-position-mismatch", "continuation position no longer matches the selected snapshot");
        }
        var window = ordered.Skip(offset).Take(limit + 1).ToList();
        var page = window.Take(limit).ToList();
        var lastDigest = page.Count > 0 ? digestOf(page[^1]) : expectedDigest ?? "";
        return new(page, window.Count > limit, offset + page.Count, lastDigest);
    }

    public static (int Offset, string? Digest) DecodePosition(string[]? position)
    {
        try
        {
            return position switch
            {
                null => (0, null),
                [var offset] => (int.Parse(offset, System.Globalization.CultureInfo.InvariantCulture), null),
                [var offset, var digest] => (int.Parse(offset, System.Globalization.CultureInfo.InvariantCulture), digest),
                _ => throw new TaskQueryCursorException("invalid-cursor", "continuation position has an unsupported shape"),
            };
        }
        catch (FormatException)
        {
            throw new TaskQueryCursorException("invalid-cursor", "continuation position is not a valid offset");
        }
    }
}
