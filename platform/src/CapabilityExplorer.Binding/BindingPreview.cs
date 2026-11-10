using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CapabilityExplorer.Contracts;

namespace CapabilityExplorer.Binding;

public sealed record BindingPreviewSource(string RepositoryId, string CommitSha, string UnitPath, string ArtifactHash);
public sealed record BindingPreviewOccurrence(BindingPreviewSource Source, string EdgeId, string Relation, IReadOnlyList<string> Evidence);
public sealed record BindingPreviewHint(string Hint, string? ExistingTarget, IReadOnlyList<BindingPreviewOccurrence> Occurrences);
public sealed record BindingPreviewCatalog(string Fingerprint, IReadOnlyList<BindingPreviewSource> Sources, IReadOnlyList<BindingPreviewHint> Hints, IReadOnlyList<string> Targets,
    IReadOnlyList<BindingPreviewSource>? TargetSources = null);
public sealed record BindingPreviewEntry(string Hint, string Target, string? Note, bool Reviewed);
public sealed record BindingPreviewRequest(string ExpectedFingerprint, IReadOnlyList<BindingPreviewEntry> Entries);
public sealed record BindingPreviewRow(string Hint, string Target, string State, string Message, bool Reviewed, int EdgeCount);
public sealed record BindingPreviewResult(string Fingerprint, IReadOnlyList<BindingPreviewRow> Rows, bool CanExport);
public sealed record BindingPreviewExport(int Version, string ReviewState, string SourceFingerprint, string ExistingBindingsHash, IReadOnlyList<BindingPreviewSource> Sources, IReadOnlyList<BindingEntry> Bindings,
    IReadOnlyList<BindingPreviewSource>? TargetSources = null);
public sealed class BindingPreviewStaleException() : InvalidOperationException("The source or existing bindings changed. Reload and review the current snapshot.");

/// <summary>Pure, revision-bound human binding preview. Neither this service nor its export adopts mappings.</summary>
public static class BindingPreview
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static BindingPreviewCatalog Catalog(IEnumerable<(LoadedFrom From, Artifact Artifact)> artifacts, BindingFile existing,
        IEnumerable<(LoadedFrom From, Artifact Artifact)>? destinations = null, string? context = null)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(existing);
        var list = artifacts.ToList();
        var targetList = destinations?.ToList() ?? [];
        var combined = Combine(list, targetList);
        var targetSources = targetList.Select(item => Source(item.From, item.Artifact)).Distinct()
            .OrderBy(item => item.RepositoryId, StringComparer.Ordinal).ThenBy(item => item.UnitPath, StringComparer.Ordinal).ToList();
        var sources = list.Select(x => Source(x.From, x.Artifact)).OrderBy(x => x.RepositoryId, StringComparer.Ordinal)
            .ThenBy(x => x.CommitSha, StringComparer.Ordinal).ThenBy(x => x.UnitPath, StringComparer.Ordinal).ToList();
        if (sources.Count == 0) throw new ArgumentException("No source artifacts are selected.");
        if (sources.DistinctBy(x => (x.RepositoryId, x.UnitPath)).Count() != sources.Count)
            throw new ArgumentException("Select one exact artifact per repository and unit.");
        var previous = existing.Entries.ToDictionary(x => x.Hint, StringComparer.Ordinal);
        var hints = list.SelectMany(x => x.Artifact.Edges.Where(e => e.To is not null).SelectMany(e =>
                e.To!.Split('|').Where(s => s.StartsWith("@unbound:", StringComparison.Ordinal)).Select(h =>
                    (Hint: h, Occurrence: new BindingPreviewOccurrence(Source(x.From, x.Artifact), e.Id, e.Relation, e.Evidence)))))
            .GroupBy(x => x.Hint, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new BindingPreviewHint(g.Key, previous.GetValueOrDefault(g.Key)?.Target,
                g.Select(x => x.Occurrence).OrderBy(x => x.Source.RepositoryId, StringComparer.Ordinal)
                    .ThenBy(x => x.Source.UnitPath, StringComparer.Ordinal).ThenBy(x => x.EdgeId, StringComparer.Ordinal).ToList())).ToList();
        if (hints.Count > 1000) throw new ArgumentException("Too many hints; select a smaller capability.");
        // Only HTTP Endpoint declarations establish a service-ref suggestion. SQL object keys
        // do not establish database resources; chan/store use different segment positions.
        // Named resources remain explicit manual assertions until a typed resource adapter exists.
        var targets = combined.SelectMany(x => x.Artifact.Nodes).Where(n => n.NodeKind == "Endpoint").Select(n => n.SymbolKey.Split('|'))
            .Where(p => p.Length == 3 && p[0] == "http" && !p[1].StartsWith('@'))
            .Select(p => p[1]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var fingerprint = Hash(JsonSerializer.Serialize(new { Sources = sources, TargetSources = targetSources, ExistingBindings = existing.Sha256, Context = context }, Json));
        return new(fingerprint, sources, hints, targets, targetSources);
    }

    public static BindingPreviewResult Preview(IEnumerable<(LoadedFrom From, Artifact Artifact)> artifacts, BindingFile existing, BindingPreviewRequest request,
        IEnumerable<(LoadedFrom From, Artifact Artifact)>? destinations = null, string? context = null)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(request);
        var list = artifacts.ToList();
        var targetList = destinations?.ToList() ?? [];
        var catalog = Catalog(list, existing, targetList, context);
        if (!string.Equals(catalog.Fingerprint, request.ExpectedFingerprint, StringComparison.Ordinal)) throw new BindingPreviewStaleException();
        if (request.Entries is null || request.Entries.Count is < 1 or > 200) throw new ArgumentException("Preview between 1 and 200 explicit mappings.");
        var hints = catalog.Hints.ToDictionary(x => x.Hint, StringComparer.Ordinal);
        var duplicate = request.Entries.Where(x => x is not null).GroupBy(x => x.Hint, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var rows = new List<BindingPreviewRow>();
        foreach (var entry in request.Entries)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Hint) || !hints.TryGetValue(entry.Hint, out var hint))
                throw new ArgumentException("Every mapping must reference a hint in the selected source snapshot.");
            if (string.IsNullOrWhiteSpace(entry.Target) || entry.Target.Length > 512 || entry.Target.Any(char.IsControl)
                || entry.Target.Any(char.IsWhiteSpace) || entry.Target.Contains('|', StringComparison.Ordinal) || entry.Target.StartsWith('@')
                || entry.Target.Contains('?', StringComparison.Ordinal) || entry.Target.Contains('#', StringComparison.Ordinal)
                || entry.Target.Contains('@', StringComparison.Ordinal) || entry.Target.Contains("://", StringComparison.Ordinal)
                || entry.Target.Any(c => !char.IsLetterOrDigit(c) && c is not ('.' or '-' or '_' or '/' or ':'))
                || entry.Target.Split('/').Any(segment => segment is "" or "." or "..") || entry.Note?.Length > 2000)
                throw new ArgumentException("Use a concrete resource name or service reference without whitespace, credentials, URL queries or key delimiters.");
            if (duplicate.Contains(entry.Hint) || hint.ExistingTarget is { } old && old != entry.Target)
            {
                rows.Add(new(entry.Hint, entry.Target, "conflicting", "Duplicate proposal or a different mapping already exists. Resolve the conflict before export.", entry.Reviewed, 0));
                continue;
            }
            var map = BindingFile.Parse(JsonSerializer.Serialize(new { version = 1, bindings = new[] { new BindingEntry(entry.Hint, entry.Target, entry.Note) } }, Json));
            var bound = Binder.Bind(Combine(list, targetList), map).Edges.Where(edge => list.Any(item =>
                item.From.RepositoryId == edge.Artifact.RepositoryId && item.From.CommitSha == edge.Artifact.CommitSha && item.From.UnitPath == edge.Artifact.UnitPath)).ToList();
            var uncertain = bound.Count == 0 || bound.Any(x => x.Status == BindingStatus.Broken);
            var external = bound.Any(x => x.Original.Relation != "REQUESTS");
            rows.Add(new(entry.Hint, entry.Target, uncertain ? "uncertain" : external ? "external" : "evidenced",
                uncertain ? "A requested target is not declared in the selected revisions. Export is blocked."
                    : external ? "Named resource is a human assertion; existence outside the selected artifacts is not verified."
                    : "The requested endpoint is declared. The connection remains a human assertion, not a discovered integration.", entry.Reviewed, bound.Count));
        }
        rows.Sort((a, b) => string.CompareOrdinal(a.Hint, b.Hint));
        return new(catalog.Fingerprint, rows, rows.All(x => x.Reviewed && x.State is "evidenced" or "external"));
    }

    public static BindingPreviewExport Export(IEnumerable<(LoadedFrom From, Artifact Artifact)> artifacts, BindingFile existing, BindingPreviewRequest request,
        IEnumerable<(LoadedFrom From, Artifact Artifact)>? destinations = null, string? context = null)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        var list = artifacts.ToList();
        var targetList = destinations?.ToList() ?? [];
        var preview = Preview(list, existing, request, targetList, context);
        if (!preview.CanExport) throw new ArgumentException("Review every mapping and resolve uncertain or conflicting targets before export.");
        var additions = request.Entries.ToDictionary(x => x.Hint, StringComparer.Ordinal);
        var merged = existing.Entries.Where(x => !additions.ContainsKey(x.Hint))
            .Concat(request.Entries.Select(x => new BindingEntry(x.Hint, x.Target, x.Note))).OrderBy(x => x.Hint, StringComparer.Ordinal).ToList();
        var catalog = Catalog(list, existing, targetList, context);
        return new(1, "reviewed-export-not-adopted", preview.Fingerprint, existing.Sha256, catalog.Sources, merged, catalog.TargetSources);
    }

    private static List<(LoadedFrom From, Artifact Artifact)> Combine(List<(LoadedFrom From, Artifact Artifact)> sources, List<(LoadedFrom From, Artifact Artifact)> targets)
    {
        var all = sources.Concat(targets).ToList();
        ValidateSources(all);
        return all.DistinctBy(item => (item.From.RepositoryId, item.From.UnitPath)).ToList();
    }

    internal static void ValidateSources(IReadOnlyList<(LoadedFrom From, Artifact Artifact)> all)
    {
        foreach (var item in all) _ = Source(item.From, item.Artifact);
        if (all.GroupBy(item => item.From.RepositoryId).Any(group => group.Select(item => item.From.CommitSha).Distinct(StringComparer.Ordinal).Count() > 1)
            || all.GroupBy(item => (item.From.RepositoryId, item.From.UnitPath)).Any(group => group.Select(item => item.Artifact.ContentSha256).Distinct(StringComparer.Ordinal).Count() > 1))
            throw new ArgumentException("Select one exact revision and artifact per repository and unit.");
    }

    private static BindingPreviewSource Source(LoadedFrom from, Artifact artifact)
    {
        if (from.RepositoryId != artifact.Header.RepositoryId || from.CommitSha != artifact.Header.CommitSha || from.UnitPath != artifact.Header.Unit.Path)
            throw new ArgumentException("Artifact provenance does not match its source identity.");
        return new(from.RepositoryId, from.CommitSha, from.UnitPath, artifact.ContentSha256);
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
