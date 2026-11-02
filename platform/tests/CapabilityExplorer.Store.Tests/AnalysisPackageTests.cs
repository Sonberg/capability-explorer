extern alias analysiscli;
using System.Text.Json;
using CapabilityExplorer.Capabilities.Analysis;
using analysiscli::CapabilityExplorer.Cli;
using Xunit;

namespace CapabilityExplorer.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class AnalysisPackageTests
{
    [Fact]
    public async Task Package_credentials_and_untrusted_fetch_output_do_not_enter_run_evidence_or_offline_children()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Unix qualification required");
        var root = Path.Combine(Path.GetTempPath().Replace("/var/","/private/var/",StringComparison.Ordinal),"ce-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var priorPath = Environment.GetEnvironmentVariable("PATH");
        var priorFeeds = Environment.GetEnvironmentVariable("CE_PACKAGE_FEEDS_FILE");
        var priorNpm = Environment.GetEnvironmentVariable("npm_config_userconfig");
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
            Write("feeds.json",JsonSerializer.Serialize(new { feeds=new[] { new { id="private",kind="npm",name="Private",url="https://packages.example.invalid/",scope="@fixture",secret } } }));
            Write("npmrc","");
            Write("bin/node","#!/bin/sh\nprintf '%s' \"$CE_PACKAGE_FEEDS\"\n",true);
            Write("scripts/sandbox","""
                #!/bin/sh
                if [ "$1" = --offline ]; then
                  printf '%s' "${CE_PACKAGE_FEEDS-ABSENT}" > offline-environment
                  exit 0
                fi
                printf '%s' "$CE_PACKAGE_FEEDS" > package-environment
                echo synthetic-fixture-secret-never-log
                echo synthetic-fixture-secret-never-log >&2
                case "$*" in
                  *fetch-packages*) echo 'packages: 0 downloaded, 0 already present, 0 unavailable;' ;;
                  *) echo 'npm packages: 1 lockfile-backed project(s) installed, 0 unavailable;' ;;
                esac
                """ + "\n",true);
            Environment.SetEnvironmentVariable("PATH",Path.Combine(root,"bin") + Path.PathSeparator + priorPath);
            Environment.SetEnvironmentVariable("CE_PACKAGE_FEEDS_FILE",Path.Combine(root,"feeds.json"));
            Environment.SetEnvironmentVariable("npm_config_userconfig",Path.Combine(root,"npmrc"));
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
            Assert.DoesNotContain(secret,store.ReadLog(run.RunId) ?? "",StringComparison.Ordinal);
            Assert.DoesNotContain(secret,File.ReadAllText(Path.Combine(store.RunDirectory(run.RunId),"accepted.json")),StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH",priorPath);
            Environment.SetEnvironmentVariable("CE_PACKAGE_FEEDS_FILE",priorFeeds);
            Environment.SetEnvironmentVariable("npm_config_userconfig",priorNpm);
            Directory.Delete(root,true);
        }
    }
    private sealed class Runtime : IAnalysisContainerRuntime
    {
        public Task EnsureFrozen() => Task.CompletedTask;
        public void ApplyEnvironment(System.Diagnostics.ProcessStartInfo start) { }
        public Task<bool> CleanupOwned(string label) => Task.FromResult(true);
    }
}
