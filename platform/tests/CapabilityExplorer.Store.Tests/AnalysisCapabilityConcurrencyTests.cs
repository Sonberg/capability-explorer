extern alias analysiscli;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using CapabilityExplorer.Capabilities.Analysis;
using analysiscli::CapabilityExplorer.Cli;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class AnalysisCapabilityConcurrencyTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "ce-capability-workers-" + Guid.NewGuid().ToString("N"));
    private readonly FileRunStore _store;
    private readonly WorkerOwnership _owner;
    private readonly AnalysisRun[] _runs;

    public AnalysisCapabilityConcurrencyTests()
    {
        Directory.CreateDirectory(_root);
        var ids = new[] { "sales", "other", "third" };
        var configuration = Path.Combine(_root, "capabilities.json");
        File.WriteAllText(configuration, JsonSerializer.Serialize(new
        {
            capabilities = ids.Select(id => new { id, repositories = Array.Empty<object>() })
        }));
        _store = new FileRunStore(Path.Combine(_root, ".sandbox-cache/requests"));
        _runs = ids.Select(id => _store.Admit(id, configuration, null, new(Packages: false)).Run).ToArray();
        _owner = _store.AcquireWorker();
    }

    [Fact]
    public async Task Isolated_extractions_overlap_and_one_uncertain_failure_preserves_sibling_outputs()
    {
        if (OperatingSystem.IsWindows()) return;
        var scripts = Path.Combine(_root, "scripts");
        var parser = Path.Combine(_root, "extractors/ts/src");
        var active = Path.Combine(_root, "active");
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(parser);
        Directory.CreateDirectory(active);
        File.WriteAllText(Path.Combine(parser, "cli.ts"), "\"extract\" \"extract-sql\"");
        var sandbox = Path.Combine(scripts, "sandbox");
        File.WriteAllText(sandbox, """
            #!/bin/sh
            kind=ts; repo=; output=
            for arg in "$@"; do case "$arg" in *ce-cs.dll) kind=cs;; extract-sql) kind=sql;; esac; done
            while [ "$#" -gt 0 ]; do case "$1" in --repository) shift; repo="$1";; --out) shift; output="$1";; esac; shift; done
            case "$output" in /work/*) output="$PWD/${output#/work/}";; esac
            safe=${SANDBOX_LABEL#ce-analyze=}
            touch "active/$safe-$kind-$repo"
            while [ ! -f release ]; do sleep .01; done
            if [ -f fail ] && [ "$(cat fail)" = "$SANDBOX_LABEL" ]; then exit 9; fi
            mkdir -p "$output"
            printf '%s' "$kind:$repo" > "$output/$kind.jsonl"
            rm -f "active/$safe-$kind-$repo"
            """ + "\n");
        File.SetUnixFileMode(sandbox, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var release = new ManualResetEventSlim();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var activeAttempts = new ConcurrentDictionary<string, AnalysisRun>(StringComparer.Ordinal);
        var execution = AnalysisCapabilityScheduler.RunAdmitted(_store, _owner, _runs, 2, async run =>
        {
            activeAttempts[run.AttemptId!] = run;
            var work = _store.AttemptDirectory(new(run.RunId, run.AttemptId!));
            var repoId = "repo-" + run.CapabilityId;
            var sha = new string(run.CapabilityId == "sales" ? 'a' : 'c', 40);
            var source = Path.Combine(work, "src", repoId);
            var staging = Path.Combine(work, "out", repoId, sha);
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(staging);
            File.WriteAllText(Path.Combine(source, "source.txt"), run.CapabilityId);
            var repository = new AnalysisRepository(repoId, sha, source, staging);
            using var process = new AnalysisProcess(_root, _store, run, CancellationToken.None, new Runtime());
            try
            {
                await AnalysisParallelExtraction.Run(_root, _store, run, process, [repository], work,
                    Path.Combine(work, "locks"), Path.Combine(work, "packages"));
                var inputs = new Dictionary<string, string>
                {
                    ["source"] = AnalysisPackagePreparation.SourceIdentity(source),
                    ["revision"] = sha,
                };
                var artifacts = AnalysisPreparation.CheckedFiles(staging).ToArray();
                Assert.Equal(3, artifacts.Length);
                foreach (var file in artifacts) inputs[Path.GetRelativePath(staging, file)] = AnalysisWorkerCommand.HashFile(file);
                var attempt = new AttemptIdentity(run.RunId, run.AttemptId!);
                var manifest = _store.BindResolved(_owner, attempt, new(run.AcceptedInputsHash, inputs));
                _store.FinishExecution(_owner, attempt, 0);
                using var publication = _store.BeginPublication(_owner, attempt, manifest);
                publication.Intent("test", run.CapabilityId);
                publication.Confirm("test", run.CapabilityId, AnalysisWorkerCommand.HashText(manifest));
                publication.Complete();
                return 0;
            }
            catch
            {
                var current = _store.ReadRun(run.RunId);
                if (current.State is "running" or "cancelling")
                    _store.FinishExecution(_owner, new(run.RunId, run.AttemptId!), 1, process.TerminationConfirmed);
                return 1;
            }
        }, timeout.Token);

        var wait = Stopwatch.StartNew();
        while (Directory.GetFiles(active).Length < 4)
        {
            if (execution.IsCompleted) await execution;
            if (wait.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("two capability extractions did not overlap");
            await Task.Delay(10, timeout.Token);
        }
        Assert.Equal(4, Directory.GetFiles(active).Length);
        Assert.Equal(2, activeAttempts.Count);
        var first = activeAttempts.Values.Single(run => run.CapabilityId == "sales");
        var second = activeAttempts.Values.Single(run => run.CapabilityId == "other");
        var firstLabel = "ce-analyze=" + AnalysisWorkerCommand.HashText(_root)[..16] + "-" + first.RunId + "-" + first.AttemptId;
        File.WriteAllText(Path.Combine(_root, "fail"), firstLabel);
        File.WriteAllText(Path.Combine(_root, "release"), "");

        Assert.Equal(1, await execution.WaitAsync(timeout.Token));
        Assert.Equal("unknown", _store.ReadRun(first.RunId).State);
        Assert.Equal("done", _store.ReadRun(second.RunId).State);
        Assert.False(_store.ReadRun(second.RunId).CancellationRequested);
        Assert.NotEqual(first.AttemptId, second.AttemptId);
        Assert.NotEqual(_store.AttemptDirectory(new(first.RunId, first.AttemptId!)),
            _store.AttemptDirectory(new(second.RunId, second.AttemptId!)));
        Assert.NotEqual(_store.LogPath(first.RunId), _store.LogPath(second.RunId));
        Assert.True(File.Exists(_store.LogPath(first.RunId)));
        Assert.True(File.Exists(_store.LogPath(second.RunId)));
        Assert.Equal("queued", _store.ReadRun(_runs[2].RunId).State);
        Assert.NotNull(_store.ReadBlockReason());
        var siblingAttempt = _store.AttemptDirectory(new(second.RunId, second.AttemptId!));
        Assert.Equal(3, AnalysisPreparation.CheckedFiles(Path.Combine(siblingAttempt, "out", "repo-other",
            new string('c', 40))).Count());
    }

    [Fact]
    public async Task Capability_runs_overlap_up_to_ceiling_then_dispatch_queued_run()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startCount = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var active = 0;
        var peak = 0;
        var attempts = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var errors = new ConcurrentQueue<Exception>();
        var execution = AnalysisCapabilityScheduler.RunAdmitted(_store, _owner, _runs, 2, async run =>
        {
            Assert.True(attempts.TryAdd(run.RunId, run.AttemptId!));
            var current = Interlocked.Increment(ref active);
            UpdateMaximum(ref peak, current);
            if (Interlocked.Increment(ref startCount) == 2) started.TrySetResult();
            try
            {
                await Task.Run(() => release.Wait(timeout.Token), timeout.Token);
                Complete(run);
                return 0;
            }
            catch (Exception error)
            {
                errors.Enqueue(error);
                return 1;
            }
            finally { Interlocked.Decrement(ref active); }
        }, timeout.Token);

        await started.Task.WaitAsync(timeout.Token);
        Assert.Equal(2, Volatile.Read(ref active));
        Assert.Equal(2, attempts.Count);
        Assert.Equal("queued", _store.ReadRun(_runs[2].RunId).State);
        Assert.Equal(2, attempts.Values.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, attempts.Keys.Select(runId => _store.AttemptDirectory(
            new(runId, attempts[runId]))).Distinct(StringComparer.Ordinal).Count());

        release.Set();
        var result = await execution.WaitAsync(timeout.Token);
        Assert.Empty(errors);
        Assert.Equal(0, result);
        Assert.Equal(2, peak);
        Assert.All(_runs, run => Assert.Equal("done", _store.ReadRun(run.RunId).State));
        Assert.Equal(0, Volatile.Read(ref active));
    }

    [Fact]
    public async Task Background_scheduler_honors_each_queued_runs_frozen_worker_ceiling()
    {
        var root = Path.Combine(_root, "frozen-worker-ceiling");
        Directory.CreateDirectory(root);
        var ids = new[] { "one", "two", "three" };
        var configuration = Path.Combine(root, "capabilities.json");
        File.WriteAllText(configuration, JsonSerializer.Serialize(new
        {
            capabilities = ids.Select(id => new { id, repositories = Array.Empty<object>() })
        }));
        var store = new FileRunStore(Path.Combine(root, ".sandbox-cache/requests"));
        var runs = ids.Select(id => store.Admit(id, configuration, null,
            new(Packages: false, CapabilityWorkers: 1)).Run).ToArray();
        using var owner = store.AcquireWorker();
        using var permits = new SemaphoreSlim(0);
        using var started = new SemaphoreSlim(0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var startCount = 0;
        var active = 0;
        var peak = 0;
        var execution = AnalysisCapabilityScheduler.RunPending(store, owner, 4, async run =>
        {
            var current = Interlocked.Increment(ref active);
            UpdateMaximum(ref peak, current);
            Interlocked.Increment(ref startCount);
            started.Release();
            try
            {
                await permits.WaitAsync(timeout.Token);
                Complete(store, owner, run);
                return 0;
            }
            finally { Interlocked.Decrement(ref active); }
        }, timeout.Token);

        for (var expected = 1; expected <= runs.Length; expected++)
        {
            await started.WaitAsync(timeout.Token);
            await Task.Delay(50, timeout.Token);
            Assert.Equal(expected, Volatile.Read(ref startCount));
            Assert.Equal(1, Volatile.Read(ref active));
            permits.Release();
        }

        Assert.Equal(0, await execution.WaitAsync(timeout.Token));
        Assert.Equal(1, peak);
        Assert.All(runs, run => Assert.Equal("done", store.ReadRun(run.RunId).State));
    }

    [Fact]
    public async Task One_failed_capability_does_not_cancel_sibling_and_scheduler_drains_started_tasks()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startCount = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var completedTasks = new ConcurrentBag<string>();
        var execution = AnalysisCapabilityScheduler.RunAdmitted(_store, _owner, _runs, 2, async run =>
        {
            if (Interlocked.Increment(ref startCount) == 2) started.TrySetResult();
            await Task.Run(() => release.Wait(timeout.Token), timeout.Token);
            try
            {
                if (run.CapabilityId == "sales")
                {
                    _store.FinishExecution(_owner, new(run.RunId, run.AttemptId!), 1);
                    return 1;
                }
                Complete(run);
                return 0;
            }
            finally { completedTasks.Add(run.CapabilityId); }
        }, timeout.Token);

        await started.Task.WaitAsync(timeout.Token);
        release.Set();
        Assert.Equal(1, await execution.WaitAsync(timeout.Token));
        Assert.Equal(2, completedTasks.Count);
        Assert.Equal("failed", _store.ReadRun(_runs[0].RunId).State);
        Assert.Equal("done", _store.ReadRun(_runs[1].RunId).State);
        Assert.False(_store.ReadRun(_runs[1].RunId).CancellationRequested);
        Assert.Equal("queued", _store.ReadRun(_runs[2].RunId).State);
    }

    [Fact]
    public async Task Cancelling_one_capability_preserves_and_drains_its_started_sibling()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startCount = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var completedTasks = new ConcurrentBag<string>();
        var execution = AnalysisCapabilityScheduler.RunAdmitted(_store, _owner, _runs, 2, async run =>
        {
            if (Interlocked.Increment(ref startCount) == 2) started.TrySetResult();
            await Task.Run(() => release.Wait(timeout.Token), timeout.Token);
            try
            {
                if (_store.ReadRun(run.RunId).CancellationRequested)
                {
                    _store.FinishExecution(_owner, new(run.RunId, run.AttemptId!), 130);
                    return 1;
                }
                Complete(run);
                return 0;
            }
            finally { completedTasks.Add(run.CapabilityId); }
        }, timeout.Token);

        await started.Task.WaitAsync(timeout.Token);
        Assert.Equal("accepted", _store.Cancel(_runs[0].RunId).Outcome);
        release.Set();

        Assert.Equal(1, await execution.WaitAsync(timeout.Token));
        Assert.Equal(2, completedTasks.Count);
        Assert.Equal("cancelled", _store.ReadRun(_runs[0].RunId).State);
        Assert.Equal("done", _store.ReadRun(_runs[1].RunId).State);
        Assert.False(_store.ReadRun(_runs[1].RunId).CancellationRequested);
        Assert.Equal("queued", _store.ReadRun(_runs[2].RunId).State);
    }

    private void Complete(AnalysisRun run)
        => Complete(_store, _owner, run);

    private static void Complete(FileRunStore store, WorkerOwnership owner, AnalysisRun run)
    {
        var attempt = new AttemptIdentity(run.RunId, run.AttemptId!);
        var manifest = store.BindResolved(owner, attempt,
            new(run.AcceptedInputsHash, new Dictionary<string, string> { ["revision"] = new('a', 40) }));
        store.FinishExecution(owner, attempt, 0);
        using var publication = store.BeginPublication(owner, attempt, manifest);
        publication.Intent("test", run.CapabilityId);
        publication.Confirm("test", run.CapabilityId, new('b', 64));
        publication.Complete();
    }

    private static void UpdateMaximum(ref int maximum, int value)
    {
        var observed = Volatile.Read(ref maximum);
        while (value > observed)
        {
            var prior = Interlocked.CompareExchange(ref maximum, value, observed);
            if (prior == observed) return;
            observed = prior;
        }
    }

    private sealed class Runtime : IAnalysisContainerRuntime
    {
        public Task EnsureFrozen() => Task.CompletedTask;
        public void ApplyEnvironment(ProcessStartInfo start) { }
        public Task<bool> CleanupOwned(string label) => Task.FromResult(true);
    }

    public void Dispose()
    {
        _owner.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
