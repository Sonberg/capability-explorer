extern alias analysiscli;
using System.Diagnostics;
using CapabilityExplorer.Capabilities;
using analysiscli::CapabilityExplorer.Cli;
using Xunit;

namespace CapabilityExplorer.Tests;

/// <summary>
/// Host-native door-fact tests for the shadow (non-filtering) C# adapter-planning step: they exercise
/// real `git` and real file-system enumeration against synthetic in-test repositories, never a fixture
/// repository, and are excluded from the sandbox/container test run by the "HostDocker" trait.
/// </summary>
public sealed class AnalysisAdapterShadowTests
{
    private static void RequireHostDoor()
    {
        Assert.False(File.Exists("/.dockerenv") ||
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true",
            "host-native door required");
    }

    [Fact]
    [Trait("Profile", "HostDocker")]
    public async Task Given_a_C_sharp_only_repository_when_shadow_planning_runs_then_identity_status_and_census_are_exact()
    {
        RequireHostDoor();
        using var repo = new Repo();
        repo.Write("Api/Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        repo.Write("Api/Order.cs", "class Order {}");
        repo.Write("Api/Customer.cs", "class Customer {}");
        var sha = await repo.Commit("csharp-only");
        var expectedTree = (await Repo.Git(repo.Source, "rev-parse", sha + "^{tree}")).Trim();

        var (export, outcome, shadow) = await repo.ExportAndPlan(sha);

        Assert.Equal(expectedTree, export.TreeSha);
        Assert.Equal("ok", outcome.Status);
        Assert.Empty(outcome.Codes);
        Assert.Equal("complete=1 unknown=0 skipped=0 units=1/1/0/0/0 inputs=3/3/0/0/0", outcome.Census);
        Assert.Matches("^[0-9a-f]{64}$", outcome.PlanSha256);
        Assert.Equal(Repo.RecipeSha256, shadow.RecipeSha256);
        Assert.Equal(Repo.ManifestSha256, shadow.ManifestSha256);
        Assert.Matches("^[A-Za-z0-9 .+-]{1,128}$", shadow.HostRuntime);
    }

    [Fact]
    [Trait("Profile", "HostDocker")]
    public async Task Given_the_same_commit_run_twice_then_tree_plan_and_census_are_identical()
    {
        RequireHostDoor();
        using var repoA = new Repo();
        repoA.Write("Api/Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        repoA.Write("Api/Order.cs", "class Order {}");
        var shaA = await repoA.Commit("determinism");
        var (exportA, outcomeA, _) = await repoA.ExportAndPlan(shaA);

        using var repoB = new Repo();
        repoB.Write("Api/Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        repoB.Write("Api/Order.cs", "class Order {}");
        var shaB = await repoB.Commit("determinism");
        var (exportB, outcomeB, _) = await repoB.ExportAndPlan(shaB);

        Assert.Equal(exportA.TreeSha, exportB.TreeSha);
        Assert.Equal(outcomeA.PlanSha256, outcomeB.PlanSha256);
        Assert.Equal(outcomeA.Census, outcomeB.Census);
    }

    [Fact]
    [Trait("Profile", "HostDocker")]
    public async Task Given_one_byte_changed_in_a_source_file_then_tree_and_plan_both_differ()
    {
        RequireHostDoor();
        using var repo = new Repo();
        repo.Write("Api/Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        repo.Write("Api/Order.cs", "class Order {}");
        var before = await repo.Commit("base");
        var (exportBefore, outcomeBefore, _) = await repo.ExportAndPlan(before);

        repo.Write("Api/Order.cs", "class Order {} // changed");
        var after = await repo.Commit("changed");
        var (exportAfter, outcomeAfter, _) = await repo.ExportAndPlan(after);

        Assert.NotEqual(exportBefore.TreeSha, exportAfter.TreeSha);
        Assert.NotEqual(outcomeBefore.PlanSha256, outcomeAfter.PlanSha256);
    }

    [Fact]
    [Trait("Profile", "HostDocker")]
    public async Task Given_an_edited_manifest_exclusion_then_the_plan_differs_but_the_tree_does_not()
    {
        RequireHostDoor();
        using var repo = new Repo();
        repo.Write("Api/Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        repo.Write("Api/Order.cs", "class Order {}");
        var sha = await repo.Commit("exclusion");
        var (exportBefore, outcomeBefore, _) = await repo.ExportAndPlan(sha);

        repo.EditManifestExclusions(["Api"]);
        var (exportAfter, outcomeAfter, _) = await repo.ExportAndPlan(sha);

        Assert.Equal(exportBefore.TreeSha, exportAfter.TreeSha);
        Assert.NotEqual(outcomeBefore.PlanSha256, outcomeAfter.PlanSha256);
    }

    [Fact]
    [Trait("Profile", "HostDocker")]
    public async Task Given_a_symlink_and_a_gitlink_then_the_status_is_incomplete_with_two_skips()
    {
        RequireHostDoor();
        using var repo = new Repo();
        repo.Write("Api/Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        repo.Write("Api/Order.cs", "class Order {}");
        await Repo.Git(repo.Source, "init", "-q");
        await Repo.Git(repo.Source, "add", ".");
        await Repo.Git(repo.Source, "-c", "core.symlinks=true", "update-index", "--add", "--cacheinfo",
            "120000," + await BlobSha(repo, "Api/Order.cs") + ",link.txt");
        await Repo.Git(repo.Source, "update-index", "--add", "--cacheinfo",
            "160000,1111111111111111111111111111111111111111,sub/mod");
        var sha = await repo.CommitStaged("symlink-and-gitlink");

        var (export, outcome, shadow) = await repo.ExportAndPlan(sha);

        Assert.Equal(2, export.Skipped);
        Assert.Equal("incomplete", outcome.Status);
        Assert.Equal(["export-skipped"], outcome.Codes);
        Assert.Contains("skipped=2", outcome.Census);
        var inputs = new Dictionary<string, string>();
        AnalysisAdapterShadow.AddInputs(inputs, "repo", export.TreeSha, outcome);
        Assert.Equal("incomplete:export-skipped", inputs["adapter-shadow-status:repo"]);
    }

    [Fact]
    [Trait("Profile", "HostDocker")]
    public async Task Given_101_one_file_projects_then_the_status_is_limit_exceeded_with_the_unit_limit_code()
    {
        RequireHostDoor();
        using var repo = new Repo();
        for (var index = 0; index < 101; index++)
        {
            repo.Write($"Projects/P{index:000}/P{index:000}.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        }
        var sha = await repo.Commit("one-hundred-one-projects");

        var (_, outcome, _) = await repo.ExportAndPlan(sha);

        Assert.Equal("limit-exceeded", outcome.Status);
        Assert.Equal(["unit-limit-exceeded"], outcome.Codes);
        var inputs = new Dictionary<string, string>();
        AnalysisAdapterShadow.AddInputs(inputs, "repo", "0000000000000000000000000000000000000a", outcome);
        Assert.Equal("limit-exceeded:unit-limit-exceeded", inputs["adapter-shadow-status:repo"]);
    }

    [Fact]
    [Trait("Profile", "HostDocker")]
    public async Task Given_a_root_without_adapter_config_then_loading_the_shadow_context_fails()
    {
        RequireHostDoor();
        using var root = TempDirectory.Create();
        await Assert.ThrowsAnyAsync<IOException>(() => AnalysisAdapterShadow.LoadAsync(root.Path, CancellationToken.None));
    }

    [Fact]
    [Trait("Profile", "HostDocker")]
    public async Task Given_a_recipe_digest_mismatch_then_loading_the_shadow_context_fails()
    {
        RequireHostDoor();
        using var root = TempDirectory.Create();
        Repo.CopyTrackedConfig(root.Path);
        var recipePath = Path.Combine(root.Path, "config/adapters/csharp.roslyn/recipe.json");
        await File.WriteAllTextAsync(recipePath, await File.ReadAllTextAsync(recipePath) + "\n// tampered");

        await Assert.ThrowsAsync<InvalidDataException>(() => AnalysisAdapterShadow.LoadAsync(root.Path, CancellationToken.None));
    }

    // G2 is a candidate-only unit test of PlanAsync's one permitted deadline catch, not a pre-existing
    // door fact: it needs no container/host distinction, so it carries no "HostDocker" trait.
    [Fact]
    public async Task Given_a_near_zero_budget_then_the_plan_reports_a_time_limit_without_a_plan_digest()
    {
        using var repo = new Repo();
        repo.Write("Api/Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        repo.Write("Api/Order.cs", "class Order {}");
        var sha = await repo.Commit("deadline");
        var export = await repo.Export(sha);
        var shadow = await AnalysisAdapterShadow.LoadAsync(repo.Root, CancellationToken.None);

        var outcome = await AnalysisAdapterShadow.PlanAsync(export.Destination, "repo", sha, export.TreeSha,
            export.Result.Skipped, shadow, TimeSpan.Zero, CancellationToken.None);

        Assert.Equal("limit-exceeded", outcome.Status);
        Assert.Equal(["time-limit"], outcome.Codes);
        Assert.Null(outcome.PlanSha256);
    }

    [Fact]
    public async Task Given_an_already_cancelled_stop_token_then_the_real_cancellation_propagates()
    {
        using var repo = new Repo();
        repo.Write("Api/Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var sha = await repo.Commit("cancelled");
        var export = await repo.Export(sha);
        var shadow = await AnalysisAdapterShadow.LoadAsync(repo.Root, CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => AnalysisAdapterShadow.PlanAsync(
            export.Destination, "repo", sha, export.TreeSha, export.Result.Skipped, shadow,
            TimeSpan.FromSeconds(120), cancelled.Token));
    }

    private static async Task<string> BlobSha(Repo repo, string relativePath) =>
        (await Repo.Git(repo.Source, "hash-object", Path.Combine(repo.Source, relativePath))).Trim();

    private sealed record Export(ExportResult Result, string Destination)
    {
        public string TreeSha => Result.TreeSha;
        public int Skipped => Result.Skipped;
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }
        private TempDirectory(string path) => Path = path;
        public static TempDirectory Create() => new(Directory.CreateTempSubdirectory("adapter-shadow-").FullName);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    /// <summary>
    /// A disposable, fixed-author/committer synthetic git repository plus the tracked adapter config,
    /// copied verbatim from the project root so that digests stay cross-verified with production bytes.
    /// </summary>
    private sealed class Repo : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("adapter-shadow-").FullName;
        public string Source => System.IO.Path.Combine(Root, "source");

        public static readonly string RecipeSha256 = Sha256File(System.IO.Path.Combine(
            Fixtures.RepositoryRoot, "config/adapters/csharp.roslyn/recipe.json"));
        public static readonly string ManifestSha256 = Sha256File(System.IO.Path.Combine(
            Fixtures.RepositoryRoot, "config/adapters/csharp.roslyn/manifest.json"));

        public Repo()
        {
            Directory.CreateDirectory(Source);
            CopyTrackedConfig(Root);
        }

        public static void CopyTrackedConfig(string root)
        {
            var directory = System.IO.Path.Combine(root, "config/adapters/csharp.roslyn");
            Directory.CreateDirectory(directory);
            foreach (var file in new[] { "manifest.json", "recipe.json" })
            {
                File.WriteAllText(System.IO.Path.Combine(directory, file),
                    File.ReadAllText(System.IO.Path.Combine(Fixtures.RepositoryRoot, "config/adapters/csharp.roslyn", file)));
            }
        }

        public void EditManifestExclusions(string[] prefixes)
        {
            var path = System.IO.Path.Combine(Root, "config/adapters/csharp.roslyn/manifest.json");
            var original = File.ReadAllText(path);
            var edited = original.Replace(
                "\"excludedPathPrefixes\": []",
                "\"excludedPathPrefixes\": [" + string.Join(',', prefixes.Select(prefix => '"' + prefix + '"')) + "]");
            Assert.NotEqual(original, edited);
            File.WriteAllText(path, edited);
        }

        public void Write(string relativePath, string content)
        {
            var path = System.IO.Path.Combine(Source, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public async Task<string> Commit(string message)
        {
            await Git(Source, "init", "-q");
            await Git(Source, "add", ".");
            return await CommitStaged(message);
        }

        public async Task<string> CommitStaged(string message)
        {
            var start = GitStart(Source, "-c", "user.name=ce-test", "-c", "user.email=test@example.invalid",
                "commit", "-q", "-m", message);
            foreach (var (key, value) in new (string, string)[]
            {
                ("GIT_AUTHOR_NAME", "ce-test"), ("GIT_AUTHOR_EMAIL", "test@example.invalid"),
                ("GIT_AUTHOR_DATE", "2024-01-01T00:00:00Z"),
                ("GIT_COMMITTER_NAME", "ce-test"), ("GIT_COMMITTER_EMAIL", "test@example.invalid"),
                ("GIT_COMMITTER_DATE", "2024-01-01T00:00:00Z"),
            }) start.Environment[key] = value;
            await Run(start);
            return (await Git(Source, "rev-parse", "HEAD")).Trim();
        }

        public async Task<Export> Export(string sha)
        {
            var sync = new SourceSync(System.IO.Path.Combine(Root, "workspace"));
            var destination = System.IO.Path.Combine(Root, "export-" + sha + "-" + Guid.NewGuid().ToString("N"));
            var result = await sync.ExportAsync(new RepositorySource("repo", Source, SourceKind.Local), sha, destination);
            return new Export(result, destination);
        }

        public async Task<(Export Export, AdapterShadowOutcome Outcome, AdapterShadowContext Shadow)> ExportAndPlan(string sha)
        {
            var export = await Export(sha);
            var shadow = await AnalysisAdapterShadow.LoadAsync(Root, CancellationToken.None);
            var outcome = await AnalysisAdapterShadow.PlanAsync(export.Destination, "repo", sha, export.TreeSha,
                export.Skipped, shadow, TimeSpan.FromSeconds(120), CancellationToken.None);
            return (export, outcome, shadow);
        }

        public static Task<string> Git(string workingDirectory, params string[] args) => Run(GitStart(workingDirectory, args));

        private static ProcessStartInfo GitStart(string workingDirectory, params string[] args)
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in args) start.ArgumentList.Add(argument);
            return start;
        }

        private static async Task<string> Run(ProcessStartInfo start)
        {
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, await error);
            return await output;
        }

        private static string Sha256File(string path) =>
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
