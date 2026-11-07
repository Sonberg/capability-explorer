using System.Text.Json;
using System.Text.RegularExpressions;
using CapabilityExplorer.Capabilities;
using CapabilityExplorer.Capabilities.Analysis;

namespace CapabilityExplorer.Cli;

internal sealed record AnalysisRepository(string Id, string Sha, string Source, string Staging);
internal sealed record PreparedAnalysis(IReadOnlyList<AnalysisRepository> Repositories, Dictionary<string,string> Inputs, Dictionary<string,string> Artifacts);

internal static class AnalysisPreparation
{
    internal static readonly SemaphoreSlim SourceAcquisitionGate = new(1, 1);
    internal static readonly SemaphoreSlim PackageAcquisitionGate = new(1, 1);
    private static readonly string[] ReservedEnvironmentPrefixes = ["CE_", "DOCKER_", "CONTAINER_", "SANDBOX_", "DOTNET_", "GITHUB_", "GH_", "GIT_", "npm_"];
    private static readonly string[] TokenVariables = ["GITHUB_TOKEN", "GH_TOKEN", "GH_CONFIG_DIR"];
    private const string Cli = "platform/src/CapabilityExplorer.Cli/bin/Debug/net10.0/ce.dll";
    private const string CSharp = "platform/src/CapabilityExplorer.Extractors.CSharp/bin/Debug/net10.0/ce-cs.dll";
    private const string TypeScript = "extractors/ts/src/cli.ts";

    internal static void ValidateSettings(AnalysisSettings settings)
    {
        if (settings.ExtractionJobs is < 1 or > 4 || settings.CapabilityWorkers is < 1 or > 4 ||
            settings.ExtractionJobs * settings.CapabilityWorkers > 16)
            throw new ArgumentOutOfRangeException(nameof(settings), "worker and extraction limits exceed the bounded analysis resource ceiling");
        if (!GitHubAuth.IsValidMode(settings.GitHubAuth)) throw new ArgumentException("github-auth must be auto, git, none or account:<login>");
        var names = settings.NugetPassEnv.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (names.Length > 50 || names.Any(n => !Regex.IsMatch(n, "^[A-Z_][A-Z0-9_]{0,99}\\z") ||
            n is "HOME" or "PATH" or "TMPDIR" or "CI" or "NUGET_PACKAGES" or "NUGET_HTTP_CACHE_PATH" or "NUGET_XMLDOC_MODE" ||
            ReservedEnvironmentPrefixes.Any(prefix => n.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))))
            throw new ArgumentException("NuGet environment names must identify credentials, never runtime or policy settings");
        if (settings.NugetAllowedOrigins?.Length > 16384) throw new ArgumentException("NuGet origin configuration too large");
        foreach (var origin in (settings.NugetAllowedOrigins ?? "").Split(',',StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!Uri.TryCreate(origin,UriKind.Absolute,out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("NuGet policy requires HTTPS origins without credentials, paths, query or fragment");
        if (Environment.GetEnvironmentVariable("SANDBOX_DOTNET_IMAGE") is not null || Environment.GetEnvironmentVariable("SANDBOX_NODE_IMAGE") is not null)
            throw new ArgumentException("analysis requires the pinned runtime images; image overrides need separate qualification");
    }

    internal static string RuntimeIdentity(string root)
    {
        var files = new[] { "config/runtime/images.env", "scripts/sandbox", "scripts/github-token", "scripts/container-engine", "scripts/lib/container-engine.sh", "scripts/lib/analysis-runtime.ts", "scripts/fetch-npm-packages.mjs", Cli, CSharp, TypeScript, "extractors/ts/package-lock.json" };
        var inputs = files.Select(p => { RejectLinks(Path.Combine(root,p)); return p + ":" + AnalysisWorkerCommand.HashFile(Path.Combine(root, p)); });
        // Transitive managed binaries affect parser semantics as well as the entry assembly.
        var parserInputs = CheckedFiles(Path.Combine(root,"extractors/ts/src")).Order(StringComparer.Ordinal)
            .Select(p => Path.GetRelativePath(root,p) + ":" + AnalysisWorkerCommand.HashFile(p));
        var dlls = new[] { Path.GetDirectoryName(Cli)!, Path.GetDirectoryName(CSharp)! }
            .Distinct().SelectMany(d => Directory.GetFiles(Path.Combine(root,d), "*.dll")).Distinct().Order(StringComparer.Ordinal)
            .Select(p => Path.GetRelativePath(root,p) + ":" + AnalysisWorkerCommand.HashFile(p));
        return AnalysisWorkerCommand.HashText(string.Join('\n', inputs.Concat(dlls).Concat(parserInputs)));
    }

    internal static async Task<PreparedAnalysis> Prepare(string root, FileRunStore store, AnalysisRun run, AnalysisProcess process, CancellationToken stop = default)
    {
        ValidateSettings(run.Settings);
        var runtime = RuntimeIdentity(root);
        if (run.Settings.RuntimeIdentity is not null && run.Settings.RuntimeIdentity != runtime) throw new InvalidOperationException("runtime/extractor inputs changed after admission");
        var attempt = new AttemptIdentity(run.RunId, run.AttemptId!);
        var work = Path.GetFullPath(store.AttemptDirectory(attempt));
        RejectLinks(work);
        Directory.CreateDirectory(work);
        if (Path.GetRelativePath(root, work).StartsWith("../", StringComparison.Ordinal)) throw new IOException("attempt must be inside project root");
        using var definitions = CapabilityStore.Open(run.ConfigurationPath, Path.Combine(root,".sandbox-cache/sources/snapshots.json"), loadSnapshots: false);
        var definition = definitions.Find(run.CapabilityId) ?? throw new FormatException("frozen configuration lacks capability");
        foreach (var source in definition.Repositories)
        {
            SourceParser.Validate(source);
            if (source.Kind != SourceKind.Local) continue;
            var expanded = Path.GetFullPath(SourceSync.ExpandHome(source.Location));
            if (expanded.Contains(',') || expanded.Contains(':') || expanded.Contains('\n')) throw new IOException("unsupported local mount path");
            RejectLinks(expanded);
        }
        var tokenEnv = new Dictionary<string,string>();
        foreach (var name in TokenVariables)
            if (Environment.GetEnvironmentVariable(name) is {} value) tokenEnv[name] = value;
        var token = await process.Run("scripts/github-token", [run.Settings.GitHubAuth], tokenEnv, capture:true);
        // Acquisition executes only in this exclusive trusted host, not a second CLI with a claimed lock token.
        // Git never executes repository code; subsequent extraction children retain the offline sandbox profile.
        var sync = new SourceSync(Path.Combine(root, ".sandbox-cache/sources"), auth: GitHubAuth.FromToken(token.Trim(), "host credential helper"));
        var priorCheck = store.ReadLatestCheck(definition.Id);
        CapabilitySnapshot snapshot;
        var manifest = new List<string>();
        var exports = new Dictionary<string, ExportResult>(StringComparer.Ordinal);
        await SourceAcquisitionGate.WaitAsync(stop);
        try
        {
            using (var timing = process.Measure("sources.check"))
            {
                snapshot = run.Settings.Locked
                    ? priorCheck is { State: "done", Snapshot: not null } && FileRunStore.CheckMatches(priorCheck, run.ConfigurationPath, run.Settings.GitHubAuth)
                        ? priorCheck.Snapshot : throw new InvalidOperationException("locked analysis requires a matching completed host source check")
                    : await process.AcquireSource(sync, token => sync.CheckAsync(definition, token));
                if (sync.ExecutionUncertain) { process.MarkUncertainAcquisition(); throw new InvalidOperationException("source acquisition requires reconciliation"); }
                process.CheckCancellation();
                if (snapshot.Repositories.Count != definition.Repositories.Count || snapshot.Repositories.Any(repo => repo.CommitSha is null || repo.Error is not null))
                {
                    foreach (var repository in snapshot.Repositories.Where(repository => repository.Error is not null).Take(100))
                        process.LogFailure($"Source '{repository.Id}' check failed: {repository.Error}");
                    throw new InvalidOperationException("source acquisition failed");
                }
                timing.Complete();
            }
            foreach (var repository in snapshot.Repositories)
            {
                process.CheckCancellation();
                using var timing = process.Measure("sources.export",repository.Id);
                var source = definition.Repositories.Single(source => source.Id == repository.Id && source.Ref == repository.Ref);
                var destination = Path.Combine(work, "src", source.Id);
                exports[source.Id] = await process.AcquireSource(sync, token => sync.ExportAsync(source, repository.CommitSha!, destination, token));
                manifest.Add($"{source.Id}\t{repository.CommitSha}\tsrc/{source.Id}");
                timing.Complete();
            }
        }
        finally { SourceAcquisitionGate.Release(); }
        process.CheckCancellation();
        await File.WriteAllLinesAsync(Path.Combine(work, "manifest.tsv"), manifest, stop);
        await File.WriteAllTextAsync(Path.Combine(work, "include-tests"), definition.IncludeTests ? "1\n" : "0\n", stop);
        var manifestPath = Path.Combine(work,"manifest.tsv");
        if (new FileInfo(manifestPath).Length > 1024 * 1024) throw new IOException("manifest too large");
        var repositories = new List<AnalysisRepository>();
        foreach (var line in await File.ReadAllLinesAsync(manifestPath, stop))
        {
            var fields = line.Split('\t');
            if (fields.Length != 3 || !Regex.IsMatch(fields[0], "^[a-z0-9][a-z0-9._-]{0,99}\\z") || !Regex.IsMatch(fields[1],"^[a-f0-9]{40}\\z")) throw new FormatException("invalid export manifest");
            if (repositories.Any(r => r.Id == fields[0]) || !definition.Repositories.Any(r => r.Id == fields[0])) throw new FormatException("manifest repository mismatch");
            var expectedSuffix = "/" + fields[0];
            var segments = fields[2].Split('/');
            if (fields[2] != "src/" + fields[0] &&
                !(segments.Length == 3 && segments[0] == "src" && Regex.IsMatch(segments[1], "^[a-f0-9]{32}\\z") && fields[2].EndsWith(expectedSuffix, StringComparison.Ordinal)))
                throw new FormatException("invalid export source path");
            var source = Path.GetFullPath(Path.Combine(work, fields[2]));
            var staging = Path.Combine(work, "out", fields[0], fields[1]);
            RejectLinks(source); RejectLinks(staging);
            Directory.CreateDirectory(staging);
            repositories.Add(new(fields[0], fields[1], source, staging));
        }
        if (repositories.Count == 0 || repositories.Count != definition.Repositories.Count || repositories.Count > 1000)
            throw new FormatException("incomplete capability export; refusing partial analysis");
        var packageDir = Path.Combine(work,"packages");
        var locks = Path.Combine(work,"locks");
        RejectLinks(locks); RejectLinks(packageDir);
        Directory.CreateDirectory(locks); Directory.CreateDirectory(packageDir);
        Dictionary<string,string> sources;
        using (var timing = process.Measure("hashing.sources.before"))
        {
            sources = repositories.ToDictionary(repo => repo.Id, repo => AnalysisPackagePreparation.SourceIdentity(repo.Source), StringComparer.Ordinal);
            timing.Complete();
        }
        var shadow = await AnalysisAdapterShadow.LoadAsync(root, stop);
        var shadowOutcomes = new Dictionary<string, AdapterShadowOutcome>(StringComparer.Ordinal);
        foreach (var repo in repositories)
        {
            process.CheckCancellation();
            using var timing = process.Measure("adapter.shadow", repo.Id);
            var outcome = await AnalysisAdapterShadow.PlanAsync(repo.Source, repo.Id, repo.Sha, exports[repo.Id].TreeSha,
                exports[repo.Id].Skipped, shadow, TimeSpan.FromSeconds(120), stop);
            shadowOutcomes[repo.Id] = outcome;
            if (outcome.Status != "ok")
                process.LogFailure($"Shadow adapter plan '{repo.Id}': {outcome.Status}" + (outcome.Codes.Count > 0 ? ":" + string.Join(',', outcome.Codes) : ""));
            timing.Complete();
        }
        await PackageAcquisitionGate.WaitAsync(stop);
        Dictionary<string, string> packageInputs;
        try { packageInputs = await AnalysisPackagePreparation.Run(root, run, process, repositories, work, locks, packageDir); }
        finally { PackageAcquisitionGate.Release(); }
        // Prove npm/workspace links stay confined before any offline parser can follow them.
        Dictionary<string,string> dependencies;
        using (var timing = process.Measure("hashing.dependencies.before"))
        {
            dependencies = repositories.ToDictionary(repo => repo.Id, repo => AnalysisPackagePreparation.DependencyIdentity(repo.Source), StringComparer.Ordinal);
            timing.Complete();
        }
        await AnalysisParallelExtraction.Run(root, store, run, process, repositories, work, locks, packageDir);
        process.CheckCancellation();
        using var integrityTiming = process.Measure("integrity.final");
        if (runtime != RuntimeIdentity(root)) throw new InvalidOperationException("runtime changed during preparation");
        var inputs = new Dictionary<string,string> { ["runtime"] = runtime, ["manifest.tsv"] = AnalysisWorkerCommand.HashFile(manifestPath) };
        var artifacts = new Dictionary<string,string>();
        foreach (var repo in repositories)
        {
            inputs["revision:" + repo.Id] = repo.Sha;
            if (sources[repo.Id] != AnalysisPackagePreparation.SourceIdentity(repo.Source)) throw new InvalidOperationException("exported source changed during preparation");
            inputs["source:" + repo.Id] = sources[repo.Id];
            inputs["export-path:" + repo.Id] = Path.GetRelativePath(work, repo.Source).Replace('\\', '/');
            AnalysisAdapterShadow.AddInputs(inputs, repo.Id, exports[repo.Id].TreeSha, shadowOutcomes[repo.Id]);
            if (dependencies[repo.Id] != AnalysisPackagePreparation.DependencyIdentity(repo.Source)) throw new InvalidOperationException("installed dependencies changed during extraction");
            inputs["npm-dependencies:" + repo.Id] = dependencies[repo.Id];
            foreach (var file in CheckedFiles(repo.Staging))
            {
                if (!file.EndsWith(".jsonl",StringComparison.Ordinal)) throw new FormatException("unexpected staging file");
                artifacts[file] = AnalysisWorkerCommand.HashFile(file);
                inputs["artifact:" + repo.Id + ":" + Path.GetRelativePath(repo.Staging,file)] = artifacts[file];
            }
        }
        AnalysisAdapterShadow.AddGlobalInputs(inputs, shadow);
        foreach (var input in packageInputs) inputs.Add(input.Key, input.Value);
        foreach (var file in CheckedFiles(locks)) inputs["lock:" + Path.GetFileName(file)] = AnalysisWorkerCommand.HashFile(file);
        if (run.Settings.Packages) inputs["package-cache"] = AnalysisWorkerCommand.HashText(string.Join('\n',CheckedFiles(packageDir).Order(StringComparer.Ordinal)
            .Select(file => Path.GetRelativePath(packageDir,file) + ":" + AnalysisWorkerCommand.HashFile(file))));
        if (artifacts.Count is < 1 or > 100000) throw new FormatException("invalid artifact count");
        integrityTiming.Complete();
        return new(repositories,inputs,artifacts);
    }

    internal static IEnumerable<string> CheckedFiles(string root)
    {
        var pending = new Stack<string>(); pending.Push(root);
        var count = 0;
        while (pending.TryPop(out var directory))
        {
            RejectLinks(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++count > 200000) throw new IOException("input entry limit exceeded");
                RejectLinks(entry);
                if (Directory.Exists(entry)) pending.Push(entry);
                else { if (new FileInfo(entry).Length > 64L * 1024 * 1024) throw new IOException("input file too large"); yield return entry; }
            }
        }
    }
    internal static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (new FileInfo(current).LinkTarget is not null || ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)) throw new IOException("symlink/reparse paths are unsupported");
    }
}
