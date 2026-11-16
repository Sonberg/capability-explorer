using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CapabilityExplorer.Explorer;

/// <summary>Structural and semantic validation of a parsed <see cref="ProcessLandscapeSnapshot"/>, beyond what
/// strict JSON deserialization already enforces (required members, exact member set). Throws
/// <see cref="ProcessLandscapeRejectedException"/> with <c>Status</c> <c>"invalid"</c> (malformed/unsupported
/// content) or <c>"denied"</c> (model-content carried without server opt-in) on the first violation found.</summary>
public static partial class ProcessLandscapeValidation
{
    private const string Schema = "ce.process-landscape.v1";
    private const int ManifestVersion = 1;
    private const int MaxTag = 512;

    [GeneratedRegex(@"^landscape:[a-f0-9]{64}$")] private static partial Regex SnapshotIdPattern();
    [GeneratedRegex(@"^landscape-process:[a-f0-9]{64}$")] private static partial Regex ProcessIdentityPattern();
    [GeneratedRegex(@"^landscape-element:[a-f0-9]{64}$")] private static partial Regex ElementIdentityPattern();
    [GeneratedRegex(@"^landscape-relationship:[a-f0-9]{64}$")] private static partial Regex RelationshipIdentityPattern();
    [GeneratedRegex(@"^[a-f0-9]{64}$")] private static partial Regex Sha256Pattern();

    public static void Validate(ProcessLandscapeSnapshot snapshot, bool allowModelContent)
    {
        if (snapshot.Schema != Schema) Reject("invalid", "unsupported-schema");
        if (snapshot.ManifestVersion != ManifestVersion) Reject("invalid", "unsupported-manifest-version");
        if (snapshot.RepositoryId is not { Length: > 0 and <= MaxTag }) Reject("invalid", "invalid-repository-id");
        if (snapshot.ContentProfile is not ("structural" or "model-content")) Reject("invalid", "invalid-content-profile");
        // Deny by default: a model-content snapshot is rejected before any of its embedded bytes are inspected further.
        if (snapshot.ContentProfile == "model-content" && !allowModelContent) Reject("denied", "model-content-disabled");
        if (!SnapshotIdPattern().IsMatch(snapshot.SnapshotId)) Reject("invalid", "invalid-snapshot-id");

        ValidateCoverage(snapshot.Coverage);
        foreach (var diagnosticOrNull in snapshot.Diagnostics) ValidateDiagnostic(RequireItem(diagnosticOrNull, "invalid-diagnostic"));

        var sourcesByPath = ValidateSources(snapshot.Sources, snapshot.ContentProfile);
        ValidateNavigator(snapshot.Navigator, snapshot.ContentProfile);

        var processIdentities = new HashSet<string>(StringComparer.Ordinal);
        var referencedSourcePaths = new HashSet<string>(StringComparer.Ordinal);
        var processes = new List<ProcessLandscapeProcess>();
        foreach (var processOrNull in snapshot.Processes)
        {
            var process = RequireItem(processOrNull, "invalid-process");
            ValidateProcess(process, processIdentities, sourcesByPath, referencedSourcePaths);
            processes.Add(process);
        }
        // sources[] is required in both profiles and is the single source of truth for file identity: every row
        // must be joined by at least one process (a row nothing references could only ever be a dead/unverifiable
        // record, and in model-content specifically, unreferenced raw bytes), in both content profiles.
        foreach (var path in sourcesByPath.Keys)
        {
            if (!referencedSourcePaths.Contains(path)) Reject("invalid", "unreferenced-source");
        }


        // Elements/processes were already null-checked above during ValidateProcess; this just re-indexes them.
        var knownElementsByProcess = processes.ToDictionary(
            p => p.ProcessIdentity, p => p.Elements.Select(e => RequireItem(e, "invalid-element").Identity).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var entryOrNull in snapshot.SearchIndex) ValidateSearchEntry(RequireItem(entryOrNull, "invalid-search-entry"), processIdentities, knownElementsByProcess);
        foreach (var eventOrNull in snapshot.CrossProcessEvents)
        {
            var @event = RequireItem(eventOrNull, "invalid-cross-process-event");
            ValidateEndpoint(RequireItem(@event.Source, "invalid-cross-process-event-endpoint"), processIdentities, knownElementsByProcess);
            ValidateEndpoint(RequireItem(@event.Target, "invalid-cross-process-event-endpoint"), processIdentities, knownElementsByProcess);
            if (@event.EventId.Length is 0) Reject("invalid", "invalid-cross-process-event");
        }
        foreach (var lifecycleOrNull in snapshot.ObjectLifecycle)
        {
            var lifecycle = RequireItem(lifecycleOrNull, "invalid-object-lifecycle");
            if (lifecycle.ObjectId.Length is 0) Reject("invalid", "invalid-object-lifecycle");
            foreach (var stageOrNull in lifecycle.Stages)
            {
                var stage = RequireItem(stageOrNull, "invalid-object-lifecycle-stage");
                if (stage.Stage.Length is 0 || stage.Order < 0 || stage.ProcessId.Length is 0) Reject("invalid", "invalid-object-lifecycle-stage");
                ValidateEndpointLike(stage.Resolved, stage.ProcessIdentity, stage.ElementIdentity, processIdentities, knownElementsByProcess);
            }
        }
        foreach (var phaseOrNull in snapshot.PhaseJourney)
        {
            var phase = RequireItem(phaseOrNull, "invalid-phase-journey-phase");
            if (phase.PhaseId.Length is 0 || phase.Order < 0) Reject("invalid", "invalid-phase-journey-phase");
            foreach (var stepOrNull in phase.Steps)
            {
                var step = RequireItem(stepOrNull, "invalid-phase-journey-step");
                if (step.Order < 0 || step.ProcessId.Length is 0) Reject("invalid", "invalid-phase-journey-step");
                ValidateEndpointLike(step.Resolved, step.ProcessIdentity, step.ElementIdentity, processIdentities, knownElementsByProcess);
            }
        }
        ValidateOverlays(snapshot.Overlays.Context, processIdentities);
        ValidateOverlays(snapshot.Overlays.Phase, processIdentities);
    }

    private static void ValidateCoverage(ProcessLandscapeCoverage coverage)
    {
        if (coverage.Status is not ("full" or "partial")) Reject("invalid", "invalid-coverage-status");
        if (coverage.Supported is null || coverage.Limitations is null) Reject("invalid", "invalid-coverage-lists");
    }

    private static void ValidateDiagnostic(ProcessLandscapeDiagnostic diagnostic)
    {
        if (diagnostic.Code.Length is 0) Reject("invalid", "invalid-diagnostic-code");
        if (diagnostic.Disposition is not ("partial" or "unresolved")) Reject("invalid", "invalid-diagnostic-disposition");
    }

    /// <summary>Validates the required top-level file-metadata/raw-byte collection and returns it indexed by path
    /// for process-level join checks. <c>sources[]</c> is required in <c>both</c> content profiles: structural
    /// rows must carry no bytes anywhere in this collection (file identity only), model-content rows (already
    /// gated by the deny-by-default check above) must decode cleanly and every entry's declared length/hash must
    /// match its own bytes. <c>Path</c> must be unique: this collection represents one record per distinct
    /// admitted file, not per process - <see cref="ValidateProcess"/> enforces that every process joins exactly
    /// one row here by (<c>Path</c>, <c>ByteLength</c>, <c>Sha256</c>), and the caller enforces that every row is
    /// referenced by at least one process.</summary>
    private static Dictionary<string, (long ByteLength, string Sha256, byte[]? Bytes)> ValidateSources(
        IReadOnlyList<ProcessLandscapeSource> sources, string contentProfile)
    {
        var byPath = new Dictionary<string, (long ByteLength, string Sha256, byte[]? Bytes)>(StringComparer.Ordinal);
        foreach (var sourceOrNull in sources)
        {
            var source = RequireItem(sourceOrNull, "invalid-source");
            if (!IsSafeRelativePath(source.Path)) Reject("invalid", "unsafe-source-path");
            if (source.ByteLength < 0) Reject("invalid", "invalid-source-byte-length");
            if (!Sha256Pattern().IsMatch(source.Sha256)) Reject("invalid", "invalid-source-sha256");
            if (!byPath.TryAdd(source.Path, default)) Reject("invalid", "duplicate-source-path");

            byte[]? bytes = null;
            if (contentProfile == "structural")
            {
                if (source.SourceBytesBase64 is not null) Reject("invalid", "structural-source-bytes");
            }
            else
            {
                if (source.SourceBytesBase64 is null) Reject("invalid", "missing-source-bytes");
                try { bytes = Convert.FromBase64String(source.SourceBytesBase64!); }
                catch (FormatException) { Reject("invalid", "invalid-source-bytes-encoding"); throw; }
                if (bytes.LongLength != source.ByteLength) Reject("invalid", "source-bytes-length-mismatch");
                var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
                if (!string.Equals(digest, source.Sha256, StringComparison.Ordinal)) Reject("invalid", "source-bytes-hash-mismatch");
            }
            byPath[source.Path] = (source.ByteLength, source.Sha256, bytes);
        }
        return byPath;
    }

    /// <summary>Validates the navigator sidecar against the producer's final schema: when present, it must carry
    /// a valid <c>bundle</c> file record (<c>path</c>/<c>byteLength</c>/<c>sha256</c>, describing the navigator's
    /// own admitted file so <see cref="ProcessLandscapeIdentity"/> can fold it into the snapshotId reconstruction)
    /// and a boolean <c>verified</c> flag, and every other top-level member must be one of the final producer
    /// category keys (<see cref="NavigatorCategoryKeys"/>) - any other key is rejected as unknown rather than
    /// silently accepted. Both content profiles may carry a navigator, but only as much as the top-level
    /// <c>contentProfile</c> permits: a <c>structural</c> snapshot must receive the navigator with embedded
    /// XML/raw model bytes already stripped by the producer, never the full embedded-content bundle. The exact
    /// internal shape of each category is not independently re-validated here (the producer owns it), so
    /// structural mode additionally walks the whole payload and rejects anything that looks like embedded model
    /// content: a string value that is itself an XML/markup fragment, or a property name that names a
    /// raw/base64/XML carrier. <c>model-content</c> (already gated by the deny-by-default check above) performs
    /// no such scan, since embedding XML there is expected.</summary>
    private static readonly string[] NavigatorCategoryKeys =
        ["catalogue", "elements", "links", "events", "lifecycle", "journey", "contexts", "procFile", "dmn", "phases", "generated"];

    private static void ValidateNavigator(JsonElement? navigator, string contentProfile)
    {
        if (navigator is not { } value) return;
        if (value.ValueKind != JsonValueKind.Object) Reject("invalid", "invalid-navigator-payload");

        foreach (var property in value.EnumerateObject())
        {
            if (property.Name is not ("bundle" or "verified") &&
                !NavigatorCategoryKeys.Contains(property.Name, StringComparer.Ordinal))
                Reject("invalid", "unknown-navigator-key");
        }

        if (!value.TryGetProperty("bundle", out var bundle)) Reject("invalid", "missing-navigator-bundle");
        if (bundle.ValueKind != JsonValueKind.Object) Reject("invalid", "invalid-navigator-bundle");
        if (!bundle.TryGetProperty("path", out var pathProperty) || pathProperty.ValueKind != JsonValueKind.String ||
            !IsSafeRelativePath(pathProperty.GetString() ?? ""))
            Reject("invalid", "invalid-navigator-bundle-path");
        if (!bundle.TryGetProperty("byteLength", out var byteLengthProperty) ||
            byteLengthProperty.ValueKind != JsonValueKind.Number ||
            !byteLengthProperty.TryGetInt64(out var byteLength) || byteLength < 0)
            Reject("invalid", "invalid-navigator-bundle-byte-length");
        if (!bundle.TryGetProperty("sha256", out var sha256Property) || sha256Property.ValueKind != JsonValueKind.String ||
            !Sha256Pattern().IsMatch(sha256Property.GetString() ?? ""))
            Reject("invalid", "invalid-navigator-bundle-sha256");

        if (!value.TryGetProperty("verified", out var verifiedProperty) ||
            verifiedProperty.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            Reject("invalid", "invalid-navigator-verified");

        if (contentProfile == "structural") RejectEmbeddedModelContent(value);
    }


    private static readonly string[] EmbeddedContentKeywords = ["xml", "base64", "rawbytes", "sourcebytes"];

    private static void RejectEmbeddedModelContent(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (EmbeddedContentKeywords.Any(keyword => property.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                        Reject("invalid", "structural-navigator-embedded-content");
                    RejectEmbeddedModelContent(property.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) RejectEmbeddedModelContent(item);
                break;
            case JsonValueKind.String:
                var text = element.GetString();
                if (text is { Length: > 0 } && text.AsSpan().TrimStart().StartsWith("<", StringComparison.Ordinal))
                    Reject("invalid", "structural-navigator-embedded-content");
                break;
        }
    }

    private static void ValidateProcess(
        ProcessLandscapeProcess process, HashSet<string> processIdentities,
        Dictionary<string, (long ByteLength, string Sha256, byte[]? Bytes)> sourcesByPath,
        HashSet<string> referencedSourcePaths)
    {
        if (!ProcessIdentityPattern().IsMatch(process.ProcessIdentity)) Reject("invalid", "invalid-process-identity");
        if (!processIdentities.Add(process.ProcessIdentity)) Reject("invalid", "duplicate-process-identity");
        if (!IsSafeRelativePath(process.Path)) Reject("invalid", "unsafe-process-path");
        if (process.ByteLength < 0) Reject("invalid", "invalid-process-byte-length");
        if (!Sha256Pattern().IsMatch(process.Sha256)) Reject("invalid", "invalid-process-sha256");
        if (process.ProposalStatus is not (null or "draft" or "proposed" or "approved")) Reject("invalid", "invalid-proposal-status");
        if (process.ContextTags.Any(tag => tag is null || tag.Length is 0) || process.PhaseTags.Any(tag => tag is null || tag.Length is 0))
            Reject("invalid", "invalid-process-tags");
        ValidateCoverage(process.Coverage);
        foreach (var diagnosticOrNull in process.Diagnostics) ValidateDiagnostic(RequireItem(diagnosticOrNull, "invalid-diagnostic"));

        var elementIdentities = new HashSet<string>(StringComparer.Ordinal);
        var elements = process.Elements.Select(e => RequireItem(e, "invalid-element")).ToList();
        foreach (var element in elements) ValidateElement(element, process.Path, elementIdentities);
        foreach (var element in elements)
        {
            if (element.Parent is { } parent && !elementIdentities.Contains(parent))
                Reject("invalid", "dangling-element-parent");
        }
        foreach (var relationshipOrNull in process.Relationships) ValidateRelationship(RequireItem(relationshipOrNull, "invalid-relationship"), elementIdentities);

        // Every process joins exactly one sources[] row (the final schema requires sources[] in both profiles,
        // one row per distinct admitted BPMN path): a process whose path has no matching row, or whose declared
        // (byteLength, sha256) disagrees with that row, is rejected outright. One BPMN file may legitimately
        // declare several <process> definitions, so distinct processes sharing a path/hash all join the same row.
        if (!sourcesByPath.TryGetValue(process.Path, out var source)) Reject("invalid", "process-missing-source-reference");
        if (source.ByteLength != process.ByteLength || source.Sha256 != process.Sha256)
            Reject("invalid", "process-source-mismatch");
        referencedSourcePaths.Add(process.Path);
    }


    private static void ValidateElement(ProcessLandscapeElement element, string ownerProcessPath, HashSet<string> elementIdentities)
    {
        if (!ElementIdentityPattern().IsMatch(element.Identity)) Reject("invalid", "invalid-element-identity");
        if (!elementIdentities.Add(element.Identity)) Reject("invalid", "duplicate-element-identity");
        if (element.Kind is not ("bpmn" or "opaqueExtension" or "unsupported")) Reject("invalid", "invalid-element-kind");
        if (element.Id.Length is 0 || element.Type.Length is 0) Reject("invalid", "invalid-element-id-or-type");
        if (element.Order < 0) Reject("invalid", "invalid-element-order");
        if (element.Parent is { } parent && !ElementIdentityPattern().IsMatch(parent)) Reject("invalid", "invalid-element-parent");
        if (!IsSafeRelativePath(element.Source.Path)) Reject("invalid", "unsafe-element-source-path");
        // The producer always extracts every element's source from the single BPMN file that declared its owning
        // process, and always stamps the element's own in-file id back onto source.element (see
        // extract-semantics/process-landscape-xml.ts: `source: { path, element: id }`). A mismatch here means the
        // element claims to come from a file other than the one it is nested under, or from a different
        // identifier than its own - never a legitimate cross-file/cross-element reference.
        if (element.Source.Path != ownerProcessPath) Reject("invalid", "element-source-path-mismatch");
        if (element.Source.Element is not { Length: > 0 } sourceElement || sourceElement != element.Id)
            Reject("invalid", "element-source-element-mismatch");
    }

    private static void ValidateRelationship(ProcessLandscapeRelationship relationship, HashSet<string> elementIdentities)
    {
        if (!RelationshipIdentityPattern().IsMatch(relationship.Identity)) Reject("invalid", "invalid-relationship-identity");
        if (relationship.Kind is not ("sequenceFlow" or "messageFlow" or "association" or "reference"))
            Reject("invalid", "invalid-relationship-kind");
        if (!ElementIdentityPattern().IsMatch(relationship.Source) || !elementIdentities.Contains(relationship.Source))
            Reject("invalid", "dangling-relationship-source");
        if (!ElementIdentityPattern().IsMatch(relationship.Target) || !elementIdentities.Contains(relationship.Target))
            Reject("invalid", "dangling-relationship-target");
        if (relationship.Order < 0) Reject("invalid", "invalid-relationship-order");
    }

    private static void ValidateSearchEntry(
        ProcessLandscapeSearchEntry entry, HashSet<string> processIdentities,
        Dictionary<string, HashSet<string>> knownElementsByProcess)
    {
        if (entry.Term.Length is 0 || entry.DeepLink.Length is 0) Reject("invalid", "invalid-search-entry");
        if (entry.Kind is not ("process" or "element")) Reject("invalid", "invalid-search-entry-kind");
        if (!processIdentities.Contains(entry.ProcessIdentity)) Reject("invalid", "dangling-search-process-reference");
        if (entry.ElementIdentity is { } elementIdentity &&
            !knownElementsByProcess[entry.ProcessIdentity].Contains(elementIdentity))
            Reject("invalid", "dangling-search-element-reference");
    }

    /// <summary>An endpoint/stage/step may legitimately report <c>resolved: false</c> with no identity at all
    /// (an unresolved cross-reference). Only a claimed-resolved reference is checked against the declared
    /// processes/elements, so a resolved reference can never point at something that does not exist.</summary>
    private static void ValidateEndpoint(
        ProcessLandscapeEndpoint endpoint, HashSet<string> processIdentities,
        Dictionary<string, HashSet<string>> knownElementsByProcess)
    {
        if (endpoint.ProcessId.Length is 0) Reject("invalid", "invalid-endpoint-process-id");
        ValidateEndpointLike(endpoint.Resolved, endpoint.ProcessIdentity, endpoint.ElementIdentity, processIdentities, knownElementsByProcess);
    }

    private static void ValidateEndpointLike(
        bool resolved, string? processIdentity, string? elementIdentity,
        HashSet<string> processIdentities, Dictionary<string, HashSet<string>> knownElementsByProcess)
    {
        if (!resolved) return;
        if (processIdentity is null || !processIdentities.Contains(processIdentity))
            Reject("invalid", "resolved-reference-missing-process");
        if (elementIdentity is not null && !knownElementsByProcess[processIdentity!].Contains(elementIdentity))
            Reject("invalid", "resolved-reference-missing-element");
    }

    private static void ValidateOverlays(IReadOnlyList<ProcessLandscapeOverlayEntry> entries, HashSet<string> processIdentities)
    {
        foreach (var entryOrNull in entries)
        {
            var entry = RequireItem(entryOrNull, "invalid-overlay-entry");
            if (entry.Tag.Length is 0) Reject("invalid", "invalid-overlay-tag");
            foreach (var identity in entry.ProcessIdentities)
            {
                if (identity is null || !ProcessIdentityPattern().IsMatch(identity) || !processIdentities.Contains(identity))
                    Reject("invalid", "dangling-overlay-process-reference");
            }
        }
    }

    internal static bool IsSafeRelativePath(string path) =>
        path.Length is > 0 and <= 1024 &&
        !Path.IsPathRooted(path) &&
        !path.Contains('\\') &&
        !path.Contains(':') &&
        !path.Any(char.IsControl) &&
        path.Split('/').All(part => part is not ("" or "." or ".."));

    private static void Reject(string status, string reason) => throw new ProcessLandscapeRejectedException(status, reason);

    /// <summary>Guards against an explicit JSON <c>null</c> item inside an otherwise-present collection (for
    /// example <c>processes: [null]</c> or <c>sources: [null]</c>). <see cref="JsonSerializerOptions.RespectNullableAnnotations"/>
    /// (set on the loader) already rejects a whole field being <c>null</c>, but it cannot distinguish a generic
    /// collection's element-type nullability, so individual <c>null</c> array items still deserialize
    /// successfully and must be screened here before the item is dereferenced.</summary>
    private static T RequireItem<T>(T? item, string reason) where T : class =>
        item ?? throw new ProcessLandscapeRejectedException("invalid", reason);
}
