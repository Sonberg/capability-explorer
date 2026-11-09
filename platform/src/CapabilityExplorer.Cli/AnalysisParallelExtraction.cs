using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CapabilityExplorer.Capabilities;
using CapabilityExplorer.Capabilities.Analysis;
using CapabilityExplorer.Contracts;

namespace CapabilityExplorer.Cli;

/// <summary>Bounded offline jobs share one attempt and owner, but never share writable extractor output.</summary>
internal static class AnalysisParallelExtraction
{
    private const string CSharp = "platform/src/CapabilityExplorer.Extractors.CSharp/bin/Debug/net10.0/ce-cs.dll";
    private const string TypeScript = "extractors/ts/src/cli.ts";
    private sealed record Job(AnalysisRepository Repository,string Language,string Output,EligibleCSharpSelection? Selection);

    internal static async Task<IReadOnlyDictionary<string, int>> Run(string root, FileRunStore store, AnalysisRun run, AnalysisProcess process,
        IReadOnlyList<AnalysisRepository> repositories, string work, string locks, string packageDir,
        IReadOnlyDictionary<string, EligibleCSharpSelection>? selections = null,
        AdapterShadowContext? shadow = null,
        IReadOnlyDictionary<string, string>? sourceIdentities = null,
        CancellationToken stop = default)
    {
        if (run.Settings.ExtractionJobs is < 1 or > 4) throw new ArgumentException("extraction jobs must be from 1 to 4");
        var attempt = new AttemptIdentity(run.RunId,run.AttemptId!);
        if (Path.GetFullPath(work) != store.AttemptDirectory(attempt)) throw new IOException("extraction work is not attempt-owned");
        if (run.Settings.EligibleCSharp && (selections is null || shadow is null || sourceIdentities is null))
            throw new InvalidOperationException("eligible C# extraction requires its validated immutable selection");
        var jobs = new List<Job>();
        foreach (var repo in repositories)
        {
            EligibleCSharpSelection? selection = null;
            if (run.Settings.EligibleCSharp) selection = selections![repo.Id];
            if (!run.Settings.EligibleCSharp || selection!.SelectedCount > 0)
                jobs.Add(new Job(repo,"cs",Path.Combine(work,"out/tasks","cs",repo.Id,repo.Sha),selection));
        }
        foreach (var language in new[] { "ts", "sql" })
            jobs.AddRange(repositories.Select(repo => new Job(repo,language,
                Path.Combine(work,"out/tasks",language,repo.Id,repo.Sha),null)));
        var emitted = new Dictionary<string, int>(StringComparer.Ordinal);
        if (run.Settings.EligibleCSharp)
            foreach (var repo in repositories) emitted.Add(repo.Id, 0);
        using var timing = process.Measure("extraction");
        var started = Stopwatch.StartNew();
        Console.WriteLine("-- extraction concurrency: 1");
        try
        {
            foreach (var job in jobs) await Execute(job);
        }
        catch
        {
            process.Abort();
            // WhenAll only completes after every started job has drained. Then clean the shared exact attempt label again.
            await process.CleanupAfterAbort();
            throw;
        }
        process.CheckCancellation();
        // Merge only after every child reported success. Collisions are refused, never silently overwritten.
        foreach (var job in jobs)
        {
            AnalysisPreparation.RejectLinks(job.Repository.Staging);
            Directory.CreateDirectory(job.Repository.Staging);
            foreach (var file in AnalysisPreparation.CheckedFiles(job.Output))
            {
                var relative = Path.GetRelativePath(job.Output,file);
                var destination = Path.Combine(job.Repository.Staging,relative);
                AnalysisPreparation.RejectLinks(destination);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file,destination,overwrite:false);
            }
        }
        Console.WriteLine($"-- timing: extraction {started.Elapsed.TotalSeconds:F1}s");
        timing.Complete();
        return emitted;

        async Task Execute(Job job)
        {
            try
            {
                process.CheckCancellation();
                if (run.Settings.EligibleCSharp)
                    await VerifyActiveIdentity(job.Repository);
                using var jobTiming = process.Measure("extraction.job",job.Repository.Id,job.Language);
                var timer = Stopwatch.StartNew();
                AnalysisPreparation.RejectLinks(job.Output);
                Directory.CreateDirectory(job.Output);
                var args = new List<string> { "--offline",job.Language == "cs" ? "dotnet" : "node",job.Language == "cs" ? "dotnet" : "node",
                    job.Language == "cs" ? CSharp : TypeScript,
                    job.Language == "sql" ? "extract-sql" : job.Language == "cs" && job.Selection is not null ? "extract-selected-v1" : "extract",
                    "--source",ContainerPath(root,job.Repository.Source),"--repository",job.Repository.Id,"--commit",job.Repository.Sha,"--out",ContainerPath(root,job.Output) };
                if (job.Language == "cs" && run.Settings.Packages)
                {
                    args.AddRange(["--packages",ContainerPath(root,packageDir)]);
                    var lockFile = Path.Combine(locks,job.Repository.Id + ".json");
                    AnalysisPreparation.RejectLinks(lockFile);
                    if (File.Exists(lockFile)) args.AddRange(["--lock",ContainerPath(root,lockFile)]);
                }
                if (job.Language != "sql" && run.Settings.IncludeTests) args.Add("--include-tests");
                if (job.Language == "cs" && job.Selection is { } selected)
                {
                    AddSelector(args, selected);
                    if (args.Sum(argument => Encoding.UTF8.GetByteCount(argument) + 1) > 65_536)
                        throw new InvalidDataException("selected C# extraction argv exceeds 64 KiB");
                }
                await process.Run("scripts/sandbox",args);
                if (job.Language == "cs" && job.Selection is { } expected)
                {
                    var files = AnalysisPreparation.CheckedFiles(job.Output).Where(file => file.EndsWith(".jsonl",StringComparison.Ordinal)).ToArray();
                    foreach (var file in files)
                    {
                        var report = ArtifactValidator.Validate(await File.ReadAllTextAsync(file, stop));
                        if (!report.IsValid) throw new InvalidDataException("selected C# child emitted an invalid artifact");
                    }
                    if (files.Length != expected.SelectedCount)
                        throw new InvalidDataException("selected C# emitted artifact count differs from the validated selection");
                    emitted[job.Repository.Id] = files.Length;
                }
                Console.WriteLine($"-- timing: {job.Repository.Id} {job.Language} {timer.Elapsed.TotalSeconds:F1}s");
                jobTiming.Complete();
            }
            catch { process.Abort(); throw; }
        }
        async Task VerifyActiveIdentity(AnalysisRepository repository)
        {
            process.CheckCancellation();
            await AnalysisAdapterShadow.VerifyCurrentAsync(root,shadow!,stop);
            if (sourceIdentities![repository.Id] != AnalysisPackagePreparation.SourceIdentity(repository.Source))
                throw new InvalidOperationException("exported source changed before extraction child");
        }
    }

    internal static async Task<SelectedCSharpValidation> ValidateSelected(
        string root, AnalysisRepository repository, EligibleCSharpSelection selection, bool includeTests,
        AnalysisProcess process)
    {
        var arguments = new List<string>
        {
            "--offline", "dotnet", "dotnet", CSharp, "validate-selected-v1",
            "--source", ContainerPath(root, repository.Source),
        };
        AddSelector(arguments, selection);
        if (includeTests) arguments.Add("--include-tests");
        if (arguments.Sum(argument => Encoding.UTF8.GetByteCount(argument) + 1) > 65_536)
            throw new InvalidDataException("selected C# validation argv exceeds 64 KiB");
        process.CheckCancellation();
        var output = await process.Run("scripts/sandbox", arguments, capture: true, captureLimit: 65536);
        using var document = JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = 8 });
        var rootElement = document.RootElement;
        if (rootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("selected C# validation response is malformed");
        var names = rootElement.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (rootElement.EnumerateObject().Count() != 6 ||
            !names.SetEquals(["schema", "candidateCount", "testExcludedCount", "selectedCount", "selectionSha256", "sourceSha256"]) ||
            rootElement.GetProperty("schema").GetString() != "ce.csharp.selection-validation.v1")
            throw new InvalidDataException("selected C# validation response is malformed");
        var result = new SelectedCSharpValidation(
            rootElement.GetProperty("candidateCount").GetInt32(),
            rootElement.GetProperty("testExcludedCount").GetInt32(),
            rootElement.GetProperty("selectedCount").GetInt32(),
            rootElement.GetProperty("selectionSha256").GetString() ?? "",
            rootElement.GetProperty("sourceSha256").GetString() ?? "");
        if (result.CandidateCount is < 1 or > 100 || result.TestExcludedCount < 0 ||
            result.SelectedCount < 0 ||             result.TestExcludedCount + result.SelectedCount != result.CandidateCount ||
            result.SelectionSha256 != selection.SelectionSha256 || result.SourceSha256 != selection.SourceSha256)
            throw new InvalidDataException("selected C# validation response does not match its request");
        return result;
    }

    internal static List<string> AddSelector(List<string> arguments, EligibleCSharpSelection selection)
    {
        if (selection.ProjectPaths.Count is < 1 or > 100)
            throw new InvalidDataException("selected C# project count is outside its bounds");
        foreach (var path in selection.ProjectPaths)
        {
            if (Encoding.UTF8.GetByteCount(path) > 1024 || path.StartsWith('/') ||
                path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl) ||
                path.Split('/').Any(segment => segment.Length == 0 || segment is "." or "..") ||
                !path.EndsWith(".csproj",StringComparison.Ordinal))
                throw new InvalidDataException("selected C# project path is unsafe");
            arguments.Add("--project");
            arguments.Add(path);
        }
        arguments.Add("--selection-sha256");
        arguments.Add(selection.SelectionSha256);
        arguments.Add("--source-sha256");
        arguments.Add(selection.SourceSha256);
        return arguments;
    }

    private static string ContainerPath(string root, string path)
    {
        var relative = Path.GetRelativePath(root,Path.GetFullPath(path));
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../",StringComparison.Ordinal))
            throw new IOException("extraction path escapes project root");
        return "/work/" + relative.Replace('\\','/');
    }
}
