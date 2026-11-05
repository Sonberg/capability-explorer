using System.Security.Cryptography;
using System.Text.Json;
using CapabilityExplorer.Capabilities.Analysis;

namespace CapabilityExplorer.Cli;

internal static class AnalysisPackageReuse
{
    private sealed record Candidate(string RunId, string Directory, string Hash, string[] Files);

    internal static string? Seed(string root, AnalysisRun run, IReadOnlyList<AnalysisRepository> repositories,
        string packageDir, string feedPolicy, AnalysisProcess process)
    {
        var store = new FileRunStore(Path.Combine(root,".sandbox-cache/requests"));
        if (Path.GetFullPath(packageDir) != Path.Combine(store.AttemptDirectory(new(run.RunId,run.AttemptId!)),"packages")) return null;
        AnalysisPreparation.RejectLinks(packageDir);
        if (Directory.EnumerateFileSystemEntries(packageDir).Any()) return null;
        using var verification = process.Measure("packages.reuse.verify");
        var runtime = run.Settings.RuntimeIdentity ?? AnalysisPreparation.RuntimeIdentity(root);
        Dictionary<string,string>? sources = null;
        var candidates = new List<AnalysisRun>();
        foreach (var directory in Directory.EnumerateDirectories(Path.GetDirectoryName(store.RunDirectory(run.RunId))!))
        {
            try
            {
                var previous = store.ReadRun(Path.GetFileName(directory));
                if (previous.RunId != run.RunId && previous.CapabilityId == run.CapabilityId && previous.State == "done" &&
                    previous.ExecutionCompleted && previous.Settings.Packages && previous.AttemptId is not null &&
                    previous.Settings.IncludeTests == run.Settings.IncludeTests &&
                    previous.Settings.NugetAllowedOrigins == run.Settings.NugetAllowedOrigins &&
                    previous.Effects is { Count: > 0 } && previous.Effects.All(effect => effect.State == "confirmed"))
                    candidates.Add(previous);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or KeyNotFoundException) { }
        }
        foreach (var previous in candidates.OrderByDescending(candidate => candidate.AcceptedAt))
        {
            Candidate? candidate;
            try
            {
                var manifest = Path.Combine(store.RunDirectory(previous.RunId),"resolved.json");
                AnalysisPreparation.RejectLinks(manifest);
                if (new FileInfo(manifest).Length > 1024 * 1024) continue;
                var bytes = File.ReadAllBytes(manifest);
                if (bytes.Length > 1024 * 1024 || Convert.ToHexStringLower(SHA256.HashData(bytes)) != previous.ManifestHash) continue;
                var resolved = JsonSerializer.Deserialize<ResolvedManifest>(bytes,JsonSerializerOptions.Web);
                if (resolved is null || resolved.AcceptedInputsHash != previous.AcceptedInputsHash ||
                    !resolved.Inputs.TryGetValue("runtime",out var priorRuntime) || priorRuntime != runtime ||
                    !resolved.Inputs.TryGetValue("package-feed-policy",out var priorPolicy) || priorPolicy != feedPolicy ||
                    !resolved.Inputs.TryGetValue("package-cache",out var expected) ||
                    resolved.Inputs.Keys.Count(key => key.StartsWith("revision:",StringComparison.Ordinal)) != repositories.Count) continue;
                sources ??= repositories.ToDictionary(repository => repository.Id,repository => AnalysisPackagePreparation.SourceIdentity(repository.Source),StringComparer.Ordinal);
                if (repositories.Any(repository =>
                    !resolved.Inputs.TryGetValue("revision:" + repository.Id,out var revision) || revision != repository.Sha ||
                    !resolved.Inputs.TryGetValue("source:" + repository.Id,out var source) || source != sources[repository.Id])) continue;
                var packages = Path.Combine(store.AttemptDirectory(new(previous.RunId,previous.AttemptId!)),"packages");
                var actual = Identity(packages,out var files,process);
                if (files.Length == 0 || actual != expected) continue;
                candidate = new(previous.RunId,packages,expected,files);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or KeyNotFoundException) { continue; }
            verification.Complete();
            verification.Dispose();
            using (var timing = process.Measure("packages.reuse.copy"))
            {
                foreach (var file in candidate.Files)
                {
                    AnalysisPreparation.RejectLinks(file);
                    var target = Path.Combine(packageDir,Path.GetRelativePath(candidate.Directory,file));
                    AnalysisPreparation.RejectLinks(target);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file,target,overwrite:false);
                }
                timing.Complete();
            }
            using (var timing = process.Measure("packages.reuse.verify-copy"))
            {
                if (Identity(packageDir) != candidate.Hash) throw new InvalidDataException("retained package content changed during verified reuse");
                timing.Complete();
            }
            using (var timing = process.Measure("packages.reuse.verify-source"))
            {
                if (Identity(candidate.Directory) != candidate.Hash) throw new InvalidDataException("retained package content changed during verified reuse");
                timing.Complete();
            }
            return candidate.RunId;
        }
        verification.Complete();
        return null;
    }

    private static string Identity(string directory) => Identity(directory,out _);

    private static string Identity(string directory, out string[] files, AnalysisProcess? process = null)
    {
        using (var inventory = process?.Measure("packages.reuse.inventory"))
        {
            files = AnalysisPreparation.CheckedFiles(directory).Order(StringComparer.Ordinal).ToArray();
            inventory?.Complete();
        }
        return AnalysisWorkerCommand.HashText(string.Join('\n',files
            .Select(file => Path.GetRelativePath(directory,file) + ":" + AnalysisWorkerCommand.HashFile(file))));
    }
}