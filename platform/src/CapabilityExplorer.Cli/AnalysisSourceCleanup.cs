using CapabilityExplorer.Capabilities.Analysis;
using System.Security.Cryptography;
using System.Text.Json;

namespace CapabilityExplorer.Cli;

internal static class AnalysisSourceCleanup
{
    internal static void AfterPublication(FileRunStore store, AnalysisRun run, PreparedAnalysis prepared)
    {
        var current = store.ReadRun(run.RunId);
        if (current.State != "done" || current.AttemptId != run.AttemptId || current.ManifestHash != run.ManifestHash || current.AcceptedInputsHash != run.AcceptedInputsHash)
            throw new InvalidOperationException("source cleanup requires the confirmed publication identity");
        var manifestPath = Path.Combine(store.RunDirectory(run.RunId),"resolved.json");
        AnalysisPreparation.RejectLinks(manifestPath);
        if (new FileInfo(manifestPath).Length > 1024 * 1024) throw new InvalidDataException("cleanup manifest exceeds bounds");
        var bytes = File.ReadAllBytes(manifestPath);
        if (bytes.Length > 1024 * 1024 || Convert.ToHexStringLower(SHA256.HashData(bytes)) != current.ManifestHash)
            throw new InvalidDataException("cleanup manifest identity changed");
        var resolved = JsonSerializer.Deserialize<ResolvedManifest>(bytes,JsonSerializerOptions.Web) ?? throw new InvalidDataException("missing cleanup manifest");
        if (resolved.AcceptedInputsHash != current.AcceptedInputsHash || resolved.Inputs.Count != prepared.Inputs.Count ||
            resolved.Inputs.Any(pair => !prepared.Inputs.TryGetValue(pair.Key,out var value) || value != pair.Value))
            throw new InvalidDataException("cleanup inputs differ from durable evidence");
        var attempt = store.AttemptDirectory(new(run.RunId,run.AttemptId!));
        foreach (var repository in prepared.Repositories)
        {
            var path = Path.GetFullPath(repository.Source);
            if (!resolved.Inputs.TryGetValue("export-path:" + repository.Id,out var relative) ||
                !resolved.Inputs.TryGetValue("revision:" + repository.Id,out var sha) || sha != repository.Sha ||
                path != Path.GetFullPath(Path.Combine(attempt,relative)) ||
                !path.StartsWith(Path.Combine(attempt,"src") + Path.DirectorySeparatorChar,StringComparison.Ordinal))
                throw new InvalidDataException("source cleanup is not bound to the attempt manifest");
            AnalysisPreparation.RejectLinks(path);
            // Directory.Delete removes nested links themselves; it does not recursively delete their targets.
            if (Directory.Exists(path)) Directory.Delete(path,recursive:true);
        }
    }
}
