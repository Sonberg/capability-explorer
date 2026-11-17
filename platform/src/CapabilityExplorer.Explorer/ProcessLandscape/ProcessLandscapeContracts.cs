using System.Text.Json;

namespace CapabilityExplorer.Explorer;

/// <summary>Server-configured, files-mode <c>ce.process-landscape.v1</c> source. Bound only from configuration
/// (<c>ProcessLandscape:SnapshotPath</c>, <c>ProcessLandscape:AllowModelContent</c>); never from request data.</summary>
public sealed class ProcessLandscapeOptions
{
    public string? SnapshotPath { get; set; }

    /// <summary>Deny by default: a snapshot whose <c>contentProfile</c> is <c>model-content</c> is rejected unless
    /// an operator explicitly opts in here. Structural snapshots are always accepted regardless of this flag.</summary>
    public bool AllowModelContent { get; set; }
}

public sealed record ProcessLandscapeCoverage(string Status, IReadOnlyList<string> Supported, IReadOnlyList<string> Limitations);

public sealed record ProcessLandscapeDiagnosticSource(string? Path = null, string? Element = null, string? Ref = null);

public sealed record ProcessLandscapeDiagnostic(
    string Code, string Message, string Disposition, ProcessLandscapeDiagnosticSource? Source = null);

public sealed record ProcessLandscapeElementSource(string Path, string? Element = null);

public sealed record ProcessLandscapeElementContent(string Text, string? Language = null);

public sealed record ProcessLandscapeElement(
    string Identity, string Kind, string Namespace, string Id, string Type, int Order,
    IReadOnlyDictionary<string, string> Attributes, ProcessLandscapeElementSource Source,
    string? Parent = null, ProcessLandscapeElementContent? Content = null);

public sealed record ProcessLandscapeRelationshipCondition(string? Text = null, string? Language = null);

/// <summary>Endpoints are process-local except a <c>reference</c> from a BPMN callActivity to a unique,
/// declared BPMN process root in another admitted process, matching the caller's local calledElement id.
/// Other relationship kinds cannot use this cross-process exception.</summary>
public sealed record ProcessLandscapeRelationship(
    string Identity, string Kind, string Source, string Target, int Order,
    ProcessLandscapeRelationshipCondition? Condition = null);

/// <summary>Opt-in canonical, same-file collaboration/layout graph. Process semantics remain in
/// <c>processes[]</c>; repeated process DI/DC elements are checked projections, not new owners.
/// Unresolved collaboration facts remain elements plus snapshot diagnostics, never invented edges.</summary>
public sealed record ProcessLandscapeDocument(
    string Path, IReadOnlyList<ProcessLandscapeElement> Elements,
    IReadOnlyList<ProcessLandscapeDocumentRelationship> Relationships);

/// <summary>A document-local messageFlow with its authored carrier and endpoint identities. Activities/events
/// resolve to exactly one participant in the carrier's collaboration; explicit participant endpoints name
/// their own pools. This does not authorize cross-process sequenceFlow or other process relationships.</summary>
public sealed record ProcessLandscapeDocumentRelationship(
    string Identity, string Kind, string Source, string Target, int Order, string ElementIdentity,
    ProcessLandscapeRelationshipCondition? Condition = null);

/// <summary>A single BPMN process definition. Multiple process records may repeat the same <c>Path</c>/
/// <c>Sha256</c> (one BPMN file can declare several <c>&lt;process&gt;</c> elements); no path uniqueness is
/// enforced across <c>processes[]</c>. Raw source bytes are never carried per-process: the file-level metadata
/// (and, for <c>model-content</c>, the raw bytes) for this process's file lives once in the top-level
/// <see cref="ProcessLandscapeSnapshot.Sources"/> collection (required in both profiles), joined by (<c>Path</c>,
/// <c>Sha256</c>).</summary>
public sealed record ProcessLandscapeProcess(
    string ProcessIdentity, string Path, long ByteLength, string Sha256,
    IReadOnlyList<string> ContextTags, IReadOnlyList<string> PhaseTags,
    ProcessLandscapeCoverage Coverage, IReadOnlyList<ProcessLandscapeDiagnostic> Diagnostics,
    IReadOnlyList<ProcessLandscapeElement> Elements, IReadOnlyList<ProcessLandscapeRelationship> Relationships,
    string? ProcessId = null, string? Name = null, string? ProposalStatus = null);

/// <summary>One raw-byte-record-or-metadata row per admitted BPMN file, required in <c>both</c> content profiles:
/// the final schema always carries exactly one <see cref="ProcessLandscapeSource"/> row per distinct BPMN path
/// referenced by <c>processes[]</c>. Structural snapshots must carry no <see cref="SourceBytesBase64"/> anywhere
/// in this collection (file identity only: path/length/hash); <c>model-content</c> rows must carry base64 bytes
/// whose decoded length and SHA-256 match the declared <c>ByteLength</c>/<c>Sha256</c> exactly. <c>Path</c> is
/// unique within this collection; any number of <see cref="ProcessLandscapeProcess"/> records may reference the
/// same file by matching (<c>Path</c>, <c>Sha256</c>) instead of duplicating its bytes inline, and every row must
/// be referenced by at least one process (an orphaned row is rejected).</summary>
public sealed record ProcessLandscapeSource(string Path, long ByteLength, string Sha256, string? SourceBytesBase64 = null);


public sealed record ProcessLandscapeSearchEntry(
    string Term, string Kind, string ProcessIdentity, string DeepLink, string? ElementIdentity = null);

public sealed record ProcessLandscapeEndpoint(
    string ProcessId, bool Resolved, string? ElementId = null, string? ProcessIdentity = null, string? ElementIdentity = null);

public sealed record ProcessLandscapeCrossProcessEvent(
    string EventId, ProcessLandscapeEndpoint Source, ProcessLandscapeEndpoint Target, string? Label = null);

public sealed record ProcessLandscapeLifecycleStage(
    string Stage, int Order, string ProcessId, bool Resolved,
    string? ElementId = null, string? ProcessIdentity = null, string? ElementIdentity = null);

public sealed record ProcessLandscapeObjectLifecycle(
    string ObjectId, IReadOnlyList<ProcessLandscapeLifecycleStage> Stages, string? Label = null);

public sealed record ProcessLandscapeJourneyStep(
    int Order, string ProcessId, bool Resolved,
    string? ElementId = null, string? ProcessIdentity = null, string? ElementIdentity = null);

public sealed record ProcessLandscapePhaseJourneyPhase(
    string PhaseId, int Order, IReadOnlyList<ProcessLandscapeJourneyStep> Steps, string? Label = null);

public sealed record ProcessLandscapeOverlayEntry(string Tag, IReadOnlyList<string> ProcessIdentities);

public sealed record ProcessLandscapeOverlays(
    IReadOnlyList<ProcessLandscapeOverlayEntry> Context, IReadOnlyList<ProcessLandscapeOverlayEntry> Phase);

/// <param name="Sources">Required top-level file-metadata/raw-byte collection (see
/// <see cref="ProcessLandscapeSource"/>): the final schema carries exactly one row per distinct BPMN path
/// referenced by <c>processes[]</c> in both content profiles, bytes present only for <c>model-content</c>.</param>
/// <param name="Navigator">Additive, optional pre-parsed offline navigator bundle (one generated JSON dataset per
/// navigator view). This is the producer's finalized contract: a closed set of category keys (<c>catalogue</c>,
/// <c>elements</c>, <c>links</c>, <c>events</c>, <c>lifecycle</c>, <c>journey</c>, <c>contexts</c>, <c>procFile</c>,
/// <c>dmn</c>, <c>phases</c>, <c>generated</c>) plus a <c>bundle</c> file record and a <c>verified</c> flag - any
/// other top-level member is rejected as unknown rather than tolerated. Embedded XML model content is already
/// stripped by the producer for structural snapshots, and structural validation additionally scans the whole
/// payload to reject any embedded XML/raw bytes outright. Treated the same as any other embedded model content
/// for the deny-by-default <c>contentProfile</c> gate. Known category containers, fields and reference tuple
/// shapes are validated without requiring optional fields or resolving bundle-local references; unknown nested
/// metadata is preserved, not certified. See <see cref="ProcessLandscapeValidation"/>.</param>
public sealed record ProcessLandscapeSnapshot(
    string Schema, string SnapshotId, int ManifestVersion, string RepositoryId, string ContentProfile,
    ProcessLandscapeCoverage Coverage, IReadOnlyList<ProcessLandscapeDiagnostic> Diagnostics,
    IReadOnlyList<ProcessLandscapeProcess> Processes, IReadOnlyList<ProcessLandscapeSearchEntry> SearchIndex,
    IReadOnlyList<ProcessLandscapeCrossProcessEvent> CrossProcessEvents,
    IReadOnlyList<ProcessLandscapeObjectLifecycle> ObjectLifecycle,
    IReadOnlyList<ProcessLandscapePhaseJourneyPhase> PhaseJourney, ProcessLandscapeOverlays Overlays,
    IReadOnlyList<ProcessLandscapeSource> Sources, JsonElement? Navigator = null,
    IReadOnlyList<ProcessLandscapeDocument>? Documents = null);


/// <summary>Whether the <c>snapshotId</c> could be independently reproduced from the delivered snapshot content.
/// The producer's identity preimage is keyed over every admitted source file (one record per unique manifest path,
/// including non-BPMN sidecar inputs such as cross-process-events/object-lifecycle/phase-journey feeds), but only
/// the admitted BPMN files are ever reflected back into <c>processes[]</c>. Whenever any sidecar contributed to the
/// snapshot, its path/byteLength/sha256 record is invisible to this API, so recomputation from <c>processes[]</c>
/// alone cannot reproduce the exact preimage. A <see cref="Reconstructed"/> match is a strong, cryptographically
/// bound confirmation; a mismatch is reported as <see cref="Unverifiable"/>, never silently treated as confirmed.</summary>
public enum ProcessLandscapeIdentityStatus { Reconstructed, Unverifiable }

public sealed record ProcessLandscapeIdentityVerification(ProcessLandscapeIdentityStatus Status, string Reason);

public sealed record ProcessLandscapeResponse(
    string Status, ProcessLandscapeSnapshot? Snapshot = null,
    ProcessLandscapeIdentityVerification? IdentityVerification = null, string? Reason = null)
{
    public string Schema => "ce.process-landscape.v1";
}

public sealed class ProcessLandscapeRejectedException(string status, string reason) : Exception(reason)
{
    public string Status { get; } = status;
    public string Reason { get; } = reason;
}

/// <summary>Defensive byte ceilings for the single configured snapshot file and its re-serialized response,
/// derived from the producer's manifest-wide raw-content budget (<c>MANIFEST_LIMITS.maxTotalBytes</c> in
/// <c>extractors/ts/src/models/process-landscape-manifest.ts</c>, currently 96 MiB) plus explicit, deliberately
/// modest (not merely convenient) margins for the inflation that budget does not itself bound:
/// <list type="bullet">
/// <item><see cref="MaxSourceBytesBase64"/>: exactly 4/3 inflation, since <c>model-content</c> base64-encodes
/// every admitted byte exactly once in <c>sources[]</c> (never duplicated per process);</item>
/// <item><see cref="MaxStructuralJsonBytes"/>: a 2x multiplier for the structural JSON encoding of the extracted
/// BPMN process/element/attribute/relationship/diagnostic/search-index tree that both content profiles always
/// carry. BPMN XML is verbose relative to its JSON projection, so 2x is a conservative ceiling grounded in that
/// relationship, not an arbitrary guess;</item>
/// <item><see cref="MaxNavigatorBytes"/>: one additional raw-budget margin for the additive <c>navigator</c>
/// sidecar, which re-projects comparable content (catalogue/elements/links/etc. plus an admitted bundle file)
/// under a separate structure.</item>
/// </list>
/// These are chosen so a legitimate maximum-size snapshot is never rejected solely by an undersized API cap,
/// while staying an order of magnitude away from the multi-gigabyte peaks an unexamined "generous margin" formula
/// can produce. The snapshot path itself is server-configured only (never request data), so these caps exist to
/// bound server memory against a misconfigured or corrupt file, not as a security boundary against an untrusted
/// caller. See <see cref="ApproximatePeakWorkingSetBytes"/> for the documented (not code-enforced) worst-case
/// in-memory footprint these caps imply once JSON parsing/deserialization/re-serialization overhead is accounted
/// for - operators provisioning this service should budget for roughly that much headroom per in-flight request.</summary>
public static class ProcessLandscapeSizeLimits
{
    private const int ProducerMaxRawBytes = 96 * 1024 * 1024;

    private const int MaxSourceBytesBase64 = ProducerMaxRawBytes * 4 / 3; // 128 MiB
    private const int MaxStructuralJsonBytes = ProducerMaxRawBytes * 2; // 192 MiB
    private const int MaxNavigatorBytes = ProducerMaxRawBytes; // 96 MiB

    /// <summary>Upper bound on the configured snapshot file's on-disk byte size (~416 MiB): the sum of
    /// <see cref="MaxSourceBytesBase64"/>, <see cref="MaxStructuralJsonBytes"/>, and
    /// <see cref="MaxNavigatorBytes"/>.</summary>
    public const int MaxSnapshotFileBytes = MaxSourceBytesBase64 + MaxStructuralJsonBytes + MaxNavigatorBytes;

    /// <summary>The response re-serializes the same already-validated content with no new bytes added (the
    /// parsed snapshot is echoed back, not duplicated or expanded), so this cap intentionally matches the
    /// file-read cap exactly rather than adding a further, unjustified margin on top of it.</summary>
    public const int MaxResponseBytes = MaxSnapshotFileBytes;

    /// <summary>Documented (not enforced in code) worst-case peak managed-heap footprint while handling one
    /// request: the raw file bytes held by the loader (up to <see cref="MaxSnapshotFileBytes"/>), plus the
    /// deserialized <see cref="ProcessLandscapeSnapshot"/> object graph - conservatively assumed up to 3x the raw
    /// bytes to account for UTF-16 string doubling, per-object/collection overhead, and short-lived buffers held
    /// concurrently during <c>JsonDocument</c> parsing and validation - plus the bounded output buffer in
    /// <see cref="BoundedJsonResult"/> (up to <see cref="MaxResponseBytes"/>). With the current constants this is
    /// ~1.625 GiB; that is the number to provision against, not the nominal ~416 MiB file-size cap alone.</summary>
    public const long ApproximatePeakWorkingSetBytes = MaxSnapshotFileBytes * 3L + MaxResponseBytes;
}
