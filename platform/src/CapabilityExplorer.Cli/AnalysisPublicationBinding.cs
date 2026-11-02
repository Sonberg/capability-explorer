using System.Text.Json;
using System.Security.Cryptography;
using CapabilityExplorer.Capabilities.Analysis;

namespace CapabilityExplorer.Cli;

internal static class AnalysisPublicationBinding
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static PreparedAnalysis Validate(string root, AnalysisRun run, PreparedAnalysis prepared)
    {
        RequireIdentity(run.RunId); RequireIdentity(run.AttemptId);
        var runDirectory = Path.Combine(Path.GetFullPath(root),".sandbox-cache/requests/runs",run.RunId);
        if (run.ConfigurationPath != Path.Combine(runDirectory,"capabilities.json") ||
            (run.BindingsPath is not null && run.BindingsPath != Path.Combine(runDirectory,"bindings.json")))
            throw new InvalidDataException("publication does not reference canonical frozen inputs");
        var current = Read<AnalysisRun>(Path.Combine(runDirectory,"run.json"));
        if (current.ConfigurationPath != "capabilities.json" ||
            (current.BindingsPath is not null && current.BindingsPath != "bindings.json") ||
            (current.BindingsPath is null) != (run.BindingsPath is null) ||
            current.RunId != run.RunId || current.AttemptId != run.AttemptId || current.AcceptedInputsHash != run.AcceptedInputsHash ||
            current.ManifestHash != run.ManifestHash || current.State != "publishing" || current.CapabilityId != run.CapabilityId)
            throw new InvalidDataException("publication run differs from its durable owner record");
        var resolvedPath = Path.Combine(runDirectory,"resolved.json");
        var resolvedBytes = ReadBytes(resolvedPath);
        if (run.ManifestHash is null || Convert.ToHexStringLower(SHA256.HashData(resolvedBytes)) != run.ManifestHash)
            throw new InvalidDataException("publication resolved manifest hash mismatch");
        var resolved = JsonSerializer.Deserialize<ResolvedManifest>(resolvedBytes,Json) ?? throw new InvalidDataException("empty resolved manifest");
        if (resolved.AcceptedInputsHash != run.AcceptedInputsHash || !Same(resolved.Inputs,prepared.Inputs))
            throw new InvalidDataException("prepared inputs differ from the immutable resolved manifest");
        var revisions = resolved.Inputs.Where(p => p.Key.StartsWith("revision:",StringComparison.Ordinal))
            .ToDictionary(p => p.Key[9..],p => p.Value,StringComparer.Ordinal);
        if (revisions.Count is < 1 or > 1000 || prepared.Repositories.Count != revisions.Count ||
            prepared.Repositories.Select(repo => repo.Id).Distinct(StringComparer.Ordinal).Count() != revisions.Count)
            throw new InvalidDataException("prepared repository set differs from the immutable revision vector");
        var expectedArtifacts = new Dictionary<string,string>(StringComparer.Ordinal);
        var attemptDirectory = Path.Combine(runDirectory,"attempt-" + run.AttemptId);
        foreach (var repo in prepared.Repositories)
        {
            if (!SafeRepository(repo.Id) || !revisions.TryGetValue(repo.Id,out var sha) || sha != repo.Sha ||
                sha.Length != 40 || sha.Any(c => !(char.IsAsciiDigit(c) || c is >= 'a' and <= 'f')) ||
                repo.Source != Path.Combine(attemptDirectory,"src",repo.Id) || repo.Staging != Path.Combine(attemptDirectory,"out",repo.Id,sha))
                throw new InvalidDataException("prepared repository identity or attempt path mismatch");
            foreach (var input in resolved.Inputs.Where(p => p.Key.StartsWith("artifact:" + repo.Id + ":",StringComparison.Ordinal)))
            {
                var relative = input.Key[(10 + repo.Id.Length)..];
                if (Path.IsPathRooted(relative) || !relative.EndsWith(".jsonl",StringComparison.Ordinal) ||
                    relative.Contains('\\') || relative.Split('/').Any(segment => segment is "" or "." or ".." || segment.Any(char.IsControl)))
                    throw new InvalidDataException("invalid resolved artifact path");
                var artifact = Path.Combine(repo.Staging,relative);
                AnalysisPreparation.RejectLinks(artifact);
                expectedArtifacts.Add(artifact,input.Value);
            }
        }
        if (resolved.Inputs.Count(p => p.Key.StartsWith("artifact:",StringComparison.Ordinal)) != expectedArtifacts.Count ||
            !Same(expectedArtifacts,prepared.Artifacts))
            throw new InvalidDataException("prepared artifact map differs from the immutable resolved manifest");
        return new(prepared.Repositories.ToArray(), new Dictionary<string,string>(resolved.Inputs,StringComparer.Ordinal), expectedArtifacts);
    }
    private static bool Same(IReadOnlyDictionary<string,string> expected, Dictionary<string,string> actual)
    {
        var ordinal = actual.ToDictionary(pair => pair.Key,pair => pair.Value,StringComparer.Ordinal);
        return expected.Count == ordinal.Count && expected.All(pair => ordinal.TryGetValue(pair.Key,out var value) && value == pair.Value);
    }
    private static bool SafeRepository(string id) => id.Length is >= 1 and <= 100 && (char.IsAsciiLetterLower(id[0]) || char.IsAsciiDigit(id[0])) &&
        id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_' or '-');
    private static void RequireIdentity(string? id)
    {
        if (id is null || id.Length != 32 || id.Any(c => !(char.IsAsciiDigit(c) || c is >= 'a' and <= 'f')))
            throw new InvalidDataException("invalid publication run or attempt identity");
    }
    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(ReadBytes(path),Json) ?? throw new InvalidDataException("empty publication metadata");
    private static byte[] ReadBytes(string path)
    {
        AnalysisPreparation.RejectLinks(path);
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("publication metadata exceeds one MiB");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 1024 * 1024) throw new InvalidDataException("publication metadata exceeds one MiB");
        return bytes;
    }
}
