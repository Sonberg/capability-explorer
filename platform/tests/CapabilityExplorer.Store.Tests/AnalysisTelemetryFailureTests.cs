extern alias analysiscli;
using System.Diagnostics;
using System.Text.Json;
using CapabilityExplorer.Capabilities.Analysis;
using analysiscli::CapabilityExplorer.Cli;
using Xunit;

namespace CapabilityExplorer.Tests;

[Collection("Local publication environment")]
public sealed class AnalysisTelemetryFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Later_extraction_failure_or_cancellation_preserves_completed_and_failed_stage_timings(bool cancel)
    {
        using var fixture = new TelemetryFixture();
        fixture.Write("scripts/sandbox", """
            #!/bin/sh
            kind=cs; output=
            case "$*" in *extract-sql*) kind=sql;; *cli.ts*) kind=ts;; esac
            while [ "$#" -gt 0 ]; do
              if [ "$1" = --out ]; then shift; output="$1"; fi
              shift
            done
            printf '%s\n' "$kind" >> started
            if [ "$kind" = ts ]; then
              touch waiting
              while [ ! -f release ]; do sleep .01; done
              exit 9
            fi
            output="$PWD/${output#/work/}"
            mkdir -p "$output"
            printf 'synthetic artifact' > "$output/$kind.jsonl"
            """ + "\n", executable: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var process = fixture.Process(deadline.Token);
        var execution = AnalysisParallelExtraction.Run(fixture.Root, fixture.Store, fixture.Run, process,
            [fixture.Repository], fixture.Work, fixture.Locks, fixture.Packages);
        try
        {
            while (!File.Exists(Path.Combine(fixture.Root, "waiting")))
            {
                if (execution.IsCompleted) await execution;
                await Task.Delay(10, deadline.Token);
            }
            if (cancel) fixture.Store.Cancel(fixture.Run.RunId);
            else fixture.Write("release", "");

            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await execution);
            else await Assert.ThrowsAsync<InvalidOperationException>(async () => await execution);
            Assert.False(deadline.IsCancellationRequested);
            Assert.Equal(cancel, fixture.Store.ReadRun(fixture.Run.RunId).CancellationRequested);
            Assert.False(process.TerminationConfirmed);
            Assert.Equal(new[] { "cs", "ts" }, File.ReadAllLines(Path.Combine(fixture.Root, "started")));
            Assert.Empty(Directory.GetFiles(fixture.Repository.Staging));
            Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".sandbox-cache/artifacts")));

            var telemetry = fixture.Telemetry();
            Assert.Equal(3, telemetry.Length);
            Assert.Equal("failed", Stage(telemetry, "extraction").GetProperty("outcome").GetString());
            var jobs = telemetry.Where(entry => entry.GetProperty("stage").GetString() == "extraction.job").ToArray();
            Assert.Equal("completed", Assert.Single(jobs, entry => entry.GetProperty("language").GetString() == "cs")
                .GetProperty("outcome").GetString());
            Assert.Equal("failed", Assert.Single(jobs, entry => entry.GetProperty("language").GetString() == "ts")
                .GetProperty("outcome").GetString());
            Assert.All(jobs, entry => Assert.Equal("repo", entry.GetProperty("repositoryId").GetString()));
        }
        finally
        {
            deadline.Cancel();
            fixture.Write("release", "");
            try { await execution; }
            catch (Exception error) when (error is OperationCanceledException or InvalidOperationException) { }
        }
    }

    [Theory]
    [InlineData("nuget")]
    [InlineData("npm")]
    public async Task Missing_package_coverage_reports_failed_timing_without_rewriting_completed_stages(string failingStage)
    {
        using var fixture = new TelemetryFixture();
        using var environment = fixture.PackageEnvironment();
        fixture.Write(failingStage + "-output", "synthetic output without dependency coverage\n");
        using var process = fixture.Process();

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.PreparePackages(process));

        Assert.True(process.TerminationConfirmed);
        Assert.False(fixture.Store.ReadRun(fixture.Run.RunId).CancellationRequested);
        var telemetry = fixture.Telemetry();
        Assert.Equal("completed", Stage(telemetry, "packages.reuse").GetProperty("outcome").GetString());
        var failure = Stage(telemetry, "packages." + failingStage);
        Assert.Equal("failed", failure.GetProperty("outcome").GetString());
        foreach (var counter in new[] { "downloaded", "alreadyPresent", "installed", "unavailable", "failureCategories" })
            Assert.Equal(JsonValueKind.Null, failure.GetProperty(counter).ValueKind);
        if (failingStage == "nuget")
        {
            Assert.DoesNotContain(telemetry, entry => entry.GetProperty("stage").GetString() == "packages.npm");
            Assert.Equal(new[] { "nuget" }, File.ReadAllLines(Path.Combine(fixture.Root, "started")));
        }
        else
        {
            Assert.Equal("partial", Stage(telemetry, "packages.nuget").GetProperty("outcome").GetString());
            Assert.Equal(new[] { "nuget", "npm" }, File.ReadAllLines(Path.Combine(fixture.Root, "started")));
        }
        Assert.DoesNotContain(TelemetryFixture.UntrustedOutput, fixture.Store.ReadLog(fixture.Run.RunId) ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Partial_package_producers_preserve_coverage_counters_and_sanitized_failure_categories()
    {
        using var fixture = new TelemetryFixture();
        using var environment = fixture.PackageEnvironment();
        using var process = fixture.Process();

        var inputs = await fixture.PreparePackages(process);

        Assert.True(process.TerminationConfirmed);
        Assert.Equal("2:3:1", inputs["nuget-coverage:repo"]);
        Assert.Equal("1:2", inputs["npm-coverage"]);
        var telemetry = fixture.Telemetry();
        Assert.Equal("completed", Stage(telemetry, "packages.reuse").GetProperty("outcome").GetString());
        var nuget = Stage(telemetry, "packages.nuget");
        Assert.Equal("repo", nuget.GetProperty("repositoryId").GetString());
        Assert.Equal("partial", nuget.GetProperty("outcome").GetString());
        Assert.Equal(2, nuget.GetProperty("downloaded").GetInt32());
        Assert.Equal(3, nuget.GetProperty("alreadyPresent").GetInt32());
        Assert.Equal(1, nuget.GetProperty("unavailable").GetInt32());
        var npm = Stage(telemetry, "packages.npm");
        Assert.Equal("partial", npm.GetProperty("outcome").GetString());
        Assert.Equal(1, npm.GetProperty("installed").GetInt32());
        Assert.Equal(2, npm.GetProperty("unavailable").GetInt32());
        Assert.Equal(new[] { "authentication", "unknown" },
            npm.GetProperty("failureCategories").EnumerateArray().Select(value => value.GetString()));
        var log = fixture.Store.ReadLog(fixture.Run.RunId) ?? "";
        Assert.DoesNotContain(TelemetryFixture.UntrustedOutput, log, StringComparison.Ordinal);
        Assert.DoesNotContain("E401", log, StringComparison.Ordinal);
        Assert.DoesNotContain("UNRECOGNIZED", log, StringComparison.Ordinal);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(fixture.Store.LogPath(fixture.Run.RunId)));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".sandbox-cache/artifacts")));
    }

    private static JsonElement Stage(IEnumerable<JsonElement> telemetry, string stage) =>
        Assert.Single(telemetry, entry => entry.GetProperty("stage").GetString() == stage);

    private sealed class TelemetryFixture : IDisposable
    {
        internal const string UntrustedOutput = "synthetic-untrusted-fetch-detail";
        private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "ce-telemetry-" + Guid.NewGuid().ToString("N"));
        internal string Root { get; }
        internal FileRunStore Store { get; }
        private WorkerOwnership Owner { get; }
        internal AnalysisRun Run { get; }
        internal string Work { get; }
        internal string Locks => Path.Combine(Work, "locks");
        internal string Packages => Path.Combine(Work, "packages");
        internal AnalysisRepository Repository { get; }

        internal TelemetryFixture()
        {
            if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Unix qualification required");
            Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Root = Directory.CreateDirectory(Path.Combine(_directory, "workspace")).FullName;
            Write("capabilities.json", """{"capabilities":[{"id":"sales"}]}""");
            Store = new(Path.Combine(Root, ".sandbox-cache/requests"));
            Store.Admit("sales", Path.Combine(Root, "capabilities.json"), null,
                new(Packages: true, RuntimeIdentity: new string('a', 64), ExtractionJobs: 1));
            Owner = Store.AcquireWorker();
            Run = Store.ClaimNext(Owner)!;
            Work = Store.AttemptDirectory(new(Run.RunId, Run.AttemptId!));
            Directory.CreateDirectory(Locks);
            Directory.CreateDirectory(Packages);
            var source = Directory.CreateDirectory(Path.Combine(Work, "src/repo")).FullName;
            var staging = Directory.CreateDirectory(Path.Combine(Work, "out/repo", new string('b', 40))).FullName;
            Repository = new("repo", new string('b', 40), source, staging);
            Write("extractors/ts/src/cli.ts", "\"extract\" \"extract-sql\"");
        }

        internal AnalysisProcess Process(CancellationToken stop = default) => new(Root, Store, Run, stop, new Runtime());

        internal Task<Dictionary<string, string>> PreparePackages(AnalysisProcess process) =>
            AnalysisPackagePreparation.Run(Root, Run, process, [Repository], Work, Locks, Packages);

        internal IDisposable PackageEnvironment()
        {
            var feeds = Path.Combine(_directory, "feeds.json");
            File.WriteAllText(feeds, """{"feeds":[]}""");
            File.SetUnixFileMode(feeds, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Write("npmrc", "");
            Write("bin/node", "#!/bin/sh\nprintf '%s' \"$CE_PACKAGE_FEEDS\"\n", executable: true);
            Write("nuget-output", "packages: 2 downloaded, 3 already present, 1 unavailable;\n");
            Write("npm-output", "npm packages: 1 lockfile-backed project(s) installed, 2 unavailable;\n");
            Write("scripts/sandbox", """
                #!/bin/sh
                kind=npm
                case "$*" in *fetch-packages*) kind=nuget;; esac
                printf '%s\n' "$kind" >> started
                cat "$kind-output"
                echo synthetic-untrusted-fetch-detail >&2
                if [ "$kind" = npm ]; then
                  echo 'npm error code E401' >&2
                  echo 'npm error code UNRECOGNIZED' >&2
                fi
                """ + "\n", executable: true);
            return new EnvironmentScope(new Dictionary<string, string>
            {
                ["CE_PACKAGE_FEEDS_FILE"] = feeds,
                ["npm_config_userconfig"] = Path.Combine(Root, "npmrc"),
                ["PATH"] = Path.Combine(Root, "bin") + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            });
        }

        internal void Write(string relative, string text, bool executable = false)
        {
            var path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            if (executable) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        internal JsonElement[] Telemetry()
        {
            var entries = (Store.ReadLog(Run.RunId) ?? "").Split('\n')
                .Where(line => line.StartsWith("-- telemetry: ", StringComparison.Ordinal))
                .Select(line =>
                {
                    using var document = JsonDocument.Parse(line[14..]);
                    return document.RootElement.Clone();
                }).ToArray();
            Assert.NotEmpty(entries);
            Assert.All(entries, entry =>
            {
                Assert.Equal("ce.analysis.telemetry.v1", entry.GetProperty("schema").GetString());
                Assert.Equal(Run.RunId, entry.GetProperty("runId").GetString());
                Assert.Equal(Run.AttemptId, entry.GetProperty("attemptId").GetString());
                var elapsed = entry.GetProperty("elapsedMilliseconds").GetDouble();
                Assert.True(double.IsFinite(elapsed) && elapsed >= 0);
            });
            return entries;
        }

        public void Dispose()
        {
            Owner.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class Runtime : IAnalysisContainerRuntime
    {
        public Task EnsureFrozen() => Task.CompletedTask;
        public void ApplyEnvironment(ProcessStartInfo start) { }
        public Task<bool> CleanupOwned(string label) => Task.FromResult(true);
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previous;

        internal EnvironmentScope(Dictionary<string, string> values)
        {
            _previous = values.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
            foreach (var (name, value) in values) Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            foreach (var (name, value) in _previous) Environment.SetEnvironmentVariable(name, value);
        }
    }
}
