extern alias analysiscli;
using System.Security.Cryptography;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using CapabilityExplorer.Capabilities;
using CapabilityExplorer.Capabilities.Analysis;
using analysiscli::CapabilityExplorer.Cli;
using CapabilityExplorer.Store;
using Xunit;

namespace CapabilityExplorer.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class AnalysisExecutionTests(PostgresFixture database)
{
    [Theory]
    [InlineData("CE_CONTAINER_ENGINE")]
    [InlineData("CE_NUGET_ALLOWED_ORIGINS")]
    [InlineData("DOCKER_CONTEXT")]
    [InlineData("CONTAINER_HOST")]
    [InlineData("SANDBOX_PASS_ENV")]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("PATH")]
    [InlineData("TOKEN\n")]
    public void Package_credentials_cannot_override_policy_or_use_control_names(string name) =>
        Assert.Throws<ArgumentException>(() => AnalysisPreparation.ValidateSettings(new(NugetPassEnv: name)));

    [Theory]
    [InlineData("https://user:secret@packages.invalid")]
    [InlineData("https://packages.invalid?token=secret")]
    [InlineData("http://packages.invalid")]
    public void Package_origin_policy_cannot_capture_credentials_or_downgrade_transport(string origin) =>
        Assert.Throws<ArgumentException>(() => AnalysisPreparation.ValidateSettings(new(NugetAllowedOrigins: origin)));

    [Fact]
    public void Dangling_and_ancestor_links_are_rejected_before_writes()
    {
        using var f = new ExecutionFixture();
        var linked = Path.Combine(f.Root, "linked");
        Directory.CreateSymbolicLink(linked, Path.Combine(f.Root, "missing"));
        Assert.Throws<IOException>(() => AnalysisPreparation.RejectLinks(Path.Combine(linked, "child")));
        Assert.False(Directory.Exists(Path.Combine(f.Root, "missing")));
    }

    [Fact]
    public async Task Publication_preserves_fact_bytes_and_late_cancellation_cannot_claim_stop()
    {
        using var environment = new DatabaseEnvironment(null);
        using var f = new ExecutionFixture();
        var prepared = f.Prepare();
        using var permit = f.Begin();
        Assert.Equal("too-late", f.Store.Cancel(f.Run.RunId).Outcome);
        await AnalysisPublication.Publish(f.Root, f.Run, prepared, permit);
        var complete = permit.Complete();
        Assert.Equal("done", complete.State);
        Assert.Equal(Fixtures.Golden("web-ts/v1-base/_fixture_web.jsonl"), File.ReadAllText(f.Destination(prepared.Repositories[0])));
        Assert.All(complete.Effects!, effect => Assert.Equal("confirmed", effect.State));
        Assert.Equal("too-late", f.Store.Cancel(f.Run.RunId).Outcome);
    }

    [Fact]
    public async Task Repository_added_after_binding_refuses_before_any_canonical_effect()
    {
        using var environment = new DatabaseEnvironment(null);
        using var f = new ExecutionFixture();
        var prepared = f.Prepare();
        var missing = f.AddRepository("pricing-node/v1-base/_fixture_pricing.jsonl", includeArtifact: false);
        prepared = prepared with { Repositories = [.. prepared.Repositories, missing] };
        using (var permit = f.Begin())
            await Assert.ThrowsAsync<InvalidDataException>(() => AnalysisPublication.Publish(f.Root, f.Run, prepared, permit));
        Assert.False(File.Exists(f.Destination(prepared.Repositories[0])));
        Assert.Equal("unknown", f.Store.ReadRun(f.Run.RunId).State);
    }

    [Fact]
    public async Task Artifact_tamper_after_resolution_is_not_published()
    {
        using var environment = new DatabaseEnvironment(null);
        using var f = new ExecutionFixture();
        var prepared = f.Prepare();
        File.AppendAllText(prepared.Artifacts.Keys.Single(), " ");
        using (var permit = f.Begin())
            await Assert.ThrowsAsync<IOException>(() => AnalysisPublication.Publish(f.Root, f.Run, prepared, permit));
        Assert.False(File.Exists(f.Destination(prepared.Repositories[0])));
        Assert.Equal("unknown", f.Store.ReadRun(f.Run.RunId).State);
    }

    [Fact]
    public async Task Artifact_mutation_after_local_move_cannot_be_rebound_or_imported()
    {
        using var environment = new DatabaseEnvironment(database.ConnectionString);
        using var f = new ExecutionFixture();
        var prepared = f.Prepare();
        var before = await database.CountAsync("artifact");
        AnalysisPublication.BeforeDatabaseImportForTests = () =>
        {
            var destination = f.Destination(prepared.Repositories[0]);
            var rows = File.ReadAllText(destination).TrimEnd('\n').Split('\n');
            var header = System.Text.Json.Nodes.JsonNode.Parse(rows[0])!;
            header["configHash"] = Convert.ToHexStringLower(SHA256.HashData(Guid.NewGuid().ToByteArray()));
            rows[0] = header.ToJsonString();
            File.WriteAllText(destination, string.Join('\n', rows) + "\n");
            return Task.CompletedTask;
        };
        try
        {
            using var permit = f.Begin();
            await Assert.ThrowsAsync<IOException>(() => AnalysisPublication.Publish(f.Root, f.Run, prepared, permit));
        }
        finally { AnalysisPublication.BeforeDatabaseImportForTests = null; }

        Assert.Equal(before, await database.CountAsync("artifact"));
        Assert.Null(await new SnapshotStore(database.DataSource).GetPublishedAsync(f.Run.CapabilityId));
        Assert.Contains(f.Store.ReadRun(f.Run.RunId).Effects!,
            effect => effect.Surface == "local-artifacts" && effect.State == "confirmed");
    }

    [Fact]
    public async Task Caller_cannot_omit_accepted_bindings_before_publication()
    {
        using var f = new ExecutionFixture(withBindings: true);
        var prepared = f.Prepare();
        using (var permit = f.Begin())
            await Assert.ThrowsAsync<InvalidDataException>(() => AnalysisPublication.Publish(f.Root, f.Run with { BindingsPath = null }, prepared, permit));
        Assert.False(File.Exists(f.Destination(prepared.Repositories[0])));
        Assert.Equal("unknown", f.Store.ReadRun(f.Run.RunId).State);
    }

    [Fact]
    public async Task Isolated_export_publication_and_cleanup_bind_the_durable_attempt_and_preserve_other_sources()
    {
        using var environment = new DatabaseEnvironment(null);
        using var f = new ExecutionFixture(exportGroup:Guid.NewGuid().ToString("N"));
        var prepared = f.Prepare();
        var source = prepared.Repositories[0].Source;
        var stale = Path.Combine(f.Store.AttemptDirectory(f.Identity),"src",Guid.NewGuid().ToString("N"),"stale");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale,"sentinel"),"preserve");
        Directory.CreateSymbolicLink(Path.Combine(source,"external-link"),stale);
        Assert.Throws<InvalidOperationException>(() => AnalysisSourceCleanup.AfterPublication(f.Store,f.Run,prepared));
        using (var permit = f.Begin())
        {
            await AnalysisPublication.Publish(f.Root,f.Run,prepared,permit);
            permit.Complete();
        }
        Assert.Throws<InvalidOperationException>(() => AnalysisSourceCleanup.AfterPublication(f.Store,f.Run with { AttemptId=Guid.NewGuid().ToString("N") },prepared));
        var forged = prepared with { Inputs=new(prepared.Inputs) { ["export-path:" + prepared.Repositories[0].Id]=Path.GetRelativePath(f.Store.AttemptDirectory(f.Identity),stale) } };
        Assert.Throws<InvalidDataException>(() => AnalysisSourceCleanup.AfterPublication(f.Store,f.Run,forged));
        Assert.True(Directory.Exists(source));
        AnalysisSourceCleanup.AfterPublication(f.Store,f.Run,prepared);
        Assert.False(Directory.Exists(source));
        Assert.Equal("preserve",File.ReadAllText(Path.Combine(stale,"sentinel")));
    }

    [Fact]
    public async Task Later_repository_failure_retains_confirmed_partial_effect_without_false_success()
    {
        using var environment = new DatabaseEnvironment(null);
        using var f = new ExecutionFixture();
        var prepared = f.Prepare("pricing-node/v1-base/_fixture_pricing.jsonl");
        var second = prepared.Repositories[1];
        var blocking = Path.GetDirectoryName(f.Destination(second))!;
        Directory.CreateDirectory(Path.GetDirectoryName(blocking)!);
        File.WriteAllText(blocking, "preserve external obstruction");
        using (var permit = f.Begin())
            await Assert.ThrowsAsync<IOException>(() => AnalysisPublication.Publish(f.Root, f.Run, prepared, permit));
        Assert.True(File.Exists(f.Destination(prepared.Repositories[0])));
        Assert.Equal("preserve external obstruction", File.ReadAllText(blocking));
        var unknown = f.Store.ReadRun(f.Run.RunId);
        Assert.Equal("unknown", unknown.State);
        Assert.Contains(unknown.Effects!, effect => effect.State == "confirmed");
        Assert.Throws<InvalidOperationException>(() => f.Store.ClaimNext(f.Owner));
    }

    [Fact]
    public async Task Actual_database_commit_before_terminal_receipt_stays_unknown_without_republication()
    {
        using var environment = new DatabaseEnvironment(database.ConnectionString);
        using var f = new ExecutionFixture();
        var prepared = f.Prepare();
        using (var permit = f.Begin())
        {
            await AnalysisPublication.Publish(f.Root, f.Run, prepared, permit);
            Assert.NotNull(await new SnapshotStore(database.DataSource).GetPublishedAsync(f.Run.CapabilityId));
            // Fault boundary: actual effects committed, but terminal journal confirmation never happens.
        }
        var recorded = f.Store.ReadRun(f.Run.RunId);
        Assert.Equal("unknown", recorded.State);
        Assert.Contains(recorded.Effects!, effect => effect.Surface == "postgres" && effect.State == "confirmed");
        Assert.Throws<InvalidOperationException>(() => f.Store.ClaimNext(f.Owner));
        var published = await new SnapshotStore(database.DataSource).GetPublishedAsync(f.Run.CapabilityId);
        Assert.Equal(1, published!.Generation);
    }

    [Fact]
    public async Task Database_commit_before_host_confirmation_keeps_the_two_receipts_separate()
    {
        using var environment = new DatabaseEnvironment(database.ConnectionString);
        using var f = new ExecutionFixture();
        var prepared = f.Prepare();
        analysiscli::CapabilityExplorer.Cli.AnalysisPublication.BeforeHostConfirmationForTests =
            _ => Task.FromException(new IOException("deterministic pre-confirmation barrier"));
        try
        {
            using var permit = f.Begin();
            await Assert.ThrowsAsync<IOException>(() =>
                AnalysisPublication.Publish(f.Root, f.Run, prepared, permit));
        }
        finally
        {
            analysiscli::CapabilityExplorer.Cli.AnalysisPublication.BeforeHostConfirmationForTests = null;
        }

        var journal = f.Store.ReadRun(f.Run.RunId);
        var report = await new SnapshotStore(database.DataSource).ReconcilePublicationAttemptAsync(
            f.Run.RunId, f.Run.AttemptId!);
        Assert.Equal("unknown", journal.State);
        Assert.Contains(journal.Effects!, effect => effect.Surface == "postgres" && effect.State == "pending");
        Assert.Equal("confirmed-published", report.Outcome);
        Assert.Equal("published", report.AttemptResult);
    }

    [Fact]
    public async Task Process_death_after_actual_database_commit_blocks_republication()
    {
        var childRoot = Environment.GetEnvironmentVariable("CE_PUBLICATION_CRASH_ROOT");
        if (childRoot is not null)
        {
            using var environment = new DatabaseEnvironment(Environment.GetEnvironmentVariable("CE_PUBLICATION_CRASH_DATABASE"));
            var store = new FileRunStore(Path.Combine(childRoot,".sandbox-cache/requests"));
            var run = store.ReadRun(Environment.GetEnvironmentVariable("CE_PUBLICATION_CRASH_RUN")!);
            using var owner = store.AcquireWorker();
            using var permit = store.BeginPublication(owner,new(run.RunId,run.AttemptId!),run.ManifestHash!);
            var attempt = store.AttemptDirectory(new(run.RunId,run.AttemptId!));
            var text = File.ReadAllText(Path.Combine(store.RunDirectory(run.RunId),"resolved.json"));
            var resolved = JsonSerializer.Deserialize<ResolvedManifest>(text,JsonSerializerOptions.Web)!;
            var revision = resolved.Inputs["revision:web-ts"];
            var staging = Path.Combine(attempt,"out","web-ts",revision);
            var artifact = Path.Combine(staging,"artifact.jsonl");
            var prepared = new PreparedAnalysis([new("web-ts",revision,Path.Combine(attempt,"src","web-ts"),staging)],
                new(resolved.Inputs),new() { [artifact] = Hash(artifact) });
            await AnalysisPublication.Publish(childRoot,run,prepared,permit);
            using var pipe = new NamedPipeClientStream(".",Environment.GetEnvironmentVariable("CE_PUBLICATION_CRASH_PIPE")!,PipeDirection.Out);
            await pipe.ConnectAsync(15000);
            pipe.WriteByte(1); pipe.Flush();
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return;
        }
        using var f = new ExecutionFixture();
        f.Prepare();
        f.Owner.Dispose();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var pipeName = "ce" + Guid.NewGuid().ToString("N")[..12];
        using var server = new NamedPipeServerStream(pipeName,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
        var start = new ProcessStartInfo("dotnet") { UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add(typeof(AnalysisExecutionTests).FullName + ".Process_death_after_actual_database_commit_blocks_republication");
        start.Environment["CE_PUBLICATION_CRASH_ROOT"] = f.Root;
        start.Environment["CE_PUBLICATION_CRASH_RUN"] = f.Run.RunId;
        start.Environment["CE_PUBLICATION_CRASH_DATABASE"] = database.ConnectionString;
        start.Environment["CE_PUBLICATION_CRASH_PIPE"] = pipeName;
        using var child = Process.Start(start)!;
        try
        {
            await server.WaitForConnectionAsync(timeout.Token);
            var signal = new byte[1];
            Assert.Equal(1,await server.ReadAsync(signal,timeout.Token));
            Assert.NotNull(await new SnapshotStore(database.DataSource).GetPublishedAsync(f.Run.CapabilityId));
        }
        finally
        {
            if (!child.HasExited) child.Kill();
            await child.WaitForExitAsync(timeout.Token);
        }
        using var replacement = f.Store.AcquireWorker();
        Assert.True(f.Store.Recover(replacement).Blocked);
        Assert.Equal("unknown",f.Store.ReadRun(f.Run.RunId).State);
        Assert.Throws<InvalidOperationException>(() => f.Store.ClaimNext(replacement));
        Assert.Equal(1,(await new SnapshotStore(database.DataSource).GetPublishedAsync(f.Run.CapabilityId))!.Generation);
    }

    [Fact]
    public async Task Nonzero_sandbox_process_exit_does_not_prove_container_termination()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Unix host qualification required");
        using var f = new ExecutionFixture();
        var script = Path.Combine(f.Root, "scripts", "sandbox");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        File.WriteAllText(script, "#!/bin/sh\nexit 9\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var process = new AnalysisProcess(f.Root, f.Store, f.Run, CancellationToken.None, new FakeRuntime());
        await Assert.ThrowsAsync<InvalidOperationException>(() => process.Run("scripts/sandbox", []));
        Assert.False(process.TerminationConfirmed);
        Assert.Equal("unknown", f.Store.FinishExecution(f.Owner, f.Identity, 9, process.TerminationConfirmed).State);
    }

    [Fact]
    public async Task Cancellation_before_spawn_prevents_process_effects()
    {
        using var f = new ExecutionFixture();
        var runtime = new FakeRuntime();
        f.Store.Cancel(f.Run.RunId);
        using var process = new AnalysisProcess(f.Root,f.Store,f.Run,CancellationToken.None,runtime);
        await Assert.ThrowsAsync<OperationCanceledException>(() => process.Run("scripts/sandbox",[]));
        Assert.False(runtime.Frozen);
        Assert.True(process.TerminationConfirmed);
        Assert.Equal("cancelled",f.Store.FinishExecution(f.Owner,f.Identity,1,process.TerminationConfirmed).State);
    }

    [Fact]
    public async Task Cancellation_after_actual_spawn_requests_exact_cleanup_and_preserves_uncertainty()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Unix host qualification required");
        using var f = new ExecutionFixture();
        var script = Path.Combine(f.Root,"scripts/sandbox");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        File.WriteAllText(script,"#!/bin/sh\nprintf ready > started\nexec sleep 60\n");
        File.SetUnixFileMode(script,UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var runtime = new FakeRuntime();
        using var process = new AnalysisProcess(f.Root,f.Store,f.Run,CancellationToken.None,runtime);
        var execution = process.Run("scripts/sandbox",[]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(Path.Combine(f.Root,"started"))) await Task.Delay(10,timeout.Token);
        Assert.Equal("accepted",f.Store.Cancel(f.Run.RunId).Outcome);
        await Assert.ThrowsAsync<OperationCanceledException>(() => execution);
        Assert.Equal("ce-analyze=" + AnalysisWorkerCommand.HashText(f.Root)[..16] + "-" + f.Run.RunId + "-" + f.Run.AttemptId,runtime.CleanupLabel);
        Assert.False(process.TerminationConfirmed);
        Assert.Equal("unknown",f.Store.FinishExecution(f.Owner,f.Identity,1,process.TerminationConfirmed).State);
    }

    [Fact]
    public async Task Cancellation_during_host_git_interrupts_acquisition_and_preserves_unknown_execution()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Unix host qualification required");
        using var f = new ExecutionFixture();
        var bin = Path.Combine(f.Root, "bin");
        Directory.CreateDirectory(bin);
        var marker = Path.Combine(f.Root, "git-started");
        var git = Path.Combine(bin, "git");
        File.WriteAllText(git, "#!/bin/sh\n: > '" + marker + "'\nexec sleep 30\n");
        File.SetUnixFileMode(git, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + path);
            using var process = new AnalysisProcess(f.Root, f.Store, f.Run, CancellationToken.None, new FakeRuntime());
            var sync = new SourceSync(Path.Combine(f.Root, "sources"));
            var source = new RepositorySource("private", "https://github.com/fixture/private", SourceKind.GitHub);
            var execution = process.AcquireSource(sync, token => sync.LockAsync(source, token));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(marker)) await Task.Delay(20, timeout.Token);
            f.Store.Cancel(f.Run.RunId);
            await Assert.ThrowsAsync<OperationCanceledException>(() => execution.WaitAsync(timeout.Token));
            Assert.False(process.TerminationConfirmed);
            Assert.Equal("unknown", f.Store.FinishExecution(f.Owner, f.Identity, 1, process.TerminationConfirmed).State);
        }
        finally { Environment.SetEnvironmentVariable("PATH", path); }
    }

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public async Task Invalid_new_aggregate_configuration_removes_prior_success_receipt()
    {
        using var f = new ExecutionFixture();
        var receipt = Path.Combine(f.Root,".sandbox-cache/analysis-receipt.json");
        File.WriteAllText(receipt,"old success");
        // The owning coordinator clears prior success before attempting to parse/hash new inputs.
        await Assert.ThrowsAsync<FileNotFoundException>(() => AnalysisReceipt.Start(f.Root,Path.Combine(f.Root,"missing.json"),null));
        Assert.False(File.Exists(receipt));
    }

    [Fact]
    [Trait("Profile", "HostDocker")]
    public async Task Actual_Docker_cleanup_stops_only_the_exact_attempt_label()
    {
        var runtime = new AnalysisDockerRuntime();
        await runtime.EnsureFrozen();
        const string image = "pgvector/pgvector:0.8.6-pg17@sha256:cf134a767f474095eeba57e0117be8e568e011a63f33fbf252f14c9b760f8e6f";
        var label = "ce-analyze=qualification-" + Guid.NewGuid().ToString("N");
        string? owned = null, sibling = null;
        try
        {
            owned = (await Docker("run","--pull=never","-d","--network=none","--label",label,"--entrypoint","sleep",image,"60")).Trim();
            sibling = (await Docker("run","--pull=never","-d","--network=none","--label",label + "-other","--entrypoint","sleep",image,"60")).Trim();
            Assert.True(await runtime.CleanupOwned(label));
            Assert.Equal("false",(await Docker("inspect","--format","{{.State.Running}}",owned)).Trim());
            Assert.Equal("true",(await Docker("inspect","--format","{{.State.Running}}",sibling)).Trim());
        }
        finally
        {
            if (owned is not null) await Docker("rm","-f",owned);
            if (sibling is not null) await Docker("rm","-f",sibling);
        }
        async Task<string> Docker(params string[] args)
        {
            var start = new System.Diagnostics.ProcessStartInfo("docker") { UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true };
            runtime.ApplyEnvironment(start);
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { if (!process.HasExited) process.Kill(true); throw; }
            Assert.True(process.ExitCode == 0,"Docker qualification operation failed: " + await error);
            return await output;
        }
    }

    private sealed class FakeRuntime : IAnalysisContainerRuntime
    {
        public bool Frozen { get; private set; }
        public string? CleanupLabel { get; private set; }
        public Task EnsureFrozen() { Frozen=true; return Task.CompletedTask; }
        public void ApplyEnvironment(System.Diagnostics.ProcessStartInfo start) { }
        public Task<bool> CleanupOwned(string label) { CleanupLabel=label; return Task.FromResult(true); }
    }

    internal sealed class DatabaseEnvironment : IDisposable
    {
        private readonly string? _old = Environment.GetEnvironmentVariable("CE_DATABASE");
        public DatabaseEnvironment(string? value) => Environment.SetEnvironmentVariable("CE_DATABASE", value);
        public void Dispose() => Environment.SetEnvironmentVariable("CE_DATABASE", _old);
    }

    internal sealed class ExecutionFixture : IDisposable
    {
        public string Root { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ce-analysis-effects-" + Guid.NewGuid().ToString("N"))
            .Replace("/var/", "/private/var/", StringComparison.Ordinal));
        public FileRunStore Store { get; }
        public WorkerOwnership Owner { get; }
        public AnalysisRun Run { get; private set; }
        private readonly string? _exportGroup;
        private readonly bool _preserve;
        public AttemptIdentity Identity => new(Run.RunId, Run.AttemptId!);
        public ExecutionFixture(bool withBindings = false,string? exportGroup = null,string? root = null,bool preserve = false)
        {
            _exportGroup=exportGroup;
            _preserve=preserve;
            if (root is not null) Root=Path.GetFullPath(root);
            Directory.CreateDirectory(Root);
            var configuration = Path.Combine(Root, "capabilities.json");
            var capability = "test-lifecycle-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(configuration, JsonSerializer.Serialize(new { capabilities = new[] { new { id = capability, repositories = Array.Empty<object>() } } }));
            Store = new(Path.Combine(Root, ".sandbox-cache", "requests"));
            var bindings = withBindings ? Path.Combine(Root,"bindings-input.json") : null;
            if (bindings is not null) File.WriteAllText(bindings,"{}");
            Store.Admit(capability, configuration, bindings, new());
            Owner = Store.AcquireWorker();
            Run = Store.ClaimNext(Owner)!;
        }
        public AnalysisRepository AddRepository(string fixture, bool includeArtifact = true)
        {
            var text = Fixtures.Golden(fixture);
            using var header = JsonDocument.Parse(text.Split('\n')[0]);
            var id = header.RootElement.GetProperty("repositoryId").GetString()!;
            var revision = header.RootElement.GetProperty("commitSha").GetString()!;
            var staging = Path.Combine(Store.AttemptDirectory(Identity), "out", id, revision);
            Directory.CreateDirectory(staging);
            if (includeArtifact) File.WriteAllText(Path.Combine(staging, "artifact.jsonl"), text);
            var sourceRoot = Path.Combine(Store.AttemptDirectory(Identity),"src");
            if (_exportGroup is not null) sourceRoot=Path.Combine(sourceRoot,_exportGroup);
            var source = Path.Combine(sourceRoot,id);
            Directory.CreateDirectory(source);
            return new(id, revision, source, staging);
        }
        public PreparedAnalysis Prepare(string? additionalFixture = null)
        {
            var repo = AddRepository("web-ts/v1-base/_fixture_web.jsonl");
            var path = Path.Combine(repo.Staging, "artifact.jsonl");
            var repositories = new List<AnalysisRepository> { repo };
            if (additionalFixture is not null) repositories.Add(AddRepository(additionalFixture));
            var inputs = new Dictionary<string, string>();
            var artifacts = new Dictionary<string,string>();
            foreach (var item in repositories)
            {
                var artifact = Path.Combine(item.Staging,"artifact.jsonl");
                inputs["revision:" + item.Id] = item.Sha;
                inputs["export-path:" + item.Id] = Path.GetRelativePath(Store.AttemptDirectory(Identity),item.Source).Replace('\\','/');
                inputs["artifact:" + item.Id + ":artifact.jsonl"] = Hash(artifact);
                artifacts[artifact] = Hash(artifact);
            }
            Store.BindResolved(Owner, Identity, new(Run.AcceptedInputsHash, inputs));
            Store.FinishExecution(Owner, Identity, 0);
            Run = Store.ReadRun(Run.RunId);
            return new(repositories, inputs, artifacts);
        }
        public PublicationPermit Begin() => Store.BeginPublication(Owner, Identity, Run.ManifestHash!);
        public string Destination(AnalysisRepository repo) => Path.Combine(Root, ".sandbox-cache", "artifacts", repo.Id, repo.Sha, "artifact.jsonl");
        public void Dispose() { Owner.Dispose(); if (!_preserve) Directory.Delete(Root, true); }
    }
}

[CollectionDefinition("Local publication environment", DisableParallelization = true)]
public sealed class LocalPublicationCollection;

[Collection("Local publication environment")]
public sealed class LocalPublicationTests
{
    [Fact]
    public async Task Given_existing_artifacts_when_publishing_then_staging_is_preserved_and_atomic_moves_share_artifact_storage()
    {
        using var environment = new AnalysisExecutionTests.DatabaseEnvironment(null);
        var physicalRoot = Environment.GetEnvironmentVariable("CE_PUBLICATION_FIXTURE_ROOT");
        using var fixture = new AnalysisExecutionTests.ExecutionFixture(root: physicalRoot, preserve: physicalRoot is not null);
        var prepared = fixture.Prepare();
        var repository = Assert.Single(prepared.Repositories);
        var destination = Path.GetDirectoryName(fixture.Destination(repository))!;
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "previous.txt"), "previous canonical evidence");
        using var permit = fixture.Begin();
        await AnalysisPublication.Publish(fixture.Root, fixture.Run, prepared, permit);
        var completed = permit.Complete();
        Assert.Equal("done", completed.State);
        Assert.True(Directory.Exists(repository.Staging), "Publication must retain the original journal staging directory.");
        Assert.Equal(Fixtures.Golden("web-ts/v1-base/_fixture_web.jsonl"), File.ReadAllText(fixture.Destination(repository)));
        var previous = Path.Combine(fixture.Root, ".sandbox-cache", "artifact-publication", fixture.Run.RunId,
            fixture.Run.AttemptId!, repository.Id, repository.Sha, "previous", "previous.txt");
        Assert.Equal("previous canonical evidence", File.ReadAllText(previous));
        Assert.All(completed.Effects!, effect => Assert.Equal("confirmed", effect.State));
    }
}
