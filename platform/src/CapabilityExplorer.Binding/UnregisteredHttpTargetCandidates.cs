using CapabilityExplorer.Contracts;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CapabilityExplorer.Binding;

public sealed record HttpTargetMembership(string CapabilityId, string CapabilityName, string RepositoryId);
public sealed record HttpTargetCoverage(string Status, IReadOnlyList<BindingPreviewSource> Sources,
    IReadOnlyList<string> MissingRepositories, IReadOnlyList<string> Qualifications);
public sealed record HttpTargetOccurrence(string Id, BindingPreviewSource Source, string EdgeId, string Caller,
    string? Client, string? Target, string? Operation, IReadOnlyList<EvidenceRecord> Evidence);
public sealed record UnregisteredHttpTargetCandidate(string Id, string Label, string Status,
    string RepositoryId, string UnitPath, string Caller, string? Client, string Destination,
    IReadOnlyList<HttpTargetMembership> Consumers, IReadOnlyList<string> Operations,
    IReadOnlyList<string> Reasons, IReadOnlyList<HttpEndpointEvidence> Alternatives,
    IReadOnlyList<string> MissingEvidence, HttpTargetCoverage ProviderCoverage,
    IReadOnlyList<HttpTargetOccurrence> Occurrences);
public sealed record HttpTargetOutcome(string Id, string Status, IReadOnlyList<HttpTargetOccurrence> Occurrences,
    IReadOnlyList<HttpEndpointEvidence> Alternatives, IReadOnlyList<string> MissingEvidence);
public sealed record UnregisteredHttpTargetResult(string ResultId, string RuleVersion,
    HttpTargetCoverage ProviderCoverage, IReadOnlyList<UnregisteredHttpTargetCandidate> Candidates,
    IReadOnlyList<HttpTargetOutcome> Outcomes);

/// <summary>Read-only lookup in an exact selection, not service ownership or enrollment.</summary>
public static class UnregisteredHttpTargetCandidates
{
    public const string RuleVersion = "scoped-http-target-v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static UnregisteredHttpTargetResult Build(IEnumerable<(LoadedFrom From, Artifact Artifact)> sources,
        IEnumerable<HttpTargetMembership> membership, BindingFile bindings, string ruleVersion = RuleVersion)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(membership);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleVersion);
        var inputs = sources.OrderBy(item => item.From.RepositoryId, StringComparer.Ordinal)
            .ThenBy(item => item.From.UnitPath, StringComparer.Ordinal).ToList();
        if (inputs.Any(item => item.From.RepositoryId != item.Artifact.Header.RepositoryId
                || item.From.CommitSha != item.Artifact.Header.CommitSha || item.From.UnitPath != item.Artifact.Header.Unit.Path)
            || inputs.GroupBy(item => item.From.RepositoryId).Any(group => group.Select(item => item.From.CommitSha).Distinct(StringComparer.Ordinal).Count() > 1)
            || inputs.GroupBy(item => (item.From.RepositoryId, item.From.UnitPath)).Any(group => group.Count() > 1))
            throw new ArgumentException("Select one exact revision and artifact per repository and unit.");
        var members = membership.Distinct().OrderBy(item => item.CapabilityId, StringComparer.Ordinal)
            .ThenBy(item => item.RepositoryId, StringComparer.Ordinal).ThenBy(item => item.CapabilityName, StringComparer.Ordinal).ToList();
        var registered = members.Select(item => item.RepositoryId).ToHashSet(StringComparer.Ordinal);
        var missing = registered.Except(inputs.Select(item => item.From.RepositoryId), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var providerSources = inputs.Where(item => registered.Contains(item.From.RepositoryId)).Select(Source).ToList();
        var partial = inputs.Where(item => registered.Contains(item.From.RepositoryId) && item.Artifact.Complete.Status != "full")
            .Select(item => $"{item.From.RepositoryId}/{item.From.UnitPath}: partial analysis").ToList();
        var coverage = new HttpTargetCoverage(missing.Count == 0 && partial.Count == 0 ? "full" : "partial",
            providerSources, missing, [.. partial, "Selected catalogue lookup only; not exhaustive runtime-service knowledge."]);
        var resultId = Hash(new { ruleVersion, binderRule = Binder.RuleVersion, bindings = bindings.Sha256,
            sources = inputs.Select(Source).ToList(), membership = members });
        var bound = Binder.Bind(inputs, bindings).Edges.ToLookup(edge =>
            (edge.Artifact.RepositoryId, edge.Artifact.UnitPath, edge.Original.Id));
        var endpoints = inputs.SelectMany(item => item.Artifact.Nodes.Where(node => node.NodeKind == "Endpoint" && Parts(node.SymbolKey) is not null)
            .Select(node => new HttpEndpointEvidence(Source(item), node.SymbolKey, Evidence(item.Artifact, node.Evidence))))
            .OrderBy(endpoint => endpoint.Source.RepositoryId, StringComparer.Ordinal).ThenBy(endpoint => endpoint.Source.UnitPath, StringComparer.Ordinal)
            .ThenBy(endpoint => endpoint.Key, StringComparer.Ordinal).ToList();
        var byOperation = endpoints.ToLookup(endpoint => Parts(endpoint.Key)!.Value.Operation, StringComparer.Ordinal);
        var occurrences = inputs.SelectMany(item => item.Artifact.Edges.Where(edge => edge.Relation == "REQUESTS").Select(edge =>
        {
            var parts = Parts(edge.To);
            var client = Text(edge.Props, "client") ?? Text(edge.Props, "via");
            var occurrence = new HttpTargetOccurrence("http-occurrence:" + Hash(new { source = Source(item), edge.Id }),
                Source(item), edge.Id, edge.From, client, edge.To, parts?.Operation, Evidence(item.Artifact, edge.Evidence));
            var supported = parts is { } target && Supported(item.From, item.Artifact.Header.Unit, target.Destination, target.Operation)
                && occurrence.Evidence.Count > 0
                && (edge.Resolution != "unresolved" || edge.UnresolvedReason is "configuration" or "external");
            var identity = new { item.From.RepositoryId, item.From.UnitPath, caller = edge.From, client,
                destination = parts?.Destination,
                hostOnlyOperation = (client is null || parts?.Destination.Contains(":host=", StringComparison.Ordinal) == true)
                    && HostOnly(parts?.Destination) ? parts?.Operation : null,
                unsupported = supported ? null : occurrence.Id };
            return (Occurrence: occurrence, Supported: supported, Identity: Hash(identity));
        })).OrderBy(item => item.Occurrence.Id, StringComparer.Ordinal).ToList();
        var candidates = new List<UnregisteredHttpTargetCandidate>();
        var outcomes = new List<HttpTargetOutcome>();
        foreach (var group in occurrences.GroupBy(item => item.Identity, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var calls = group.Select(item => item.Occurrence).ToList();
            var first = calls[0];
            var destination = Parts(first.Target)?.Destination;
            var maps = calls.SelectMany(call => bound[(call.Source.RepositoryId, call.Source.UnitPath, call.EdgeId)]).ToList();
            var alternatives = calls.SelectMany(call =>
                {
                    var exact = endpoints.Where(endpoint => endpoint.Key == call.Target).ToList();
                    return exact.Count > 0 ? exact : call.Operation is { } operation ? byOperation[operation] : [];
                })
                .DistinctBy(endpoint => (endpoint.Source, endpoint.Key)).OrderBy(endpoint => endpoint.Source.RepositoryId, StringComparer.Ordinal)
                .ThenBy(endpoint => endpoint.Source.UnitPath, StringComparer.Ordinal).ThenBy(endpoint => endpoint.Key, StringComparer.Ordinal).ToList();
            var status = maps.Any(map => map.Status == BindingStatus.Broken) ? "broken-binding"
                : maps.Count > 0 ? "known-provider"
                : alternatives.Select(endpoint => Parts(endpoint.Key)!.Value.Destination).Distinct(StringComparer.Ordinal).Count() > 1 ? "ambiguous-known-providers"
                : alternatives.Count > 0 ? "known-provider"
                : group.Any(item => !item.Supported) ? "dynamic/insufficient-evidence"
                : coverage.Status != "full" ? "partial-provider-coverage"
                : "no-known-provider";
            IReadOnlyList<string> gaps = ["Ownership is not established.", "Payload, query, response, headers and content-type compatibility are not established.",
                .. coverage.Status == "partial" ? new[] { "Partial provider coverage cannot establish absence." } : Array.Empty<string>(),
                .. group.Any(item => !item.Supported) ? new[] { "Scoped static destination, operation or source evidence is missing." } : Array.Empty<string>()];
            if (status == "no-known-provider")
            {
                candidates.Add(new("unregistered-http-target:" + group.Key, Label(destination!), status,
                    first.Source.RepositoryId, first.Source.UnitPath, first.Caller, first.Client, destination!,
                    members.Where(member => member.RepositoryId == first.Source.RepositoryId).ToList(),
                    calls.Select(call => call.Operation!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                    ["Exact repository, analysis unit, caller/client and destination scope.", "Distinct observed method/route operations; repeated occurrences are retained, not additional operation support.",
                        "No exact or method/route provider match in the selected evidence; no ownership is inferred."],
                    [], gaps, coverage, calls));
            }
            else
                outcomes.Add(new("http-target-outcome:" + group.Key, status, calls, alternatives, gaps));
        }
        return new(resultId, ruleVersion, coverage, candidates.OrderBy(candidate => candidate.Id, StringComparer.Ordinal).ToList(), outcomes);
    }

    private static BindingPreviewSource Source((LoadedFrom From, Artifact Artifact) item) =>
        new(item.From.RepositoryId, item.From.CommitSha, item.From.UnitPath, item.Artifact.ContentSha256);

    private static List<EvidenceRecord> Evidence(Artifact artifact, IEnumerable<string> ids)
    {
        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        return artifact.Evidence.Where(evidence => wanted.Contains(evidence.Id)).OrderBy(evidence => evidence.Id, StringComparer.Ordinal).ToList();
    }

    private static string? Text(JsonElement? props, string name) => props is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    private static (string Destination, string Operation)? Parts(string? key)
    {
        var parts = key?.Split('|');
        return parts is { Length: 3 } && parts[0] == "http" ? (parts[1], parts[2]) : null;
    }

    private static bool Supported(LoadedFrom source, UnitRef unit, string destination, string operation)
    {
        var separator = operation.IndexOf(' ', StringComparison.Ordinal);
        if (separator < 1 || !new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" }.Contains(operation[..separator], StringComparer.Ordinal)
            || !operation[(separator + 1)..].StartsWith('/')) return false;
        if (!destination.StartsWith("@unbound:", StringComparison.Ordinal)) return destination.Length > 0;
        var prefixes = new[] { $"@unbound:{source.RepositoryId}/{unit.Path}:", $"@unbound:{source.RepositoryId}/{unit.Name}:" };
        var prefix = prefixes.FirstOrDefault(value => destination.StartsWith(value, StringComparison.Ordinal));
        if (prefix is null) return false;
        var hint = destination[prefix.Length..];
        var equals = hint.IndexOf('=', StringComparison.Ordinal);
        if (equals < 1 || equals == hint.Length - 1) return false;
        if (hint[(equals + 1)..].Equals("unknown", StringComparison.OrdinalIgnoreCase)) return false;
        if (hint[..equals] == "host")
            return Uri.TryCreate("https://" + hint[(equals + 1)..], UriKind.Absolute, out var host)
                && host.Host.Length > 0 && host.UserInfo.Length == 0 && host.AbsolutePath == "/" && host.Query.Length == 0 && host.Fragment.Length == 0;
        return hint[..equals] is "config" or "env" or "client"
            || hint[..equals] is "url" or "base" && Uri.TryCreate(hint[(equals + 1)..], UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;
    }

    private static bool HostOnly(string? destination)
    {
        var equals = destination?.IndexOf('=', StringComparison.Ordinal) ?? -1;
        return destination?.Contains(":host=", StringComparison.Ordinal) == true
            || equals >= 0 && Uri.TryCreate(destination![(equals + 1)..], UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" && uri.AbsolutePath == "/";
    }

    private static string Label(string destination)
    {
        var equals = destination.IndexOf('=', StringComparison.Ordinal);
        var value = equals < 0 ? destination : destination[(equals + 1)..];
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            value = uri.Authority + (uri.AbsolutePath == "/" ? "" : uri.AbsolutePath);
        return new string(value.Where(character => !char.IsControl(character)).Take(160).ToArray());
    }

    private static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json))));
}
