extern alias analysiscli;
using System.Text.Json;
using System.Diagnostics;
using CapabilityExplorer.Capabilities;
using CapabilityExplorer.Capabilities.Analysis;
using analysiscli::CapabilityExplorer.Cli;
using Xunit;

namespace CapabilityExplorer.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class AnalysisPackageTests
{
    [Fact]
    public void Eligible_selection_rejects_incomplete_unknown_and_unsupported_plans_before_package_dispatch()
    {
        var counts = new AdapterPlanCounts(0,0,0,0,0);
        var plan = new NativeAdapterPlan("ce.adapter.plan.v1","repo",new('1',40),new('2',40),"csharp.roslyn","0.2.0",
            new('a',64),new('b',64),new('c',64),true,0,counts,counts,[],[],[],[]);
        foreach (var blocked in new[]
        {
            plan with { EnumerationComplete = false },
            plan with { UnknownCount = 1 },
            plan with { UnitCounts = counts with { Unsupported = 1 } },
        })
            Assert.Throws<InvalidDataException>(() => AnalysisAdapterShadow.SelectEligibleProjects(blocked,new('d',64),includeTests:true));
    }

    [Theory]
    [InlineData("incomplete", "incomplete:export-skipped")]
    [InlineData("unknown", "limit-exceeded:file-byte-limit")]
    [InlineData("unsupported", "eligible C# dispatch blocked by incomplete, unknown, or unsupported C# census")]
    [InlineData("cap", "limit-exceeded:unit-limit-exceeded")]
    public async Task Eligible_preflight_blocks_incomplete_unknown_unsupported_and_capped_plans_before_any_package_child(
        string scenario, string expectedEvidence)
    {
        using var fixture = new EligiblePreparationFixture(scenario);
        var runtime = new Runtime();
        using var process = new AnalysisProcess(fixture.Root,fixture.Store,fixture.Run,CancellationToken.None,runtime);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            AnalysisPreparation.Prepare(fixture.Root,fixture.Store,fixture.Run,process));

        Assert.Contains(expectedEvidence,error.Message.Contains("census",StringComparison.Ordinal)
            ? error.Message : fixture.Store.ReadLog(fixture.Run.RunId) ?? "",StringComparison.Ordinal);
        Assert.Equal(0,runtime.SandboxInvocations);
        Assert.False(File.Exists(Path.Combine(fixture.Root,"package-child-calls")));
        var log = fixture.Store.ReadLog(fixture.Run.RunId) ?? "";
        Assert.DoesNotContain("packages.nuget",log,StringComparison.Ordinal);
        Assert.DoesNotContain("packages.npm",log,StringComparison.Ordinal);
    }

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
        var root = Path.Combine(AppContext.BaseDirectory,"ce-package-" + Guid.NewGuid().ToString("N"));
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
            foreach (var file in new[] { "manifest.json", "recipe.json" })
            {
                var destination = Path.Combine(root,"config/adapters/csharp.roslyn",file);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(Path.Combine(Fixtures.RepositoryRoot,"config/adapters/csharp.roslyn",file),destination);
            }
            var hostFeeds = Path.Combine(host,"feeds.json");
            File.WriteAllText(hostFeeds,JsonSerializer.Serialize(new { feeds=new[] { new { id="private",kind="npm",name="Private",url="https://packages.example.invalid/",scope="@fixture",secret } } }));
            File.SetUnixFileMode(hostFeeds,UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Write("npmrc","");
            Write("bin/node","#!/bin/sh\nprintf '%s' \"$CE_PACKAGE_FEEDS\"\n",true);
                        Write("scripts/sandbox",string.Join('\n',new[]
                        {
                                "#!/bin/sh",
                                "printf '%s\\n' \"$*\" >> calls",
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
            store.Admit("sales",Path.Combine(root,"capabilities.json"),null,new(EligibleCSharp:true));
            using var owner = store.AcquireWorker();
            var run = store.ClaimNext(owner)!;
            var work = store.AttemptDirectory(new(run.RunId,run.AttemptId!));
            var source = Path.Combine(work,"src/repo"); Directory.CreateDirectory(source);
            var locks = Path.Combine(work,"locks"); Directory.CreateDirectory(locks);
            File.WriteAllText(Path.Combine(source,"package-lock.json"),"{}");
            var excludedSource = Path.Combine(work,"src/repo-excluded"); Directory.CreateDirectory(excludedSource);
            var repositories = new[]
            {
                new AnalysisRepository("repo",new string('a',40),source,Path.Combine(work,"out/repo")),
                new AnalysisRepository("repo-excluded",new string('b',40),excludedSource,Path.Combine(work,"out/repo-excluded")),
            };
            var shadow = await AnalysisAdapterShadow.LoadAsync(root,CancellationToken.None);
            var sourceIdentities = repositories.ToDictionary(repository => repository.Id,
                repository => AnalysisPackagePreparation.SourceIdentity(repository.Source),StringComparer.Ordinal);
            var selections = new Dictionary<string,EligibleCSharpSelection>(StringComparer.Ordinal)
            {
                ["repo"] = new(["Api/Api.csproj"],new string('a',64),sourceIdentities["repo"],shadow.ManifestSha256,1,0,1,"selected"),
                ["repo-excluded"] = new([],new string('b',64),sourceIdentities["repo-excluded"],shadow.ManifestSha256,1,1,0,"test-policy-excluded"),
            };
            using var process = new AnalysisProcess(root,store,run,CancellationToken.None,new Runtime());
            var inputs = await AnalysisPackagePreparation.Run(root,run,process,repositories,work,locks,Path.Combine(root,"packages"),
                selections,shadow,sourceIdentities);
            Assert.Single(File.ReadAllLines(Path.Combine(root,"calls")),
                command => command.Contains("fetch-packages-selected-v1",StringComparison.Ordinal) &&
                    command.Contains("--project Api/Api.csproj",StringComparison.Ordinal));
            Assert.DoesNotContain(File.ReadAllLines(Path.Combine(root,"calls")),command =>
                command.Contains("fetch-packages",StringComparison.Ordinal) &&
                command.Contains("--source /work/",StringComparison.Ordinal) &&
                command.Contains("repo-excluded",StringComparison.Ordinal));
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
            var nuget = Assert.Single(telemetry, entry => entry.GetProperty("stage").GetString() == "packages.nuget");
            Assert.Equal("repo",nuget.GetProperty("repositoryId").GetString());
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
        var root = Path.Combine(AppContext.BaseDirectory,"ce-package-reuse-" + Guid.NewGuid().ToString("N"));
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
        public int SandboxInvocations { get; private set; }
        public Task EnsureFrozen() { SandboxInvocations++; return Task.CompletedTask; }
        public void ApplyEnvironment(System.Diagnostics.ProcessStartInfo start) { }
        public Task<bool> CleanupOwned(string label) => Task.FromResult(true);
    }

    private sealed class EligiblePreparationFixture : IDisposable
    {
        private readonly string _repository;
        public string Root { get; } = Path.Combine(Fixtures.RepositoryRoot,".sandbox-cache",
            "eligible-preflight-" + Guid.NewGuid().ToString("N"));
        public FileRunStore Store { get; }
        public WorkerOwnership Owner { get; }
        public AnalysisRun Run { get; }

        public EligiblePreparationFixture(string scenario)
        {
            if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Unix host qualification required");
            CopyRuntimeInputs(scenario);
            _repository = Path.Combine(Root,"input-repository");
            Directory.CreateDirectory(Path.Combine(_repository,"Api"));
            File.WriteAllText(Path.Combine(_repository,"Api/Api.csproj"),"<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
            File.WriteAllText(Path.Combine(_repository,"Api/Api.cs"),"public sealed class Api {}");
            switch (scenario)
            {
                case "incomplete":
                    File.CreateSymbolicLink(Path.Combine(_repository,"Api/linked.cs"),"missing.cs");
                    break;
                case "unknown":
                    File.WriteAllBytes(Path.Combine(_repository,"Api/large.cs"),new byte[2048]);
                    break;
                case "unsupported":
                    File.WriteAllText(Path.Combine(_repository,"Api/unsupported.cshtml"),"@* unsupported embedded input *@");
                    break;
                case "cap":
                    File.Delete(Path.Combine(_repository,"Api/Api.csproj"));
                    File.Delete(Path.Combine(_repository,"Api/Api.cs"));
                    for (var index = 0; index < 101; index++)
                    {
                        var project = Path.Combine(_repository,$"Project{index:D3}");
                        Directory.CreateDirectory(project);
                        File.WriteAllText(Path.Combine(project,$"Project{index:D3}.csproj"),"<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
                        File.WriteAllText(Path.Combine(project,"Source.cs"),"public sealed class Source {}");
                    }
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(scenario));
            }

            Git("init","--quiet",_repository);
            Git("-C",_repository,"config","user.name","fixture");
            Git("-C",_repository,"config","user.email","fixture@example.invalid");
            Git("-C",_repository,"add","--all");
            Git("-C",_repository,"commit","--quiet","-m","fixture");
            var configuration = Path.Combine(Root,"capabilities.json");
            File.WriteAllText(configuration,JsonSerializer.Serialize(new
            {
                capabilities = new[] { new { id = "sales", repositories = new[]
                {
                    new { id = "repo", location = _repository, kind = "local", @ref = "HEAD" }
                } } }
            }));
            Store = new FileRunStore(Path.Combine(Root,".sandbox-cache/requests"));
            Assert.True(Store.Admit("sales",configuration,null,
                new(Packages:true,GitHubAuth:"none",EligibleCSharp:true)).Accepted);
            Owner = Store.AcquireWorker();
            Run = Store.ClaimNext(Owner)!;
        }

        private void CopyRuntimeInputs(string scenario)
        {
            var sourceRoot = Fixtures.RepositoryRoot;
            void Copy(string relative, bool executable = false)
            {
                var source = Path.Combine(sourceRoot,relative);
                var destination = Path.Combine(Root,relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source,destination);
                if (executable && !OperatingSystem.IsWindows())
                    File.SetUnixFileMode(destination,UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            foreach (var path in new[]
            {
                "config/runtime/images.env", "config/adapters/csharp.roslyn/manifest.json",
                "config/adapters/csharp.roslyn/recipe.json", "scripts/github-token",
                "scripts/container-engine", "scripts/lib/container-engine.sh",
                "scripts/lib/analysis-runtime.ts", "scripts/fetch-npm-packages.mjs",
                "extractors/ts/package-lock.json"
            }) Copy(path,path == "scripts/github-token");
            if (scenario == "unknown")
            {
                var manifest = Path.Combine(Root,"config/adapters/csharp.roslyn/manifest.json");
                var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifest))!;
                document["limits"]!["maxFileBytes"] = 1024;
                File.WriteAllText(manifest,document.ToJsonString());
            }
            var sandbox = Path.Combine(Root,"scripts/sandbox");
            Directory.CreateDirectory(Path.GetDirectoryName(sandbox)!);
            File.WriteAllText(sandbox,"#!/bin/sh\nprintf '%s\\n' \"$*\" >> package-child-calls\nexit 97\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(sandbox,UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (var relative in new[]
            {
                "platform/src/CapabilityExplorer.Cli/bin/Debug/net10.0",
                "platform/src/CapabilityExplorer.Extractors.CSharp/bin/Debug/net10.0"
            })
            {
                foreach (var file in Directory.EnumerateFiles(Path.Combine(sourceRoot,relative),"*.dll"))
                {
                    var destination = Path.Combine(Root,Path.GetRelativePath(sourceRoot,file));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file,destination);
                }
            }
            var parser = Path.Combine(sourceRoot,"extractors/ts/src");
            foreach (var file in Directory.EnumerateFiles(parser,"*",SearchOption.AllDirectories))
            {
                var destination = Path.Combine(Root,Path.GetRelativePath(sourceRoot,file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file,destination);
            }
        }

        private static void Git(params string[] arguments)
        {
            var start = new ProcessStartInfo("git")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            var errors = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0,$"git fixture setup failed: {output}{errors}");
        }

        public void Dispose()
        {
            Owner.Dispose();
            Directory.Delete(Root,true);
        }
    }
}
