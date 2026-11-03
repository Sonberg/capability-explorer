using CapabilityExplorer.Capabilities.Analysis;

namespace CapabilityExplorer.Cli;

/// <summary>One host owner dispatches bounded capability tasks and drains every started task before returning.</summary>
internal static class AnalysisCapabilityScheduler
{
    internal static Task<int> RunAdmitted(FileRunStore store, WorkerOwnership owner, IReadOnlyList<AnalysisRun> runs,
        int ceiling, Func<AnalysisRun, Task<int>> execute, CancellationToken stop)
    {
        if (runs.Count == 0) return Task.FromResult(0);
        return Run(store, owner, runs, Math.Min(ceiling, runs.Min(run => run.Settings.CapabilityWorkers)), execute, stop);
    }

    internal static async Task<int> RunPending(FileRunStore store, WorkerOwnership owner, int ceiling,
        Func<AnalysisRun, Task<int>> execute, CancellationToken stop)
    {
        if (ceiling is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(ceiling), "capability worker ceiling must be from 1 to 4");
        while (!stop.IsCancellationRequested)
        {
            var queued = store.Queued(owner);
            if (queued.Count == 0) return 0;
            var bound = Math.Min(ceiling, queued.Min(run => run.Settings.CapabilityWorkers));
            if (bound is < 1 or > 4) throw new InvalidDataException("queued capability worker ceiling is invalid");
            var batch = queued.Take(bound).ToArray();
            var result = await Run(store, owner, batch, bound, execute, stop);
            if (result != 0) return result;
        }
        return 130;
    }

    private static async Task<int> Run(FileRunStore store, WorkerOwnership owner, IReadOnlyList<AnalysisRun> runs,
        int ceiling, Func<AnalysisRun, Task<int>> execute, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(execute);
        if (ceiling is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(ceiling), "capability worker ceiling must be from 1 to 4");
        if (runs.Count == 0) return 0;

        var failed = 0;
        for (var offset = 0; offset < runs.Count; offset += ceiling)
        {
            if (stop.IsCancellationRequested) return 130;
            var batch = runs.Skip(offset).Take(ceiling).ToArray();
            await Task.WhenAll(batch.Select(ExecuteOne));
            if (stop.IsCancellationRequested) return 130;
            if (Volatile.Read(ref failed) != 0) return 1;
        }
        return 0;

        async Task ExecuteOne(AnalysisRun admitted)
        {
            try
            {
                var claimed = store.ClaimNext(owner, admitted.RunId);
                if (claimed is null) throw new InvalidOperationException("admitted analysis run could not be claimed");
                if (await execute(claimed) != 0) Interlocked.Exchange(ref failed, 1);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("analysis scheduler stopped dispatch (" + error.GetType().Name + ")");
                Interlocked.Exchange(ref failed, 1);
            }
        }
    }
}
