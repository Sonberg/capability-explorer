using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CapabilityExplorer.Explorer;

/// <summary>Recomputes the producer's <c>snapshotId</c> canonicalization from the delivered snapshot and reports
/// whether the identity could be independently reconstructed. See <see cref="ProcessLandscapeIdentityStatus"/> for
/// why a mismatch is reported, not rejected: the preimage is keyed over every admitted source file including
/// sidecar feeds (process-metadata/cross-process-events/object-lifecycle/phase-journey) that never appear in the
/// delivered snapshot, so this can only ever prove a match, never prove a mismatch is wrong.
///
/// Producer formula (extractors/ts/src/models/process-landscape.ts), reproduced exactly for the file records this
/// API can see:
///   sortedRecords = distinct admitted files by path, each "{path}\0{byteLength}\0{sha256}", sorted ordinally by path
///   preimage = [schema, repositoryId, ...sortedRecords].join("\n")
///   snapshotId = "landscape:" + sha256Hex(utf8(preimage))
/// One BPMN file may declare several &lt;process&gt; definitions, so distinct processes legitimately share one
/// path/byteLength/sha256; the producer still admits that file (and therefore contributes its file record) exactly
/// once, so this reconstruction de-duplicates by path before hashing rather than hashing one record per process.
///
/// When the snapshot carries a <c>navigator.bundle</c> record (<see cref="ProcessLandscapeSnapshot.Navigator"/>,
/// already shape-validated by <see cref="ProcessLandscapeValidation"/>), it is folded in too: the navigator bundle
/// is itself an admitted file, so if the producer admits it the same way as every other sidecar (via its shared
/// <c>admit()</c> helper) its path/byteLength/sha256 record contributes to the real preimage exactly like any
/// other. This is the one additional admitted-file record this API can ever observe beyond <c>processes[]</c> -
/// every other sidecar (process-metadata/cross-process-events/object-lifecycle/phase-journey) remains invisible.
///
/// If the bundle's <c>path</c> collides with a path already admitted via <c>processes[]</c>/<c>sources[]</c>, that
/// is only ever legitimate when it describes the exact same file (identical byteLength/sha256) - a BPMN file and
/// the navigator bundle can never legitimately share one path with different content. A colliding-but-identical
/// record is treated as the one shared admitted file (no duplicate preimage entry, still counted as "contributed"
/// for the reason text below); a colliding-but-different record is an inconsistent duplicate and is reported
/// <see cref="ProcessLandscapeIdentityStatus.Unverifiable"/> immediately, never folded into a (possibly
/// coincidentally matching) reconstruction attempt.</summary>
public static class ProcessLandscapeIdentity
{
    public static ProcessLandscapeIdentityVerification Verify(ProcessLandscapeSnapshot snapshot)
    {
        var byPath = new Dictionary<string, (long ByteLength, string Sha256)>(StringComparer.Ordinal);
        foreach (var process in snapshot.Processes) byPath.TryAdd(process.Path, (process.ByteLength, process.Sha256));

        var navigatorBundleContributed = false;
        if (snapshot.Navigator is { } navigator && navigator.TryGetProperty("bundle", out var bundle))
        {
            var path = bundle.GetProperty("path").GetString()!;
            var byteLength = bundle.GetProperty("byteLength").GetInt64();
            var sha256 = bundle.GetProperty("sha256").GetString()!;
            if (byPath.TryGetValue(path, out var existing))
            {
                if (existing.ByteLength != byteLength || existing.Sha256 != sha256)
                {
                    // The navigator bundle claims the same path as an already-admitted file but disagrees on its
                    // length/hash: an inconsistent duplicate record. Never attempt a reconstruction in this state -
                    // it could at best coincidentally match, and at worst mask a tampered/corrupted bundle.
                    return new(ProcessLandscapeIdentityStatus.Unverifiable,
                        "navigator-bundle-path-conflicts-with-an-admitted-file");
                }
                // Same single admitted file observed twice (once via processes[]/sources[], once as the
                // navigator's own bundle record): it contributes exactly once to the preimage either way.
                navigatorBundleContributed = true;
            }
            else
            {
                byPath[path] = (byteLength, sha256);
                navigatorBundleContributed = true;
            }
        }

        var records = byPath
            .Select(pair => $"{pair.Key}\0{pair.Value.ByteLength}\0{pair.Value.Sha256}")
            .OrderBy(record => record, StringComparer.Ordinal)
            .ToArray();
        var preimage = string.Join('\n', new[] { snapshot.Schema, snapshot.RepositoryId }.Concat(records));
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(preimage)));
        var reconstructed = "landscape:" + digest;

        return reconstructed == snapshot.SnapshotId
            ? new(ProcessLandscapeIdentityStatus.Reconstructed, navigatorBundleContributed
                ? "snapshot-id-matches-admitted-bpmn-files-and-navigator-bundle"
                : "snapshot-id-matches-admitted-bpmn-files")
            : new(ProcessLandscapeIdentityStatus.Unverifiable, "sidecar-file-records-are-not-exposed-in-the-snapshot-contract");
    }
}
