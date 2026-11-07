using System.Diagnostics;
using CapabilityExplorer.Capabilities;
using Xunit;

namespace CapabilityExplorer.Tests;

[CollectionDefinition(GitProcessEnvironmentCollection.Name, DisableParallelization = true)]
public sealed class GitProcessEnvironmentCollection
{
    public const string Name = "Git process environment";
}

[Collection(GitProcessEnvironmentCollection.Name)]
public sealed class SourceSecurityTests
{
    [Theory]
    [InlineData("https://attacker.invalid/o/r", SourceKind.GitHub, "HEAD")]
    [InlineData("https://github.com/o/../r", SourceKind.GitHub, "HEAD")]
    [InlineData("https://github.com/o/r?x=1", SourceKind.GitHub, "HEAD")]
    [InlineData("/tmp/repo", SourceKind.Local, "--output=/tmp/bad")]
    [InlineData("/tmp/repo", (SourceKind)42, "HEAD")]
    public void Direct_records_cannot_bypass_source_validation(string location, SourceKind kind, string reference)
    {
        var sync = new SourceSync("/tmp/workspace");
        Assert.Throws<ArgumentException>(() => sync.GitDirectory(new RepositorySource("repo", location, kind, reference)));
    }

    [Fact]
    public async Task Existing_destination_symlink_cannot_overwrite_external_file()
    {
        using var fixture = await Repository.Create();
        var outside = Path.Combine(fixture.Root, "outside"); Directory.CreateDirectory(outside);
        var victim = Path.Combine(outside, "file.txt"); await File.WriteAllTextAsync(victim, "untouched");
        var output = Path.Combine(fixture.Root, "output"); Directory.CreateSymbolicLink(output, outside);
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Sync.ExportAsync(fixture.Source, fixture.Sha, output));
        Assert.Equal("untouched", await File.ReadAllTextAsync(victim));
    }

    [Fact]
    public async Task Given_a_commit_with_a_gitlink_when_exporting_then_it_is_counted_as_skipped()
    {
        using var fixture = await Repository.Create();
        await fixture.Git("update-index", "--add", "--cacheinfo", "160000,1111111111111111111111111111111111111111,sub/mod");
        await fixture.Git("-c", "user.name=test", "-c", "user.email=test@example.invalid", "commit", "-q", "-m", "gitlink");
        var sha = (await fixture.Git("rev-parse", "HEAD")).Trim();
        var output = Path.Combine(fixture.Root, "gitlink-output");
        var result = await fixture.Sync.ExportAsync(fixture.Source, sha, output);
        Assert.Equal(1, result.Files); // file.txt from the base fixture commit is unaffected
        Assert.Equal(1, result.Skipped); // the gitlink archives as an empty directory with no content: must not count as ok
        Assert.False(Directory.Exists(Path.Combine(output, "sub", "mod")) && Directory.EnumerateFileSystemEntries(Path.Combine(output, "sub", "mod")).Any());
    }

    [Fact]
    public async Task Given_a_commit_when_exporting_then_the_result_carries_the_commit_tree_sha()
    {
        using var fixture = await Repository.Create();
        var expectedTree = (await fixture.Git("rev-parse", $"{fixture.Sha}^{{tree}}")).Trim();
        var output = Path.Combine(fixture.Root, "tree-output");
        var result = await fixture.Sync.ExportAsync(fixture.Source, fixture.Sha, output);
        Assert.Matches("^[0-9a-f]{40}$", result.TreeSha);
        Assert.Equal(expectedTree, result.TreeSha);
    }

    [Fact]
    public async Task Local_paths_with_spaces_lock_and_export_into_empty_destinations()
    {
        using var fixture = await Repository.Create();
        var locked = await fixture.Sync.LockAsync(fixture.Source);
        Assert.Null(locked.Error); Assert.Equal(fixture.Sha, locked.CommitSha);
        var output = Path.Combine(fixture.Root, "empty output"); Directory.CreateDirectory(output);
        Assert.Equal(1, (await fixture.Sync.ExportAsync(fixture.Source, fixture.Sha, output)).Files);
        Assert.Equal("payload", await File.ReadAllTextAsync(Path.Combine(output, "file.txt")));
    }

    [Fact]
    public async Task Given_explicit_bare_repository_policy_when_locking_a_bare_source_then_head_is_resolved()
    {
        using var fixture = await Repository.Create();
        var bare = Path.Combine(fixture.Root, "bare.git");
        await fixture.Git("clone", "--bare", fixture.Source.Location, bare);
        var previous = new[]
        {
            Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT"),
            Environment.GetEnvironmentVariable("GIT_CONFIG_KEY_0"),
            Environment.GetEnvironmentVariable("GIT_CONFIG_VALUE_0"),
        };
        Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "safe.bareRepository");
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "explicit");
        try
        {
            var sync = new SourceSync(Path.Combine(fixture.Root, "workspace"));
            var source = new RepositorySource("bare", bare, SourceKind.Local);
            var locked = await sync.LockAsync(source);
            Assert.Null(locked.Error);
            Assert.Equal(fixture.Sha, locked.CommitSha);
            var output = Path.Combine(fixture.Root, "bare export");
            Assert.Equal(1, (await sync.ExportAsync(source, fixture.Sha, output)).Files);
            Assert.Equal("payload", await File.ReadAllTextAsync(Path.Combine(output, "file.txt")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", previous[0]);
            Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", previous[1]);
            Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", previous[2]);
        }
    }

    [Fact]
    public async Task Nonempty_destination_and_symlink_ancestors_fail_without_writes()
    {
        using var fixture = await Repository.Create();
        var output = Path.Combine(fixture.Root, "output"); Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "existing.txt"), "keep");
        await Assert.ThrowsAsync<IOException>(() => fixture.Sync.ExportAsync(fixture.Source, fixture.Sha, output));
        var link = Path.Combine(fixture.Root, "parent-link"); Directory.CreateSymbolicLink(link, output);
        await Assert.ThrowsAsync<IOException>(() => fixture.Sync.ExportAsync(fixture.Source, fixture.Sha, Path.Combine(link, "nested")));
        Assert.False(Directory.Exists(Path.Combine(output, "nested")));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(output, "existing.txt")));
    }

    [Theory]
    [InlineData(100, 6)]
    [InlineData(1, 1000)]
    public async Task Total_archive_budgets_fail_closed(int entries, long bytes)
    {
        using var fixture = await Repository.Create();
        var sync = new SourceSync(Path.Combine(fixture.Root, "workspace"), exportLimits: new ExportLimits(entries, bytes));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => sync.ExportAsync(fixture.Source, fixture.Sha, Path.Combine(fixture.Root, "output")));
        Assert.Contains("total export", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(100001, 1)]
    [InlineData(1, 268435457)]
    public async Task Callers_cannot_disable_export_limits(int entries, long bytes)
    {
        using var fixture = await Repository.Create();
        var sync = new SourceSync(fixture.Root, exportLimits: new ExportLimits(entries, bytes));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => sync.ExportAsync(fixture.Source, fixture.Sha, Path.Combine(fixture.Root, "output")));
    }

    [Fact]
    public async Task Oversized_git_subject_is_bounded_and_reported_as_error()
    {
        using var fixture = await Repository.Create();
        var message = Path.Combine(fixture.Root, "message.txt"); await File.WriteAllTextAsync(message, new string('x', 8 * 1024 * 1024 + 1));
        await fixture.Git("-c", "user.name=test", "-c", "user.email=test@example.invalid", "commit", "-q", "--allow-empty", "-F", message);
        var locked = await fixture.Sync.LockAsync(fixture.Source);
        Assert.Null(locked.CommitSha); Assert.Contains("bounded response", locked.Error, StringComparison.Ordinal);
    }

    private sealed class Repository : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("source-security-").FullName;
        public SourceSync Sync => new(Path.Combine(Root, "workspace"));
        public RepositorySource Source => new("repo", Path.Combine(Root, "repo with spaces"), SourceKind.Local);
        public string Sha { get; private set; } = "";
        public static async Task<Repository> Create()
        {
            var fixture = new Repository(); Directory.CreateDirectory(fixture.Source.Location);
            await File.WriteAllTextAsync(Path.Combine(fixture.Source.Location, "file.txt"), "payload");
            await fixture.Git("init", "-q"); await fixture.Git("add", ".");
            await fixture.Git("-c", "user.name=test", "-c", "user.email=test@example.invalid", "commit", "-q", "-m", "fixture");
            fixture.Sha = (await fixture.Git("rev-parse", "HEAD")).Trim(); return fixture;
        }
        public async Task<string> Git(params string[] args)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = Source.Location, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in args) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode); Assert.NotNull(await error); return await output;
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
