extern alias analysiscli;
using System.Diagnostics;
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
    public async Task Offline_jobs_run_serially_and_merge_isolated_outputs_even_for_older_parallel_settings(int limit)
    {
        using var f = new ParallelFixture(limit);
        using var process = new AnalysisProcess(f.Root,f.Store,f.Run,CancellationToken.None,new Runtime());
        var execution = AnalysisParallelExtraction.Run(f.Root,f.Store,f.Run,process,f.Repositories,f.Work,f.Locks,f.Packages);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var expected = 1;
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
    public async Task Failing_first_serial_job_leaves_later_jobs_unstarted_without_merging_or_erasing_uncertainty()
    {
        using var f = new ParallelFixture(2);
        File.WriteAllText(Path.Combine(f.Root,"fail"),"");
        using var process = new AnalysisProcess(f.Root,f.Store,f.Run,CancellationToken.None,new Runtime());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var started = f.WaitForFirstChild(timeout.Token);
        var execution = AnalysisParallelExtraction.Run(f.Root,f.Store,f.Run,process,f.Repositories,f.Work,f.Locks,f.Packages);
        await started;
        File.WriteAllText(Path.Combine(f.Root,"release"),"");
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await execution.WaitAsync(timeout.Token));
        Assert.Equal(new[] { "cs-repo-0" },File.ReadAllLines(Path.Combine(f.Root,"started")));
        Assert.False(process.TerminationConfirmed);
        Assert.All(f.Repositories,repo => Assert.Empty(Directory.GetFiles(repo.Staging)));
        Assert.Equal("unknown",f.Store.FinishExecution(f.Owner,new(f.Run.RunId,f.Run.AttemptId!),1,process.TerminationConfirmed).State);
        Assert.False(f.Store.ReadRun(f.Run.RunId).CancellationRequested);
    }

    [Fact]
    public async Task Cancellation_drains_first_serial_child_without_starting_later_jobs_merging_or_claiming_stopped()
    {
        using var f = new ParallelFixture(2);
        using var process = new AnalysisProcess(f.Root,f.Store,f.Run,CancellationToken.None,new Runtime());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var started = f.WaitForFirstChild(timeout.Token);
        var execution = AnalysisParallelExtraction.Run(f.Root,f.Store,f.Run,process,f.Repositories,f.Work,f.Locks,f.Packages);
        await started;
        f.Store.Cancel(f.Run.RunId);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await execution.WaitAsync(timeout.Token));
        Assert.Equal(new[] { "cs-repo-0" },File.ReadAllLines(Path.Combine(f.Root,"started")));
        Assert.False(process.TerminationConfirmed);
        Assert.All(f.Repositories,repo => Assert.Empty(Directory.GetFiles(repo.Staging)));
        Assert.Equal("unknown",f.Store.FinishExecution(f.Owner,new(f.Run.RunId,f.Run.AttemptId!),1,process.TerminationConfirmed).State);
        Assert.True(f.Store.ReadRun(f.Run.RunId).CancellationRequested);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(5)]
    public void Invalid_job_limits_refuse_before_any_child(int jobs) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => AnalysisPreparation.ValidateSettings(new(ExtractionJobs:jobs)));

    [Theory]
    [InlineData("docker")]
    [InlineData("podman")]
    public async Task Given_a_native_engine_choice_when_the_worker_starts_then_both_engines_reach_the_same_host_cli(string engine)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Unix qualification required");
        using var fixture = new ParallelFixture(1);
        var scripts = Path.Combine(fixture.Root,"scripts");
        Directory.CreateDirectory(Path.Combine(scripts,"lib"));
        File.Copy(Path.Combine(Fixtures.RepositoryRoot,"scripts/analyze-worker"),Path.Combine(scripts,"analyze-worker"));
        File.Copy(Path.Combine(Fixtures.RepositoryRoot,"scripts/lib/container-engine.sh"),Path.Combine(scripts,"lib/container-engine.sh"));
        var output = Path.Combine(fixture.Root,"platform/src/CapabilityExplorer.Cli/bin/Debug/net10.0");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output,"ce.dll"),"");
        var bin = Directory.CreateDirectory(Path.Combine(fixture.Root,"bin")).FullName;
        var dotnet = Path.Combine(bin,"dotnet");
        File.WriteAllText(dotnet,"#!/bin/sh\nprintf 'worker-engine:%s\\n' \"$CE_CONTAINER_ENGINE\"\n");
        File.SetUnixFileMode(dotnet,UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var start = new ProcessStartInfo("/bin/bash")
        {
            WorkingDirectory = fixture.Root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(scripts,"analyze-worker"));
        start.Environment.Clear();
        start.Environment["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        start.Environment["CE_CONTAINER_ENGINE"] = engine;
        using var child = Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await child.WaitForExitAsync(timeout.Token);
        Assert.True(child.ExitCode == 0,await stderr);
        Assert.Contains("worker-engine:" + engine,await stdout,StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("docker")]
    [InlineData("podman")]
    public async Task Given_a_native_engine_when_analysis_starts_then_runtime_is_frozen_before_admission(string engine)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Unix qualification required");
        using var fixture = new ParallelFixture(1);
        Directory.CreateDirectory(Path.Combine(fixture.Root,"config/runtime"));
        File.WriteAllText(Path.Combine(fixture.Root,"config/runtime/images.env"),"");
        var executable = Path.Combine(fixture.Root,"scripts/container-engine");
        var snapshot = System.Text.Json.JsonSerializer.Serialize(new
        {
            backend = engine, executable = "/fixture/" + engine,
            prefixArguments = engine == "docker" ? new[] { "--host","unix:///fixture.sock" } : new[] { "--remote=false" },
            fingerprint = new string('a',64),
        });
        File.WriteAllText(executable,"#!/bin/sh\nprintf '%s' \"$CE_CONTAINER_ENGINE\" > runtime-probe\nprintf '%s' \"$CE_ANALYSIS_RUNTIME\" > runtime-inherited\nprintf '%s\\n' '" + snapshot + "'\n");
        File.SetUnixFileMode(executable,UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = fixture.Root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(Fixtures.RepositoryRoot,"platform/src/CapabilityExplorer.Cli/bin/Debug/net10.0/ce.dll"));
        start.ArgumentList.Add("analysis-run");
        start.ArgumentList.Add("--capability");
        start.ArgumentList.Add("missing-capability");
        start.Environment["CE_CONTAINER_ENGINE"] = engine;
        start.Environment["CE_ANALYSIS_RUNTIME"] = snapshot;
        start.Environment.Remove("DOTNET_RUNNING_IN_CONTAINER");
        using var child = Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await child.WaitForExitAsync(timeout.Token);
        await stdout;
        Assert.Equal(2,child.ExitCode);
        Assert.True(File.Exists(Path.Combine(fixture.Root,"runtime-probe")),"Selected runtime must be validated before capability admission. " + await stderr);
        Assert.Equal(engine,File.ReadAllText(Path.Combine(fixture.Root,"runtime-probe")));
        Assert.Equal(snapshot,File.ReadAllText(Path.Combine(fixture.Root,"runtime-inherited")));
    }

    [Fact]
    public async Task Given_offline_extraction_when_a_source_is_selected_then_the_host_exports_it_as_a_readonly_mount()
    {
        using var fixture = new ParallelFixture(1);
        var source = Path.Combine(fixture.Work,"src","repo-0");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(fixture.Root,"scripts/sandbox"),"#!/bin/sh\nprintf '%s' \"$SANDBOX_SOURCES\"\n");
        using var process = new AnalysisProcess(fixture.Root,fixture.Store,fixture.Run,CancellationToken.None,new Runtime());
        var containerPath = "/work/" + Path.GetRelativePath(fixture.Root,source).Replace('\\','/');

        var output = await process.Run("scripts/sandbox",["--offline","node","extract","--source",containerPath],capture:true);

        Assert.Equal(source,output);
        Assert.True(process.TerminationConfirmed);
    }

    [Theory]
    [InlineData("scripts/container-engine")]
    [InlineData("scripts/lib/analysis-runtime.ts")]
    public void Given_runtime_bridge_code_when_it_changes_then_the_analysis_runtime_identity_changes(string changed)
    {
        using var fixture = new ParallelFixture(1);
        var files = new[]
        {
            "config/runtime/images.env","scripts/sandbox","scripts/github-token","scripts/lib/container-engine.sh",
            "scripts/container-engine","scripts/lib/analysis-runtime.ts","scripts/fetch-npm-packages.mjs",
            "platform/src/CapabilityExplorer.Cli/bin/Debug/net10.0/ce.dll",
            "platform/src/CapabilityExplorer.Extractors.CSharp/bin/Debug/net10.0/ce-cs.dll",
            "extractors/ts/src/cli.ts","extractors/ts/package-lock.json",
        };
        foreach (var file in files)
        {
            var path = Path.Combine(fixture.Root,file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path)) File.WriteAllText(path,"fixture-input");
        }
        var before = AnalysisPreparation.RuntimeIdentity(fixture.Root);
        File.AppendAllText(Path.Combine(fixture.Root,changed),"-changed");

        Assert.NotEqual(before,AnalysisPreparation.RuntimeIdentity(fixture.Root));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Given_failed_sandbox_execution_when_only_label_cleanup_is_available_then_execution_remains_unknown(bool cleanupConfirmed, bool independentUncertainty)
    {
        using var fixture = new ParallelFixture(1);
        File.WriteAllText(Path.Combine(fixture.Root,"fail"),"");
        File.WriteAllText(Path.Combine(fixture.Root,"release"),"");
        using var process = new AnalysisProcess(fixture.Root,fixture.Store,fixture.Run,CancellationToken.None,new Runtime(cleanupConfirmed));
        if (independentUncertainty) process.MarkUncertainAcquisition();

        await Assert.ThrowsAsync<InvalidOperationException>(() => process.Run("scripts/sandbox",
            ["ce-cs.dll","--repository","repo-0","--out",fixture.Repositories[0].Staging]));

        Assert.False(process.TerminationConfirmed);
        var completed = fixture.Store.FinishExecution(fixture.Owner,new(fixture.Run.RunId,fixture.Run.AttemptId!),1,process.TerminationConfirmed);
        Assert.Equal("unknown",completed.State);
        Assert.All(fixture.Repositories,repository => Assert.Empty(Directory.GetFiles(repository.Staging)));
    }

    [Fact]
    public async Task Given_a_detached_host_descendant_when_its_parent_fails_then_termination_remains_unconfirmed()
    {
        using var fixture = new ParallelFixture(1);
        using var process = new AnalysisProcess(fixture.Root,fixture.Store,fixture.Run,CancellationToken.None,new Runtime());
        await Assert.ThrowsAsync<InvalidOperationException>(() => process.Run("/bin/sh",
            ["-c","/bin/sleep 30 </dev/null >/dev/null 2>&1 & printf '%s' \"$!\" > child.pid; exit 9"]));
        using var survivor = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(fixture.Root,"child.pid"))));
        try
        {
            Assert.False(survivor.HasExited);
            Assert.False(process.TerminationConfirmed);
            Assert.Equal("unknown",fixture.Store.FinishExecution(fixture.Owner,new(fixture.Run.RunId,fixture.Run.AttemptId!),1,process.TerminationConfirmed).State);
        }
        finally
        {
            if (!survivor.HasExited) survivor.Kill(entireProcessTree:true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await survivor.WaitForExitAsync(timeout.Token);
        }
    }

    private sealed class Runtime(bool cleanupConfirmed = true) : IAnalysisContainerRuntime
    {
        public Task EnsureFrozen() => Task.CompletedTask;
        public void ApplyEnvironment(System.Diagnostics.ProcessStartInfo start) { }
        public Task<bool> CleanupOwned(string label) => Task.FromResult(cleanupConfirmed);
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
                printf '%s\n' "$kind-$repo" >> started
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
        public async Task WaitForFirstChild(CancellationToken stop)
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watcher = new FileSystemWatcher(Path.Combine(Root,"active"),"cs-repo-0");
            watcher.Created += (_, _) => started.TrySetResult();
            watcher.EnableRaisingEvents = true;
            if (File.Exists(Path.Combine(Root,"active/cs-repo-0"))) started.TrySetResult();
            await started.Task.WaitAsync(stop);
        }
        public void Dispose() { Owner.Dispose(); Directory.Delete(Root,true); }
    }
}
