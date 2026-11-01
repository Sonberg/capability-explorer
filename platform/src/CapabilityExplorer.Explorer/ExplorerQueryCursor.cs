using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CapabilityExplorer.Explorer;

/// <summary>Opaque continuation state is integrity protected, never an authorization grant.</summary>
public sealed class ExplorerQueryCursor
{
    private readonly byte[] _secret;
    public ExplorerQueryCursor() : this(RandomNumberGenerator.GetBytes(32)) { }
    public ExplorerQueryCursor(byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length < 32) throw new ArgumentException("cursor key must contain at least 32 bytes", nameof(secret));
        _secret = secret.ToArray();
    }
    internal sealed record State(int Version, Guid Snapshot, string Scope, string[] Position);
    internal string Encode(ExplorerQueryContext context, string filter, params string[] position)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new State(1, context.SnapshotId, Scope(context, filter), position));
        return Convert.ToBase64String(bytes) + "." + Convert.ToBase64String(HMACSHA256.HashData(_secret, bytes));
    }
    internal string[]? Decode(ExplorerQueryContext context, string filter, string? cursor)
    {
        if (cursor is null) return null;
        if (cursor.Length > 16384) throw new ArgumentException("invalid continuation cursor", nameof(cursor));
        try
        {
            var parts = cursor.Split('.');
            if (parts.Length != 2) throw new FormatException();
            var bytes = Convert.FromBase64String(parts[0]);
            var mac = Convert.FromBase64String(parts[1]);
            if (!CryptographicOperations.FixedTimeEquals(mac, HMACSHA256.HashData(_secret, bytes))) throw new FormatException();
            var state = JsonSerializer.Deserialize<State>(bytes);
            if (state is null || state.Version != 1 || state.Snapshot != context.SnapshotId
                || state.Scope != Scope(context, filter) || state.Position.Length > 8) throw new FormatException();
            return state.Position;
        }
        catch (Exception e) when (e is FormatException or JsonException)
        { throw new ArgumentException("invalid or incompatible continuation cursor", nameof(cursor)); }
    }
    private static string Scope(ExplorerQueryContext context, string filter)
    {
        var input = JsonSerializer.Serialize(new { version = 1, context.Capability, context.Generation, filter,
            selection = context.Selection.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new[] { x.Key, x.Value }) });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }
}
