using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CapabilityExplorer.Contracts;

namespace CapabilityExplorer.Binding;

internal static partial class ClusteredIntegrationMatching
{
    private sealed record Observation(IntegrationMatchInput Input, EdgeRecord Edge, Route Route);
    private sealed record Endpoint(IntegrationMatchInput Input, NodeRecord Node, Route Route);
    private sealed record Route(string Method, string Path, string[] Segments, string[] Versions)
    {
        internal string Signature => Method + " " + Path;
    }
    private sealed record Comparison(Observation Observation, Endpoint? Endpoint, bool Related, bool Compatible,
        IReadOnlyList<string> Differences, IReadOnlyList<string> Unknowns);
    private static readonly HashSet<string> Generic = ["health", "healthz", "ready", "readiness", "live", "liveness", "status", "ping", "swagger"];

    internal static IReadOnlyList<IntegrationMatch> Build(IReadOnlyList<IntegrationMatchInput> inputs, IReadOnlyList<IntegrationMatch> existing)
    {
        var endpoints = inputs.SelectMany(input => input.Artifact.Nodes.Where(node => node.NodeKind == "Endpoint")
            .Select(node => (Node: node, Route: Parse(node.SymbolKey))).Where(item => item.Route is not null)
            .Select(item => new Endpoint(input, item.Node, item.Route!)))
            .Where(item => Meaningful(item.Route)).GroupBy(item =>
                (item.Input.CapabilityId, item.Input.RunId, item.Input.From.RepositoryId, Service: item.Node.SymbolKey.Split('|')[1])).ToList();
        var observations = inputs.SelectMany(input => input.Artifact.Edges.Where(edge => edge.Relation == "REQUESTS" && edge.To is not null)
            .Select(edge => (Edge: edge, Route: Parse(edge.To!))).Where(item => item.Route is not null)
            .Select(item => new Observation(input, item.Edge, item.Route!))).Where(item => Meaningful(item.Route))
            .GroupBy(item => (item.Input.CapabilityId, item.Input.RunId, item.Input.From.RepositoryId, item.Input.From.UnitPath,
                Caller: Caller(item.Edge.From), Host: item.Edge.To!.Split('|')[1]));
        var result = new List<IntegrationMatch>();
        foreach (var cluster in observations)
        {
            var members = cluster.OrderBy(item => item.Edge.Id, StringComparer.Ordinal).ToList();
            if (members.Select(item => item.Route.Signature).Distinct(StringComparer.Ordinal).Count() < 2) continue;
            if (members.Any(member => existing.Any(match => match.SourceRunId == member.Input.RunId
                && match.Source.ArtifactHash == member.Input.Artifact.ContentSha256 && match.EdgeId == member.Edge.Id && match.Status == "matched"))) continue;
            var candidates = endpoints.Where(target => target.Key.CapabilityId != cluster.Key.CapabilityId
                    && target.Key.RepositoryId != cluster.Key.RepositoryId)
                .Select(target => new { Target = target, Comparisons = members.Select(member => Compare(member, target.ToList())).ToList() })
                .Where(candidate => candidate.Comparisons.Any(comparison => comparison.Related))
                .Select(candidate => new { candidate.Target, candidate.Comparisons,
                    Related = candidate.Comparisons.Where(comparison => comparison.Related).Select(comparison => comparison.Observation.Route.Signature).Distinct(StringComparer.Ordinal).Count(),
                    Compatible = candidate.Comparisons.Where(comparison => comparison.Compatible).Select(comparison => comparison.Observation.Route.Signature).Distinct(StringComparer.Ordinal).Count() })
                .OrderByDescending(candidate => candidate.Compatible).ThenByDescending(candidate => candidate.Related)
                .ThenBy(candidate => candidate.Target.Key.Service, StringComparer.Ordinal).ToList();
            if (candidates.Count == 0) continue;
            var best = candidates[0];
            var winners = candidates.Where(candidate => candidate.Compatible == best.Compatible && candidate.Related == best.Related).ToList();
            foreach (var candidate in winners)
            {
                var clusterId = Hash(string.Join('\n', IntegrationMatching.AlgorithmVersion, cluster.Key.CapabilityId, cluster.Key.RunId,
                    cluster.Key.RepositoryId, cluster.Key.UnitPath, cluster.Key.Caller, cluster.Key.Host));
                var differences = candidate.Comparisons.SelectMany(comparison => comparison.Differences).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                var unknowns = candidate.Comparisons.SelectMany(comparison => comparison.Unknowns)
                    .Append("Gateway/base URL deployment identity is not independently proven.")
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                var contract = new IntegrationContractAssessment(differences.Length > 0 ? "incompatible" : "unverified",
                    members.Select(member => member.Route.Signature).Distinct(StringComparer.Ordinal).Count(), candidate.Compatible, differences, unknowns);
                foreach (var comparison in candidate.Comparisons)
                {
                    var source = comparison.Observation;
                    var destination = comparison.Endpoint ?? candidate.Target.First();
                    var targetKey = comparison.Endpoint?.Node.SymbolKey
                        ?? destination.Input.Artifact.Nodes.FirstOrDefault(node => node.NodeKind == "Service")?.SymbolKey
                        ?? destination.Node.SymbolKey;
                    var reason = $"Clustered {members.Count} caller observations; {candidate.Compatible}/{contract.ComparedOperations} method/path operations align after gateway/version-template comparison. "
                        + $"Contract: {contract.Status}. " + (winners.Count > 1 ? "Equally supported target services remain ambiguous. " : "")
                        + (candidate.Related < 2 ? "Fewer than two distinct related operations: insufficient cluster support for a connection. " : "")
                        + "This is an inferred service association, not an adopted endpoint binding or runtime proof.";
                    result.Add(new(Hash(clusterId + "\n" + candidate.Target.Key.CapabilityId + "\n" + candidate.Target.Key.RunId + "\n" + targetKey + "\n" + source.Edge.Id),
                        source.Input.CapabilityId, destination.Input.CapabilityId, source.Input.RunId, destination.Input.RunId,
                        Source(source.Input), Source(destination.Input), source.Edge.Id, source.Edge.From, targetKey,
                        source.Edge.Relation, "http", winners.Count == 1 && candidate.Related >= 2 ? "inferred" : "suggested", reason,
                        Evidence(source.Input.Artifact, source.Edge.Evidence),
                        Evidence(destination.Input.Artifact, destination.Node.Evidence.Concat(destination.Input.Artifact.Edges
                            .Where(edge => edge.To == destination.Node.SymbolKey && edge.Relation is "HANDLES" or "EXPOSES").SelectMany(edge => edge.Evidence))),
                        ClusterId: clusterId, Contract: contract));
                }
            }
        }
        return result.OrderBy(match => match.Id, StringComparer.Ordinal).ToList();
    }

    private static Comparison Compare(Observation observation, IReadOnlyList<Endpoint> endpoints)
    {
        var related = endpoints.Select(endpoint => (Endpoint: endpoint, Exact: Suffix(observation.Route.Segments, endpoint.Route.Segments),
                Resource: Suffix(Literals(observation.Route), Literals(endpoint.Route))))
            .Where(item => item.Exact || item.Resource)
            .OrderByDescending(item => item.Exact && item.Endpoint.Route.Method == observation.Route.Method)
            .ThenByDescending(item => item.Exact).ThenByDescending(item => item.Endpoint.Route.Method == observation.Route.Method)
            .ThenByDescending(item => MethodFamily(item.Endpoint.Route.Method) == MethodFamily(observation.Route.Method))
            .ThenBy(item => item.Endpoint.Node.SymbolKey, StringComparer.Ordinal).ToList();
        var unknowns = new List<string>();
        if (related.Count == 0)
            return new(observation, null, false, false, [], ["No corresponding operation was found in the selected target endpoints."]);
        var target = related[0];
        var differences = new List<string>();
        if (observation.Route.Method != target.Endpoint.Route.Method)
            differences.Add($"HTTP method differs: {observation.Route.Method} versus {target.Endpoint.Route.Method} for {observation.Route.Path}.");
        if (!target.Exact)
            differences.Add($"Path identifier layout differs: {observation.Route.Path} versus {target.Endpoint.Route.Path}; query/path identifiers are not interchangeable.");
        var sourceVersions = observation.Route.Versions.Distinct(StringComparer.Ordinal).ToArray();
        var targetVersions = target.Endpoint.Route.Versions.Distinct(StringComparer.Ordinal).ToArray();
        if (targetVersions.Contains("{}", StringComparer.Ordinal)) unknowns.Add("Target API version is templated; supported concrete versions were not extracted.");
        else if (sourceVersions.Length > 0 && targetVersions.Length > 0 && !sourceVersions.Intersect(targetVersions, StringComparer.Ordinal).Any())
            differences.Add($"Concrete API version differs: {string.Join(',', sourceVersions)} versus {string.Join(',', targetVersions)}.");
        CompareSchema(observation.Edge.Props, target.Endpoint.Node.Props, "requestSchema", differences, unknowns);
        CompareSchema(observation.Edge.Props, target.Endpoint.Node.Props, "responseSchema", differences, unknowns);
        return new(observation, target.Endpoint, true, differences.Count == 0, differences, unknowns);
    }

    private static void CompareSchema(JsonElement? source, JsonElement? target, string name, List<string> differences, List<string> unknowns)
    {
        var left = Schema(source, name);
        var right = Schema(target, name);
        if (left is null || right is null) { unknowns.Add(name + " is not available on both sides; payload compatibility is unknown."); return; }
        if (!JsonElement.DeepEquals(left.Value, right.Value)) differences.Add(name + " differs between caller and target.");
    }

    private static JsonElement? Schema(JsonElement? props, string name)
    {
        if (props is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty(name, out var schema)) return null;
        if (schema.ValueKind == JsonValueKind.Object) return schema.Clone();
        if (schema.ValueKind != JsonValueKind.String) return null;
        try { using var document = JsonDocument.Parse(schema.GetString()!); return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null; }
        catch (JsonException) { return null; }
    }

    private static Route? Parse(string key)
    {
        var parts = key.Split('|');
        if (parts.Length != 3 || parts[0] != "http") return null;
        var separator = parts[2].IndexOf(' ');
        if (separator <= 0) return null;
        var method = parts[2][..separator];
        var path = Parameter().Replace(parts[2][(separator + 1)..].Split('?')[0], "{}").ToLowerInvariant();
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var versions = segments.Where(segment => Version().IsMatch(segment)).Select(segment => segment[1..]).ToArray();
        return new(method, path, segments.Where(segment => segment != "api" && !Version().IsMatch(segment)).ToArray(), versions);
    }

    private static string[] Literals(Route route) => route.Segments.Where(segment => segment != "{}").ToArray();
    private static string MethodFamily(string method) => method switch { "PUT" or "PATCH" => "update", "GET" or "HEAD" => "read", _ => method };
    private static bool Meaningful(Route route) => route.Segments.Any(segment => segment != "{}" && !Generic.Contains(segment));
    private static bool Suffix(string[] source, string[] target) => target.Length > 0 && source.Length >= target.Length
        && source.AsSpan(source.Length - target.Length).SequenceEqual(target);
    private static string Caller(string key)
    {
        var parts = key.Split('|');
        if (parts[0] == "ts" && parts.Length > 3) return string.Join('|', parts[..^1]);
        var member = parts[^1].Split('(')[0];
        var separator = member.LastIndexOf('.');
        return string.Join('|', parts[..^1]) + "|" + (separator > 0 ? member[..separator] : member);
    }
    private static BindingPreviewSource Source(IntegrationMatchInput input) =>
        new(input.From.RepositoryId, input.From.CommitSha, input.From.UnitPath, input.Artifact.ContentSha256);
    private static List<EvidenceRecord> Evidence(Artifact artifact, IEnumerable<string> ids)
    {
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        return artifact.Evidence.Where(item => selected.Contains(item.Id)).OrderBy(item => item.Id, StringComparer.Ordinal).ToList();
    }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    [GeneratedRegex("\\{[^}]*\\}", RegexOptions.CultureInvariant)] private static partial Regex Parameter();
    [GeneratedRegex("^v(?:[0-9]+(?:\\.[0-9]+)*|\\{\\})$", RegexOptions.CultureInvariant)] private static partial Regex Version();
}