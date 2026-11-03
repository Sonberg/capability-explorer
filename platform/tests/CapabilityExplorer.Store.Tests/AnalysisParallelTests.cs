extern alias analysiscli;
using CapabilityExplorer.Capabilities.Analysis;
using analysiscli::CapabilityExplorer.Cli;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class AnalysisParallelTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task Offline_jobs_overlap_within_the_bound_and_merge_isolated_outputs(int limit)
    {
        using var f = new ParallelFixture(limit);
        using var process = new AnalysisProcess(f.Root,f.Store,f.Run,CancellationToken.None,new Runtime());
        var execution = AnalysisParallelExtraction.Run(f.Root,f.Store,f.Run,process,f.Repositories,f.Work,f.Locks,f.Packages);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var expected = Math.Min(limit,6);
        while (Directory.GetFiles(Path.Combine(f.Root,"active")).Length < expected)
        {
            if (execution.IsCompleted) await execution;
            await Task.Delay(10,timeout.Token);
        }
        Assert.Equal(expected,Directory.GetFiles(Path.Combine(f.Root,"active")).Length);
        File.WriteAllText(Path.Combine(f.Root,"release"),"");
        await execution.WaitAsync(timeout.Token);
        Assert.True(process.TerminationConfirmed);
        foreach (var repository in f.Repositories)
        {
            var files = Directory.GetFiles(repository.Staging).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(3,files.Length);
            Assert.Equal(new[] { "cs:" + repository.Id,"sql:" + repository.Id,"ts:" + repository.Id },files.Select(File.ReadAllText).Order(StringComparer.Ordinal));
        }
        var peaks = File.ReadAllLines(Path.Combine(f.Root,"peaks")).Select(int.Parse);
        Assert.All(peaks,peak => Assert.InRange(peak,1,expected));
    }

    [Fact]
    public async Task Failing_parallel_job_does_not_publish_or_erase_sibling_uncertainty()
    {
        using var f = new ParallelFixture(2);
        File.WriteAllText(Path.Combine(f.Root,"fail"),"");
        using var process = new AnalysisProcess(f.Root,f.Store,f.Run,CancellationToken.None,new Runtime());
        var execution = AnalysisParallelExtraction.Run(f.Root,f.Store,f.Run,process,f.Repositories,f.Work,f.Locks,f.Packages);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (Directory.GetFiles(Path.Combine(f.Root,"active")).Length < 2)
        {
            if (execution.IsCompleted) await execution;
            await Task.Delay(10,timeout.Token);
        }
        File.WriteAllText(Path.Combine(f.Root,"release"),"");
        await Assert.ThrowsAnyAsync<Exception>(async () => await execution.WaitAsync(timeout.Token));
        Assert.False(process.TerminationConfirmed);
        Assert.All(f.Repositories,repo => Assert.Empty(Directory.GetFiles(repo.Staging)));
        Assert.Equal("unknown",f.Store.FinishExecution(f.Owner,new(f.Run.RunId,f.Run.AttemptId!),1,process.TerminationConfirmed).State);
        Assert.False(f.Store.ReadRun(f.Run.RunId).CancellationRequested);
    }

    [Fact]
    public async Task Cancellation_drains_parallel_children_without_merging_or_claiming_stopped()
    {
        using var f = new ParallelFixture(2);
        using var process = new AnalysisProcess(f.Root,f.Store,f.Run,CancellationToken.None,new Runtime());
        var execution = AnalysisParallelExtraction.Run(f.Root,f.Store,f.Run,process,f.Repositories,f.Work,f.Locks,f.Packages);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (Directory.GetFiles(Path.Combine(f.Root,"active")).Length < 2) await Task.Delay(10,timeout.Token);
        f.Store.Cancel(f.Run.RunId);
        await Assert.ThrowsAnyAsync<Exception>(async () => await execution.WaitAsync(timeout.Token));
        Assert.False(process.TerminationConfirmed);
        Assert.All(f.Repositories,repo => Assert.Empty(Directory.GetFiles(repo.Staging)));
        Assert.Equal("unknown",f.Store.FinishExecution(f.Owner,new(f.Run.RunId,f.Run.AttemptId!),1,process.TerminationConfirmed).State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(5)]
    public void Invalid_job_limits_refuse_before_any_child(int jobs) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => AnalysisPreparation.ValidateSettings(new(ExtractionJobs:jobs)));

    private sealed class Runtime : IAnalysisContainerRuntime
    {
        public Task EnsureFrozen() => Task.CompletedTask;
        public void ApplyEnvironment(System.Diagnostics.ProcessStartInfo start) { }
        public Task<bool> CleanupOwned(string label) => Task.FromResult(true);
    }

    private sealed class ParallelFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath().Replace("/var/","/private/var/",StringComparison.Ordinal),"ce-parallel-" + Guid.NewGuid().ToString("N"));
        public FileRunStore Store { get; }
        public WorkerOwnership Owner { get; }
        public AnalysisRun Run { get; }
        public string Work { get; }
        public string Locks => Path.Combine(Work,"locks");
        public string Packages => Path.Combine(Root,"packages");
        public AnalysisRepository[] Repositories { get; }
        public ParallelFixture(int jobs)
        {
            if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Unix qualification required");
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Path.Combine(Root,"active"));
            Directory.CreateDirectory(Path.Combine(Root,"scripts"));
            Directory.CreateDirectory(Path.Combine(Root,"extractors/ts/src"));
            File.WriteAllText(Path.Combine(Root,"extractors/ts/src/cli.ts"),"\"extract\" \"extract-sql\"");
            var config = Path.Combine(Root,"capabilities.json");
            File.WriteAllText(config,"""{"capabilities":[{"id":"sales"}]}""");
            Store = new(Path.Combine(Root,".sandbox-cache/requests"));
            Store.Admit("sales",config,null,new(Packages:false,ExtractionJobs:jobs));
            Owner = Store.AcquireWorker(); Run = Store.ClaimNext(Owner)!;
            Work = Store.AttemptDirectory(new(Run.RunId,Run.AttemptId!));
            Directory.CreateDirectory(Locks);
            Repositories = Enumerable.Range(0,2).Select(index =>
            {
                var id = "repo-" + index;
                var sha = new string('a',40);
                var staging = Path.Combine(Work,"out",id,sha);
                Directory.CreateDirectory(staging);
                return new AnalysisRepository(id,sha,Path.Combine(Work,"src",id),staging);
            }).ToArray();
            var script = Path.Combine(Root,"scripts/sandbox");
            File.WriteAllText(script,"""
                #!/bin/sh
                kind=ts; repo=; output=
                for arg in "$@"; do
                  case "$arg" in *ce-cs.dll) kind=cs;; extract-sql) kind=sql;; esac
                done
                while [ "$#" -gt 0 ]; do
                  case "$1" in --repository) shift; repo="$1";; --out) shift; output="$1";; esac
                  shift
                done
                case "$output" in /work/*) output="$PWD/${output#/work/}";; esac
                touch "active/$kind-$repo"
                find active -type f | wc -l >> peaks
                while [ ! -f release ]; do sleep .01; done
                if [ -f fail ] && [ "$kind" = cs ]; then exit 9; fi
                mkdir -p "$output"
                printf '%s' "$kind:$repo" > "$output/$kind.jsonl"
                rm -f "active/$kind-$repo"
                """ + "\n");
            File.SetUnixFileMode(script,UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        public void Dispose() { Owner.Dispose(); Directory.Delete(Root,true); }
    }
}
