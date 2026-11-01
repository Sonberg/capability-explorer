using System.Diagnostics;
using CapabilityExplorer.Capabilities;
using Xunit;

namespace CapabilityExplorer.Tests;

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
