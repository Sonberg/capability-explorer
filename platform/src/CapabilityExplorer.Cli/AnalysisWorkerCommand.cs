using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CapabilityExplorer.Capabilities;
using CapabilityExplorer.Capabilities.Analysis;

[assembly: InternalsVisibleTo("CapabilityExplorer.Cli.Tests")]

namespace CapabilityExplorer.Cli;

/// <summary>Single trusted host owns preparation and publication; sandbox children only produce staging.</summary>
internal static class AnalysisWorkerCommand
{
    public static async Task<int> RunAsync(string command, IReadOnlyDictionary<string, string> options)
    {
        try
        {
            if (command is not ("analysis-worker" or "analysis-run" or "analysis-all")) throw new ArgumentException("unsupported analysis command");
            foreach (var key in options.Keys)
                if (key is not ("capability" or "capabilities" or "bindings" or "include-tests" or "packages" or "locked" or "github-auth"))
                    throw new ArgumentException("unsupported analysis option");
            if ((Environment.GetEnvironmentVariable("CE_CONTAINER_ENGINE") ?? "docker") != "docker")
                throw new InvalidOperationException("analysis requires the qualified Docker host profile");
            var root = Path.GetFullPath(Environment.CurrentDirectory);
            if (!File.Exists(Path.Combine(root, "scripts/sandbox")) || !File.Exists(Path.Combine(root, "config/runtime/images.env")))
                throw new InvalidOperationException("analysis must run in the project root on the trusted .NET host");
            if (File.Exists("/.dockerenv") || Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true")
                throw new InvalidOperationException("analysis ownership must run on the host, outside an extraction container");
            var store = new FileRunStore(Path.Combine(root, ".sandbox-cache/requests"));
            using var owner = store.AcquireWorker(); // precedes recovery, admission and every shared write
            if (store.Recover(owner).Blocked) throw new InvalidOperationException("unreconciled execution/publication blocks analysis; inspect run history");
            using var stop = new CancellationTokenSource();
            ConsoleCancelEventHandler interrupt = (_, e) => { e.Cancel = true; stop.Cancel(); };
            Console.CancelKeyPress += interrupt;
            try
            {
                if (command == "analysis-worker")
                {
                    while (!stop.IsCancellationRequested)
                    {
                        var next = store.ClaimNext(owner);
                        if (next is null) { await Task.Delay(300, stop.Token); continue; }
                        if (await Execute(root, store, owner, next, stop.Token) != 0) return 1;
                    }
                    return 0;
                }
                var configuration = Path.GetFullPath(options.GetValueOrDefault("capabilities") ?? "capabilities.json");
                using var definitions = CapabilityStore.Open(configuration, Path.Combine(root, ".sandbox-cache/sources/snapshots.json"));
                var bindings = options.GetValueOrDefault("bindings") ?? Environment.GetEnvironmentVariable("BINDINGS");
                var receipt = command == "analysis-all" ? await AnalysisReceipt.Start(root,configuration,bindings) : null;
                var completedRuns = new List<string>();
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
                        NugetAllowedOrigins: Environment.GetEnvironmentVariable("CE_NUGET_ALLOWED_ORIGINS"));
                    AnalysisPreparation.ValidateSettings(settings);
                    var admitted = store.Admit(id, configuration, bindings, settings);
                    if (!admitted.Accepted) throw new InvalidOperationException("capability has unresolved run " + admitted.Run.RunId);
                    var run = store.ClaimNext(owner, admitted.Run.RunId) ?? throw new InvalidOperationException("accepted run could not be claimed");
                    if (await Execute(root, store, owner, run, stop.Token) != 0) return 1;
                    completedRuns.Add(run.RunId);
                }
                if (receipt is not null) await receipt.Complete(store,completedRuns,configuration,bindings);
                return 0;
            }
            finally { Console.CancelKeyPress -= interrupt; }
            bool Flag(string key, string env, bool fallback)
            {
                var value = options.GetValueOrDefault(key) ?? Environment.GetEnvironmentVariable(env);
                return value switch { null or "" => fallback, "1" or "true" => true, "0" or "false" => false, _ => throw new ArgumentException(key + " must be 0 or 1") };
            }
        }
        catch (OperationCanceledException) { return 130; }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException or FormatException or JsonException or System.ComponentModel.Win32Exception)
        { Console.Error.WriteLine("analysis: " + e.Message); return 2; }
    }

    private static async Task<int> Execute(string root, FileRunStore store, WorkerOwnership owner, AnalysisRun run, CancellationToken stop)
    {
        var attempt = new AttemptIdentity(run.RunId, run.AttemptId!);
        var process = new AnalysisProcess(root, store, run, stop);
        try
        {
            var prepared = await AnalysisPreparation.Prepare(root, store, run, process);
            process.CheckCancellation();
            var manifest = store.BindResolved(owner, attempt, new ResolvedManifest(run.AcceptedInputsHash, prepared.Inputs));
            store.FinishExecution(owner, attempt, 0);
            process.CheckCancellation();
            using var publication = store.BeginPublication(owner, attempt, manifest);
            await AnalysisPublication.Publish(root, store.ReadRun(run.RunId), prepared, publication);
            publication.Complete("Core artifacts published; optional enrichment pending.");
            var enrichment = await AnalysisEnrichment.Run();
            store.RecordEnrichment(owner,attempt,enrichment.Outcome,enrichment.Detail);
            Console.WriteLine($"analysis {run.RunId}/{run.AttemptId}: done");
            return 0;
        }
        catch (Exception e)
        {
            // A publication permit records uncertain/partial effects itself. Never rewrite that result.
            var current = store.ReadRun(run.RunId);
            if (current.State is "running" or "cancelling") store.FinishExecution(owner, attempt, 1, process.TerminationConfirmed);
            Console.Error.WriteLine($"analysis {run.RunId}/{run.AttemptId}: {e.GetType().Name}; inspect run record and log");
            return 1;
        }
    }

    internal static string HashFile(string path)
    { AnalysisPreparation.RejectLinks(path); using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    internal static string HashText(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
