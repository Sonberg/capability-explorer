extern alias analysiscli;
using System.Text.Json;
using CapabilityExplorer.Capabilities.Analysis;
using analysiscli::CapabilityExplorer.Cli;
using Xunit;

namespace CapabilityExplorer.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class AnalysisPackageTests
{
    [Theory]
    [InlineData("E401", "authentication")]
    [InlineData("E403", "authorization")]
    [InlineData("E404", "package-not-found")]
    [InlineData("CERT_HAS_EXPIRED", "tls")]
    [InlineData("ETIMEDOUT", "network-timeout")]
    [InlineData("ERESOLVE", "dependency-conflict")]
    [InlineData("UNRECOGNIZED", "unknown")]
    public async Task Package_credentials_and_untrusted_fetch_output_do_not_enter_run_evidence_or_offline_children(string errorCode, string category)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Unix qualification required");
        var root = Path.Combine(Path.GetTempPath().Replace("/var/","/private/var/",StringComparison.Ordinal),"ce-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var host = root + "-host";
        Directory.CreateDirectory(host, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var priorPath = Environment.GetEnvironmentVariable("PATH");
        var priorFeeds = Environment.GetEnvironmentVariable("CE_PACKAGE_FEEDS_FILE");
        var priorNpm = Environment.GetEnvironmentVariable("npm_config_userconfig");
        var priorGithub = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        const string secret = "synthetic-fixture-secret-never-log";
        try
        {
            void Write(string relative,string text,bool executable=false)
            {
                var path = Path.Combine(root,relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path,text);
                if (executable) File.SetUnixFileMode(path,UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            Write("capabilities.json","""{"capabilities":[{"id":"sales"}]}""");
            var hostFeeds = Path.Combine(host,"feeds.json");
            File.WriteAllText(hostFeeds,JsonSerializer.Serialize(new { feeds=new[] { new { id="private",kind="npm",name="Private",url="https://packages.example.invalid/",scope="@fixture",secret } } }));
            File.SetUnixFileMode(hostFeeds,UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Write("npmrc","");
            Write("bin/node","#!/bin/sh\nprintf '%s' \"$CE_PACKAGE_FEEDS\"\n",true);
                        Write("scripts/sandbox",string.Join('\n',new[]
                        {
                                "#!/bin/sh",
                                "if [ \"$1\" = --offline ]; then",
                                "  printf '%s' \"${CE_PACKAGE_FEEDS-ABSENT}\" > offline-environment",
                                "  printf '%s' \"${GITHUB_TOKEN-ABSENT}\" > offline-github",
                                "  exit 0",
                                "fi",
                                "printf '%s' \"$CE_PACKAGE_FEEDS\" > package-environment",
                                "echo synthetic-fixture-secret-never-log",
                                "echo synthetic-fixture-secret-never-log >&2",
                                "case \"$*\" in",
                                "  *fetch-packages*) echo 'packages: 0 downloaded, 0 already present, 0 unavailable;' ;;",
                                "  *)",
                                $"    echo 'npm error code {errorCode}' >&2",
                                "    echo 'https://user:synthetic-fixture-secret-never-log@packages.example.invalid/?token=synthetic-fixture-secret-never-log' >&2",
                                "    echo 'npm packages: 0 lockfile-backed project(s) installed, 1 unavailable;'",
                                "    ;;",
                                "esac",
                                ""
                        }),true);
            Environment.SetEnvironmentVariable("PATH",Path.Combine(root,"bin") + Path.PathSeparator + priorPath);
            Environment.SetEnvironmentVariable("CE_PACKAGE_FEEDS_FILE",hostFeeds);
            Environment.SetEnvironmentVariable("npm_config_userconfig",Path.Combine(root,"npmrc"));
            Environment.SetEnvironmentVariable("GITHUB_TOKEN",secret);
            var store = new FileRunStore(Path.Combine(root,".sandbox-cache/requests"));
            store.Admit("sales",Path.Combine(root,"capabilities.json"),null,new());
            using var owner = store.AcquireWorker();
            var run = store.ClaimNext(owner)!;
            var work = store.AttemptDirectory(new(run.RunId,run.AttemptId!));
            var source = Path.Combine(work,"src/repo"); Directory.CreateDirectory(source);
            var locks = Path.Combine(work,"locks"); Directory.CreateDirectory(locks);
            File.WriteAllText(Path.Combine(source,"package-lock.json"),"{}");
            using var process = new AnalysisProcess(root,store,run,CancellationToken.None,new Runtime());
            var inputs = await AnalysisPackagePreparation.Run(root,run,process,
                [new("repo",new string('a',40),source,Path.Combine(work,"out/repo"))],work,locks,Path.Combine(root,"packages"));
            Assert.Contains(secret,File.ReadAllText(Path.Combine(root,"package-environment")),StringComparison.Ordinal);
            Assert.DoesNotContain(secret,JsonSerializer.Serialize(inputs),StringComparison.Ordinal);
            await process.Run("scripts/sandbox",["--offline","node"]);
            Assert.Equal("ABSENT",File.ReadAllText(Path.Combine(root,"offline-environment")));
            Assert.Equal("ABSENT",File.ReadAllText(Path.Combine(root,"offline-github")));
            Assert.DoesNotContain(secret,store.ReadLog(run.RunId) ?? "",StringComparison.Ordinal);
            Assert.DoesNotContain(secret,File.ReadAllText(Path.Combine(store.RunDirectory(run.RunId),"accepted.json")),StringComparison.Ordinal);
            var log = store.ReadLog(run.RunId) ?? "";
            var telemetry = log.Split('\n').Where(line => line.StartsWith("-- telemetry: ",StringComparison.Ordinal))
                .Select(line => JsonDocument.Parse(line[14..]).RootElement.Clone()).ToArray();
            Assert.Contains(telemetry, entry => entry.GetProperty("stage").GetString() == "packages.nuget");
            var npm = Assert.Single(telemetry, entry => entry.GetProperty("stage").GetString() == "packages.npm");
            Assert.Equal(run.RunId,npm.GetProperty("runId").GetString());
            Assert.Equal(run.AttemptId,npm.GetProperty("attemptId").GetString());
            Assert.True(npm.GetProperty("elapsedMilliseconds").GetDouble() >= 0);
            Assert.Equal("partial",npm.GetProperty("outcome").GetString());
            Assert.Equal(0,npm.GetProperty("installed").GetInt32());
            Assert.Equal(1,npm.GetProperty("unavailable").GetInt32());
            Assert.Contains(category,npm.GetProperty("failureCategories").EnumerateArray().Select(value => value.GetString()));
            Assert.DoesNotContain("packages.example.invalid",log,StringComparison.Ordinal);
            Assert.DoesNotContain(errorCode,log,StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH",priorPath);
            Environment.SetEnvironmentVariable("CE_PACKAGE_FEEDS_FILE",priorFeeds);
            Environment.SetEnvironmentVariable("npm_config_userconfig",priorNpm);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN",priorGithub);
            Directory.Delete(root,true);
            Directory.Delete(host,true);
        }
    }
    [Theory]
    [InlineData("matching", true)]
    [InlineData("runtime", false)]
    [InlineData("source", false)]
    [InlineData("feeds", false)]
    [InlineData("manifest", false)]
    [InlineData("tampered", false)]
    [InlineData("symlink", false)]
    [InlineData("unknown", false)]
    [InlineData("new-shape-keys", true)]
    public async Task Given_retained_packages_when_another_attempt_starts_then_only_bound_matching_completed_content_is_reused(string variation, bool reuse)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Unix qualification required");
        var root = Path.Combine(Path.GetTempPath().Replace("/var/","/private/var/",StringComparison.Ordinal),"ce-package-reuse-" + Guid.NewGuid().ToString("N"));
        var host = root + "-host";
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(host,UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var priorPath = Environment.GetEnvironmentVariable("PATH");
        var priorFeeds = Environment.GetEnvironmentVariable("CE_PACKAGE_FEEDS_FILE");
        var priorNpm = Environment.GetEnvironmentVariable("npm_config_userconfig");
        try
        {
            void Write(string relative, string text, bool executable = false)
            {
                var file = Path.Combine(root,relative);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file,text);
                if (executable) File.SetUnixFileMode(file,UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            Write("capabilities.json","{\"capabilities\":[{\"id\":\"sales\"}]}");
            Write("npmrc","");
            Write("bin/node","#!/bin/sh\nprintf '%s' \"$CE_PACKAGE_FEEDS\"\n",true);
            Write("scripts/sandbox",string.Join('\n',new[]
            {
                "#!/bin/sh", "case \"$*\" in", "  *fetch-packages*)",
                "    while [ \"$#\" -gt 0 ]; do if [ \"$1\" = --packages ]; then shift; packages=\"$1\"; fi; shift; done",
                "    packages=\"$PWD/${packages#/work/}\"",
                "    if [ -f \"$packages/fixture/1.0.0/fixture.nuspec\" ]; then",
                "      echo 'packages: 0 downloaded, 1 already present, 0 unavailable;'",
                "    else",
                "      mkdir -p \"$packages/fixture/1.0.0\"",
                "      printf fixture > \"$packages/fixture/1.0.0/fixture.nuspec\"",
                "      echo 'packages: 1 downloaded, 0 already present, 0 unavailable;'",
                "    fi ;;",
                "  *) echo 'npm packages: 0 lockfile-backed project(s) installed, 0 unavailable;' ;;",
                "esac", ""
            }),true);
            var feeds = Path.Combine(host,"feeds.json");
            File.WriteAllText(feeds,"{\"feeds\":[]}");
            File.SetUnixFileMode(feeds,UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Environment.SetEnvironmentVariable("PATH",Path.Combine(root,"bin") + Path.PathSeparator + priorPath);
            Environment.SetEnvironmentVariable("CE_PACKAGE_FEEDS_FILE",feeds);
            Environment.SetEnvironmentVariable("npm_config_userconfig",Path.Combine(root,"npmrc"));
            var store = new FileRunStore(Path.Combine(root,".sandbox-cache/requests"));
            using var owner = store.AcquireWorker();
            var settings = new AnalysisSettings(RuntimeIdentity:new string('b',64));
            (AnalysisRun Run, string Work, string Packages, AnalysisRepository Repository) Start(AnalysisSettings options)
            {
                Assert.True(store.Admit("sales",Path.Combine(root,"capabilities.json"),null,options).Accepted);
                var run = store.ClaimNext(owner)!;
                var work = store.AttemptDirectory(new(run.RunId,run.AttemptId!));
                var source = Path.Combine(work,"src/repo");
                Directory.CreateDirectory(source);
                Directory.CreateDirectory(Path.Combine(work,"locks"));
                Directory.CreateDirectory(Path.Combine(work,"packages"));
                File.WriteAllText(Path.Combine(source,"source.txt"),"same-source");
                return (run,work,Path.Combine(work,"packages"),new("repo",new string('a',40),source,Path.Combine(work,"out/repo")));
            }
            var first = Start(settings);
            Dictionary<string,string> inputs;
            using (var process = new AnalysisProcess(root,store,first.Run,CancellationToken.None,new Runtime()))
                inputs = await AnalysisPackagePreparation.Run(root,first.Run,process,[first.Repository],first.Work,Path.Combine(first.Work,"locks"),first.Packages);
            Assert.Equal("1:0:0",inputs["nuget-coverage:repo"]);
            inputs["runtime"] = settings.RuntimeIdentity!;
            inputs["revision:repo"] = first.Repository.Sha;
            inputs["source:repo"] = AnalysisPackagePreparation.SourceIdentity(first.Repository.Source);
            inputs["package-cache"] = AnalysisWorkerCommand.HashText(string.Join('\n',AnalysisPreparation.CheckedFiles(first.Packages).Order(StringComparer.Ordinal)
                .Select(file => Path.GetRelativePath(first.Packages,file) + ":" + AnalysisWorkerCommand.HashFile(file))));
            if (variation == "new-shape-keys")
            {
                // New shadow-adapter receipt keys must not change whether a prior, otherwise-matching attempt is reused.
                inputs["tree:repo"] = new string('d',40);
                inputs["adapter-shadow-status:repo"] = "ok";
                inputs["adapter-plan:repo"] = new string('e',64);
                inputs["adapter-census:repo"] = "complete=1 unknown=0 skipped=0 units=1/1/0/0/0 inputs=1/1/0/0/0";
                inputs["adapter-manifest"] = new string('f',64);
                inputs["adapter-recipe"] = new string('f',64);
                inputs["adapter-host-runtime"] = ".NET 10.0.12";
            }
            var identity = new AttemptIdentity(first.Run.RunId,first.Run.AttemptId!);
            var manifest = store.BindResolved(owner,identity,new(first.Run.AcceptedInputsHash,inputs));
            store.FinishExecution(owner,identity,0);
            using (var publication = store.BeginPublication(owner,identity,manifest))
            {
                publication.Intent("test","packages");
                publication.Confirm("test","packages",new string('a',64));
                publication.Complete();
            }
            var second = Start(variation == "runtime" ? settings with { RuntimeIdentity = new string('c',64) } : settings);
            var original = Path.Combine(first.Packages,"fixture/1.0.0/fixture.nuspec");
            if (variation == "source") File.AppendAllText(Path.Combine(second.Repository.Source,"source.txt"),"changed");
            if (variation == "feeds") File.WriteAllText(feeds,"{\"feeds\":[{\"id\":\"other\",\"kind\":\"nuget\",\"url\":\"https://other.invalid/\"}]}");
            if (variation == "manifest") File.AppendAllText(Path.Combine(store.RunDirectory(first.Run.RunId),"resolved.json")," ");
            if (variation == "tampered") File.WriteAllText(original,"tampered");
            if (variation == "symlink")
            {
                var target = Path.Combine(host,"sentinel");
                File.WriteAllText(target,"preserve");
                File.Delete(original);
                File.CreateSymbolicLink(original,target);
            }
            if (variation == "unknown")
            {
                var file = Path.Combine(store.RunDirectory(first.Run.RunId),"run.json");
                var record = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
                record["state"] = "unknown";
                File.WriteAllText(file,record.ToJsonString());
            }
            using (var process = new AnalysisProcess(root,store,second.Run,CancellationToken.None,new Runtime()))
                inputs = await AnalysisPackagePreparation.Run(root,second.Run,process,[second.Repository],second.Work,Path.Combine(second.Work,"locks"),second.Packages);
            Assert.Equal(reuse ? "0:1:0" : "1:0:0",inputs["nuget-coverage:repo"]);
            Assert.Equal("fixture",File.ReadAllText(Path.Combine(second.Packages,"fixture/1.0.0/fixture.nuspec")));
            Assert.Equal(variation == "tampered" ? "tampered" : variation == "symlink" ? "preserve" : "fixture",File.ReadAllText(original));
            if (reuse)
            {
                var telemetry = (store.ReadLog(second.Run.RunId) ?? "").Split('\n')
                    .Where(line => line.StartsWith("-- telemetry: ",StringComparison.Ordinal))
                    .Select(line => JsonDocument.Parse(line[14..]).RootElement.Clone()).ToArray();
                foreach (var stage in new[] { "packages.reuse.verify", "packages.reuse.copy", "packages.reuse.verify-copy", "packages.reuse.verify-source" })
                {
                    var entry = Assert.Single(telemetry,value => value.GetProperty("stage").GetString() == stage);
                    Assert.Equal("completed",entry.GetProperty("outcome").GetString());
                    Assert.True(entry.GetProperty("elapsedMilliseconds").GetDouble() >= 0);
                }
                Assert.Single(telemetry,value => value.GetProperty("stage").GetString() == "packages.reuse.inventory");
                File.WriteAllText(Path.Combine(second.Packages,"fixture/1.0.0/fixture.nuspec"),"independent-copy");
                Assert.Equal("fixture",File.ReadAllText(original));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH",priorPath);
            Environment.SetEnvironmentVariable("CE_PACKAGE_FEEDS_FILE",priorFeeds);
            Environment.SetEnvironmentVariable("npm_config_userconfig",priorNpm);
            Directory.Delete(root,true);
            Directory.Delete(host,true);
        }
    }

    private sealed class Runtime : IAnalysisContainerRuntime
    {
        public Task EnsureFrozen() => Task.CompletedTask;
        public void ApplyEnvironment(System.Diagnostics.ProcessStartInfo start) { }
        public Task<bool> CleanupOwned(string label) => Task.FromResult(true);
    }
}
