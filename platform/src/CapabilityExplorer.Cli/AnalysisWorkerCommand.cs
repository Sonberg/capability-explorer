using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CapabilityExplorer.Capabilities;
using CapabilityExplorer.Capabilities.Analysis;
using CapabilityExplorer.Interpretation;

namespace CapabilityExplorer.Cli;

/// <summary>Single trusted host owns preparation and publication; sandbox children only produce staging.</summary>
internal static class AnalysisWorkerCommand
{
    private static readonly SemaphoreSlim PublicationGate = new(1, 1);

    public static async Task<int> RunAsync(string command, IReadOnlyDictionary<string, string> options)
    {
        try
        {
            if (command is not ("analysis-worker" or "analysis-run" or "analysis-all")) throw new ArgumentException("unsupported analysis command");
            foreach (var key in options.Keys)
                if (key is not ("capability" or "capabilities" or "bindings" or "include-tests" or "eligible-csharp" or "packages" or "locked" or "github-auth" or "extraction-jobs")
                    && !EmbeddingHostConfiguration.IsOption(key))
                    throw new ArgumentException("unsupported analysis option");
            _ = ResolveEngine();
            var root = Path.GetFullPath(Environment.CurrentDirectory);
            if (!File.Exists(Path.Combine(root, "scripts/sandbox")) || !File.Exists(Path.Combine(root, "config/runtime/images.env")))
                throw new InvalidOperationException("analysis must run in the project root on the trusted .NET host");
            if (File.Exists("/.dockerenv") || Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true")
                throw new InvalidOperationException("analysis ownership must run on the host, outside an extraction container");
            var runtime = new AnalysisEngineRuntime(root);
            await runtime.EnsureFrozen();
            var store = new FileRunStore(Path.Combine(root, ".sandbox-cache/requests"));
            using var owner = store.AcquireWorker(); // precedes recovery, admission and every shared write
            if (store.Recover(owner).Blocked) throw new InvalidOperationException("unreconciled execution/publication blocks analysis; inspect run history");
            AnalysisIntegrationReconciliation.TryReconcile(root, store);
            HostPackageFeeds.Publish(root);
            using var stop = new CancellationTokenSource();
            ConsoleCancelEventHandler interrupt = (_, e) => { e.Cancel = true; stop.Cancel(); };
            Console.CancelKeyPress += interrupt;
            try
            {
                if (command == "analysis-worker")
                {
                    var includeTests = Environment.GetEnvironmentVariable("INCLUDE_TESTS");
                    bool? includeTestsOverride = includeTests is null or "" ? null : EnvironmentFlag("INCLUDE_TESTS",false);
                    var hostDefaults = new AnalysisSettings(Packages:EnvironmentFlag("PACKAGES",true),
                        Locked:EnvironmentFlag("LOCKED",false), RuntimeIdentity:null,
                        GitHubAuth:Environment.GetEnvironmentVariable("GITHUB_AUTH") is { Length: > 0 } auth ? auth : "auto",
                        NugetPassEnv:Environment.GetEnvironmentVariable("NUGET_PASS_ENV") ?? "",
                        NugetAllowedOrigins:Environment.GetEnvironmentVariable("CE_NUGET_ALLOWED_ORIGINS"),
                        ExtractionJobs: Math.Min(1,ParseJobs(Environment.GetEnvironmentVariable("ANALYZE_JOBS"))),
                        EligibleCSharp: EnvironmentFlag("ELIGIBLE_CSHARP",false),
                        CapabilityWorkers: Math.Min(1,ParseWorkers(Environment.GetEnvironmentVariable("ANALYZE_WORKERS"))));
                    AnalysisPreparation.ValidateSettings(hostDefaults);
                    store.PublishWorkerDefaults(owner,hostDefaults,includeTestsOverride);

                    while (!stop.IsCancellationRequested)
                    {
                        var check = store.ClaimCheck(owner);
                        if (check is not null)
                        {
                            await HostSourceAcquisition.ExecuteCheck(root, store, owner, check, stop.Token);
                            if (store.ReadBlockReason() is not null) return 1;
                            continue;
                        }
                        var result = await AnalysisCapabilityScheduler.RunPending(store, owner, hostDefaults.CapabilityWorkers,
                            run => Execute(root, store, owner, run, runtime, options, stop.Token), stop.Token);
                        if (result != 0) return result;
                        await Task.Delay(300, stop.Token);
                    }
                    return 0;
                }
                var configuration = Path.GetFullPath(options.GetValueOrDefault("capabilities") ?? "capabilities.json");
                var bindings = options.GetValueOrDefault("bindings") ?? Environment.GetEnvironmentVariable("BINDINGS");
                var receipt = command == "analysis-all" ? await AnalysisReceipt.Start(root,configuration,bindings) : null;
                using var definitions = CapabilityStore.Open(configuration, Path.Combine(root, ".sandbox-cache/sources/snapshots.json"));
                var acceptedRuns = new List<AnalysisRun>();
                var ids = command == "analysis-all" ? definitions.Capabilities.Select(c => c.Id).ToArray()
                    : new[] { options.GetValueOrDefault("capability") ?? throw new ArgumentException("--capability is required") };
                if (ids.Length is < 1 or > 1000) throw new ArgumentException("expected 1 to 1000 capabilities");
                foreach (var id in ids)
                {
                    var definition = definitions.Find(id) ?? throw new ArgumentException("unknown capability");
                    var settings = new AnalysisSettings(Flag("include-tests", "INCLUDE_TESTS", definition.IncludeTests),
                        Flag("packages", "PACKAGES", true), Flag("locked", "LOCKED", false),
                        RuntimeIdentity: AnalysisPreparation.RuntimeIdentity(root),
                        GitHubAuth: options.GetValueOrDefault("github-auth") ?? Environment.GetEnvironmentVariable("GITHUB_AUTH") ?? "auto",
                        NugetPassEnv: Environment.GetEnvironmentVariable("NUGET_PASS_ENV") ?? "",
                        NugetAllowedOrigins: Environment.GetEnvironmentVariable("CE_NUGET_ALLOWED_ORIGINS"),
                        ExtractionJobs: Math.Min(1,ParseJobs(options.GetValueOrDefault("extraction-jobs") ?? Environment.GetEnvironmentVariable("ANALYZE_JOBS"))),
                        EligibleCSharp: Flag("eligible-csharp", "ELIGIBLE_CSHARP", false),
                        CapabilityWorkers: Math.Min(1,ParseWorkers(Environment.GetEnvironmentVariable("ANALYZE_WORKERS"))));
                    AnalysisPreparation.ValidateSettings(settings);
                    var admitted = store.Admit(id, configuration, bindings, settings);
                    if (!admitted.Accepted) throw new InvalidOperationException("capability has unresolved run " + admitted.Run.RunId);
                    acceptedRuns.Add(admitted.Run);
                }
                var completedRuns = new System.Collections.Concurrent.ConcurrentBag<string>();
                var executionResult = await AnalysisCapabilityScheduler.RunAdmitted(store, owner, acceptedRuns,
                    acceptedRuns[0].Settings.CapabilityWorkers, async run =>
                    {
                        var result = await Execute(root, store, owner, run, runtime, options, stop.Token);
                        if (result == 0) completedRuns.Add(run.RunId);
                        return result;
                    }, stop.Token);
                if (executionResult != 0) return executionResult;
                if (receipt is not null) await receipt.Complete(store,completedRuns.Order(StringComparer.Ordinal).ToArray(),configuration,bindings);
                return 0;
            }
            finally { Console.CancelKeyPress -= interrupt; }
            bool EnvironmentFlag(string env, bool fallback)
            {
                return Environment.GetEnvironmentVariable(env) switch
                { null or "" => fallback, "1" or "true" => true, "0" or "false" => false, _ => throw new ArgumentException(env + " must be 0 or 1") };
            }
            bool Flag(string key, string env, bool fallback)
            {
                var value = options.GetValueOrDefault(key) ?? Environment.GetEnvironmentVariable(env);
                return value switch { null or "" => fallback, "1" or "true" => true, "0" or "false" => false, _ => throw new ArgumentException(key + " must be 0 or 1") };
            }
        }
        catch (OperationCanceledException) { return 130; }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or FormatException or JsonException or System.ComponentModel.Win32Exception)
        { Console.Error.WriteLine("analysis: host operation failed (" + e.GetType().Name + ")"); return 2; }
    }

    private static async Task<int> Execute(string root, FileRunStore store, WorkerOwnership owner, AnalysisRun run,
        IAnalysisContainerRuntime runtime, IReadOnlyDictionary<string, string> options, CancellationToken stop)
    {
        var attempt = new AttemptIdentity(run.RunId, run.AttemptId!);
        using var process = new AnalysisProcess(root, store, run, stop, runtime);
        using var totalTiming = process.Measure("analysis.total");
        try
        {
            var prepared = await AnalysisPreparation.Prepare(root, store, run, process, stop);
            process.CheckCancellation();
            var manifest = store.BindResolved(owner, attempt, new ResolvedManifest(run.AcceptedInputsHash, prepared.Inputs));
            store.FinishExecution(owner, attempt, 0);
            process.CheckCancellation();
            AnalysisRun completed;
            Guid? publishedSnapshot;
            await PublicationGate.WaitAsync(stop);
            try
            {
                using var timing = process.Measure("publication");
                process.CheckCancellation();
                using var publication = store.BeginPublication(owner, attempt, manifest);
                publishedSnapshot = await AnalysisPublication.Publish(root, store.ReadRun(run.RunId), prepared, publication);
                completed = publication.Complete("Core artifacts published; optional enrichment pending.");
                timing.Complete();
            }
            finally { PublicationGate.Release(); }
            using (var timing = process.Measure("integration-matching"))
            {
                AnalysisIntegrationReconciliation.TryReconcile(root, store);
                timing.Complete();
            }
            try
            {
                using var timing = process.Measure("cleanup");
                AnalysisSourceCleanup.AfterPublication(store,completed,prepared);
                timing.Complete();
            }
            catch (Exception cleanupError) when (cleanupError is IOException or InvalidOperationException or UnauthorizedAccessException)
            { Console.Error.WriteLine("analysis: source cleanup incomplete; preserved attempt data for inspection"); }
            using (var timing = process.Measure("enrichment"))
            {
                var enrichment = await AnalysisEnrichment.Run(publishedSnapshot, options, ct: stop);
                store.RecordEnrichment(owner,attempt,enrichment.Outcome,enrichment.Detail);
                if (enrichment.Outcome != "failed") timing.Complete();
            }
            Console.WriteLine($"analysis {run.RunId}/{run.AttemptId}: done");
            totalTiming.Complete();
            return 0;
        }
        catch (Exception e)
        {
            // A publication permit records uncertain/partial effects itself. Never rewrite that result.
            process.LogFailure(e);
            var current = store.ReadRun(run.RunId);
            if (current.State is "running" or "cancelling") store.FinishExecution(owner, attempt, 1, process.TerminationConfirmed);
            Console.Error.WriteLine($"analysis {run.RunId}/{run.AttemptId}: {e.GetType().Name}; inspect run record and log");
            return 1;
        }
    }

    internal static int ParseJobs(string? value)
    {
        if (value is null or "") return 2;
        if (value.Length != 1 || value[0] is < '1' or > '4')
            throw new ArgumentException("ANALYZE_JOBS must be an integer from 1 to 4");
        return value[0] - '0';
    }

    internal static int ParseWorkers(string? value)
    {
        if (value is null or "") return 2;
        if (value.Length != 1 || value[0] is < '1' or > '4')
            throw new ArgumentException("ANALYZE_WORKERS must be an integer from 1 to 4");
        return value[0] - '0';
    }
    internal static string ResolveEngine()
    {
        var configured = Environment.GetEnvironmentVariable("CE_CONTAINER_ENGINE") ?? "auto";
        if (configured is "docker" or "podman") return configured;
        if (configured != "auto") throw new ArgumentException("CE_CONTAINER_ENGINE must be auto, docker or podman");
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var engine in new[] { "docker", "podman" })
            if (paths.Any(path => IsExecutable(Path.Combine(path,engine)))) return engine;
        throw new InvalidOperationException("no container engine is available");
    }

    private static bool IsExecutable(string path)
    {
        if (!File.Exists(path)) return false;
        if (OperatingSystem.IsWindows()) return true;
        return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
    }

    internal static string HashFile(string path)
    { AnalysisPreparation.RejectLinks(path); using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    internal static string HashText(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
