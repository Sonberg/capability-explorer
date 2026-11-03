using System.Diagnostics;
using CapabilityExplorer.Capabilities.Analysis;

namespace CapabilityExplorer.Cli;

/// <summary>Bounded offline jobs share one attempt and owner, but never share writable extractor output.</summary>
internal static class AnalysisParallelExtraction
{
    private const string CSharp = "platform/src/CapabilityExplorer.Extractors.CSharp/bin/Debug/net10.0/ce-cs.dll";
    private const string TypeScript = "extractors/ts/src/cli.ts";
    private sealed record Job(AnalysisRepository Repository,string Language,string Output);

    internal static async Task Run(string root, FileRunStore store, AnalysisRun run, AnalysisProcess process,
        IReadOnlyList<AnalysisRepository> repositories, string work, string locks, string packageDir)
    {
        if (run.Settings.ExtractionJobs is < 1 or > 4) throw new ArgumentException("extraction jobs must be from 1 to 4");
        var attempt = new AttemptIdentity(run.RunId,run.AttemptId!);
        if (Path.GetFullPath(work) != store.AttemptDirectory(attempt)) throw new IOException("extraction work is not attempt-owned");
        var languages = new List<string> { "cs" };
        var script = Path.Combine(root,TypeScript);
        if (File.Exists(script))
        {
            var source = await File.ReadAllTextAsync(script);
            if (source.Contains("\"extract\"",StringComparison.Ordinal)) languages.Add("ts");
            if (source.Contains("\"extract-sql\"",StringComparison.Ordinal)) languages.Add("sql");
        }
        var jobs = languages.SelectMany(language => repositories.Select(repo => new Job(repo,language,
            Path.Combine(work,"out/tasks",language,repo.Id,repo.Sha)))).ToArray();
        var started = Stopwatch.StartNew();
        Console.WriteLine("-- extraction concurrency: " + run.Settings.ExtractionJobs);
        using var pool = new SemaphoreSlim(run.Settings.ExtractionJobs);
        var tasks = jobs.Select(Execute).ToArray();
        try { await Task.WhenAll(tasks); }
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

        async Task Execute(Job job)
        {
            await pool.WaitAsync();
            try
            {
                process.CheckCancellation();
                var timer = Stopwatch.StartNew();
                AnalysisPreparation.RejectLinks(job.Output);
                Directory.CreateDirectory(job.Output);
                var args = new List<string> { "--offline",job.Language == "cs" ? "dotnet" : "node",job.Language == "cs" ? "dotnet" : "node",
                    job.Language == "cs" ? CSharp : TypeScript, job.Language == "sql" ? "extract-sql" : "extract",
                    "--source",InContainer(job.Repository.Source),"--repository",job.Repository.Id,"--commit",job.Repository.Sha,"--out",InContainer(job.Output) };
                if (job.Language == "cs" && run.Settings.Packages)
                {
                    args.AddRange(["--packages",InContainer(packageDir)]);
                    var lockFile = Path.Combine(locks,job.Repository.Id + ".json");
                    AnalysisPreparation.RejectLinks(lockFile);
                    if (File.Exists(lockFile)) args.AddRange(["--lock",InContainer(lockFile)]);
                }
                if (job.Language != "sql" && run.Settings.IncludeTests) args.Add("--include-tests");
                await process.Run("scripts/sandbox",args);
                Console.WriteLine($"-- timing: {job.Repository.Id} {job.Language} {timer.Elapsed.TotalSeconds:F1}s");
            }
            catch { process.Abort(); throw; }
            finally { pool.Release(); }
        }
        string InContainer(string path)
        {
            var relative = Path.GetRelativePath(root,Path.GetFullPath(path));
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../",StringComparison.Ordinal)) throw new IOException("extraction path escapes project root");
            return "/work/" + relative.Replace('\\','/');
        }
    }
}
