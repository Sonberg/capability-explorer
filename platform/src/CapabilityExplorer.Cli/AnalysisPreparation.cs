using System.Text.Json;
using System.Text.RegularExpressions;
using CapabilityExplorer.Capabilities;
using CapabilityExplorer.Capabilities.Analysis;

namespace CapabilityExplorer.Cli;

internal sealed record AnalysisRepository(string Id, string Sha, string Source, string Staging);
internal sealed record PreparedAnalysis(IReadOnlyList<AnalysisRepository> Repositories, Dictionary<string,string> Inputs, Dictionary<string,string> Artifacts);

internal static class AnalysisPreparation
{
    private static readonly string[] ReservedEnvironmentPrefixes = ["CE_", "DOCKER_", "CONTAINER_", "SANDBOX_", "DOTNET_", "GITHUB_", "GH_", "GIT_", "npm_"];
    private static readonly string[] TokenVariables = ["GITHUB_TOKEN", "GH_TOKEN", "GH_CONFIG_DIR"];
    private const string Cli = "platform/src/CapabilityExplorer.Cli/bin/Debug/net10.0/ce.dll";
    private const string CSharp = "platform/src/CapabilityExplorer.Extractors.CSharp/bin/Debug/net10.0/ce-cs.dll";
    private const string TypeScript = "extractors/ts/src/cli.ts";

    internal static void ValidateSettings(AnalysisSettings settings)
    {
        if (settings.GitHubAuth is not ("auto" or "git" or "none")) throw new ArgumentException("github-auth must be auto, git or none");
        var names = settings.NugetPassEnv.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (names.Length > 50 || names.Any(n => !Regex.IsMatch(n, "^[A-Z_][A-Z0-9_]{0,99}\\z") ||
            n is "HOME" or "PATH" or "TMPDIR" or "CI" or "NUGET_PACKAGES" or "NUGET_HTTP_CACHE_PATH" or "NUGET_XMLDOC_MODE" ||
            ReservedEnvironmentPrefixes.Any(prefix => n.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))))
            throw new ArgumentException("NuGet environment names must identify credentials, never runtime or policy settings");
        if (settings.NugetAllowedOrigins?.Length > 16384) throw new ArgumentException("NuGet origin configuration too large");
        if (Environment.GetEnvironmentVariable("SANDBOX_DOTNET_IMAGE") is not null || Environment.GetEnvironmentVariable("SANDBOX_NODE_IMAGE") is not null)
            throw new ArgumentException("analysis requires the pinned runtime images; image overrides need separate qualification");
    }

    internal static string RuntimeIdentity(string root)
    {
        var files = new[] { "config/runtime/images.env", "scripts/sandbox", "scripts/github-token", "scripts/lib/container-engine.sh", Cli, CSharp, TypeScript, "extractors/ts/package-lock.json" };
        var inputs = files.Select(p => { RejectLinks(Path.Combine(root,p)); return p + ":" + AnalysisWorkerCommand.HashFile(Path.Combine(root, p)); });
        // Transitive managed binaries affect parser semantics as well as the entry assembly.
        var parserInputs = CheckedFiles(Path.Combine(root,"extractors/ts/src")).Order(StringComparer.Ordinal)
            .Select(p => Path.GetRelativePath(root,p) + ":" + AnalysisWorkerCommand.HashFile(p));
        var dlls = new[] { Path.GetDirectoryName(Cli)!, Path.GetDirectoryName(CSharp)! }
            .Distinct().SelectMany(d => Directory.GetFiles(Path.Combine(root,d), "*.dll")).Distinct().Order(StringComparer.Ordinal)
            .Select(p => Path.GetRelativePath(root,p) + ":" + AnalysisWorkerCommand.HashFile(p));
        return AnalysisWorkerCommand.HashText(string.Join('\n', inputs.Concat(dlls).Concat(parserInputs)));
    }

    internal static async Task<PreparedAnalysis> Prepare(string root, FileRunStore store, AnalysisRun run, AnalysisProcess process)
    {
        ValidateSettings(run.Settings);
        var runtime = RuntimeIdentity(root);
        if (run.Settings.RuntimeIdentity is not null && run.Settings.RuntimeIdentity != runtime) throw new InvalidOperationException("runtime/extractor inputs changed after admission");
        var attempt = new AttemptIdentity(run.RunId, run.AttemptId!);
        var work = Path.GetFullPath(store.AttemptDirectory(attempt));
        RejectLinks(work);
        Directory.CreateDirectory(work);
        var relative = Path.GetRelativePath(root, work).Replace('\\','/');
        if (relative.StartsWith("../", StringComparison.Ordinal)) throw new IOException("attempt must be inside project root");
        string InContainer(string p) => "/work/" + Path.GetRelativePath(root,p).Replace('\\','/');
        using var definitions = CapabilityStore.Open(run.ConfigurationPath, Path.Combine(root,".sandbox-cache/sources/snapshots.json"));
        var definition = definitions.Find(run.CapabilityId) ?? throw new FormatException("frozen configuration lacks capability");
        var mounts = new List<string>();
        foreach (var source in definition.Repositories)
        {
            SourceParser.Validate(source);
            if (source.Kind != SourceKind.Local) continue;
            var expanded = Path.GetFullPath(SourceSync.ExpandHome(source.Location));
            if (expanded.Contains(',') || expanded.Contains(':') || expanded.Contains('\n')) throw new IOException("unsupported local mount path");
            RejectLinks(expanded);
            if (Path.IsPathRooted(SourceSync.ExpandHome(source.Location))) mounts.Add(expanded);
        }
        var tokenEnv = new Dictionary<string,string>();
        foreach (var name in TokenVariables)
            if (Environment.GetEnvironmentVariable(name) is {} value) tokenEnv[name] = value;
        var token = await process.Run("scripts/github-token", [run.Settings.GitHubAuth], tokenEnv, capture:true);
        var exportEnv = new Dictionary<string,string> { ["GITHUB_TOKEN"] = token.Trim(), ["CE_HOST_HOME"] = Environment.GetEnvironmentVariable("CE_HOST_HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ["SANDBOX_PASS_ENV"] = "GITHUB_TOKEN,CE_HOST_HOME", ["SANDBOX_SOURCES"] = string.Join(',', mounts.Distinct()) };
        var export = new List<string> { "dotnet", "dotnet", Cli, "export", "--capability", run.CapabilityId, "--capabilities", InContainer(run.ConfigurationPath), "--workspace", ".sandbox-cache/sources", "--out", relative };
        if (run.Settings.Locked) export.AddRange(["--locked", "1"]);
        await process.Run("scripts/sandbox", export, exportEnv);
        var manifestPath = Path.Combine(work,"manifest.tsv");
        if (new FileInfo(manifestPath).Length > 1024 * 1024) throw new IOException("manifest too large");
        var repositories = new List<AnalysisRepository>();
        foreach (var line in await File.ReadAllLinesAsync(manifestPath))
        {
            var fields = line.Split('\t');
            if (fields.Length != 3 || !Regex.IsMatch(fields[0], "^[a-z0-9][a-z0-9._-]{0,99}\\z") || !Regex.IsMatch(fields[1],"^[a-f0-9]{40}\\z") || fields[2] != "src/" + fields[0]) throw new FormatException("invalid export manifest");
            if (repositories.Any(r => r.Id == fields[0]) || !definition.Repositories.Any(r => r.Id == fields[0])) throw new FormatException("manifest repository mismatch");
            var source = Path.Combine(work, "src", fields[0]);
            var staging = Path.Combine(work, "out", fields[0], fields[1]);
            RejectLinks(source); RejectLinks(staging);
            Directory.CreateDirectory(staging);
            repositories.Add(new(fields[0], fields[1], source, staging));
        }
        if (repositories.Count == 0 || repositories.Count != definition.Repositories.Count || repositories.Count > 1000)
            throw new FormatException("incomplete capability export; refusing partial analysis");
        var packageDir = Path.Combine(root,".sandbox-cache/analyze-packages");
        var locks = Path.Combine(work,"locks");
        RejectLinks(locks); RejectLinks(packageDir);
        Directory.CreateDirectory(locks); Directory.CreateDirectory(packageDir);
        var packageEnv = new Dictionary<string,string> { ["SANDBOX_PASS_ENV"] = "CE_NUGET_ALLOWED_ORIGINS" + (run.Settings.NugetPassEnv.Length > 0 ? "," + run.Settings.NugetPassEnv : ""), ["CE_NUGET_ALLOWED_ORIGINS"] = run.Settings.NugetAllowedOrigins ?? "" };
        foreach (var name in run.Settings.NugetPassEnv.Split(',',StringSplitOptions.RemoveEmptyEntries))
            if (Environment.GetEnvironmentVariable(name) is {} secret) packageEnv[name] = secret;
        foreach (var repo in repositories)
        {
            var lockFile = Path.Combine(locks,repo.Id + ".json");
            if (run.Settings.Packages)
            {
                var args = new List<string> { "dotnet", "dotnet", CSharp, "fetch-packages", "--source", InContainer(repo.Source), "--packages", InContainer(packageDir), "--lock", InContainer(lockFile) };
                if (run.Settings.IncludeTests) args.Add("--include-tests");
                await process.Run("scripts/sandbox",args,packageEnv);
            }
            var cs = new List<string> { "--offline", "dotnet", "dotnet", CSharp, "extract", "--source", InContainer(repo.Source), "--repository",repo.Id,"--commit",repo.Sha,"--out",InContainer(repo.Staging) };
            if (run.Settings.Packages) cs.AddRange(new[] { "--packages",InContainer(packageDir),"--lock",InContainer(lockFile) });
            if (run.Settings.IncludeTests) cs.Add("--include-tests");
            await process.Run("scripts/sandbox",cs);
            foreach (var verb in new[] { "extract", "extract-sql" })
            {
                var ts = new List<string> { "--offline", "node", "node", TypeScript,verb,"--source",InContainer(repo.Source),"--repository",repo.Id,"--commit",repo.Sha,"--out",InContainer(repo.Staging) };
                if (verb == "extract" && run.Settings.IncludeTests) ts.Add("--include-tests");
                await process.Run("scripts/sandbox",ts);
            }
        }
        process.CheckCancellation();
        if (runtime != RuntimeIdentity(root)) throw new InvalidOperationException("runtime changed during preparation");
        var inputs = new Dictionary<string,string> { ["runtime"] = runtime, ["manifest.tsv"] = AnalysisWorkerCommand.HashFile(manifestPath) };
        var artifacts = new Dictionary<string,string>();
        foreach (var repo in repositories)
        {
            inputs["revision:" + repo.Id] = repo.Sha;
            inputs["source:" + repo.Id] = AnalysisWorkerCommand.HashText(string.Join('\n', CheckedFiles(repo.Source).Order(StringComparer.Ordinal)
                .Select(file => Path.GetRelativePath(repo.Source,file) + ":" + AnalysisWorkerCommand.HashFile(file))));
            foreach (var file in CheckedFiles(repo.Staging))
            {
                if (!file.EndsWith(".jsonl",StringComparison.Ordinal)) throw new FormatException("unexpected staging file");
                artifacts[file] = AnalysisWorkerCommand.HashFile(file);
                inputs["artifact:" + repo.Id + ":" + Path.GetRelativePath(repo.Staging,file)] = artifacts[file];
            }
        }
        foreach (var file in CheckedFiles(locks)) inputs["lock:" + Path.GetFileName(file)] = AnalysisWorkerCommand.HashFile(file);
        if (run.Settings.Packages) inputs["package-cache"] = AnalysisWorkerCommand.HashText(string.Join('\n',CheckedFiles(packageDir).Order(StringComparer.Ordinal)
            .Select(file => Path.GetRelativePath(packageDir,file) + ":" + AnalysisWorkerCommand.HashFile(file))));
        if (artifacts.Count is < 1 or > 100000) throw new FormatException("invalid artifact count");
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
