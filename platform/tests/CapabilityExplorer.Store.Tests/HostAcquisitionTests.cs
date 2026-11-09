extern alias analysiscli;
using System.Diagnostics;
using System.Text.Json;
using analysiscli::CapabilityExplorer.Cli;
using CapabilityExplorer.Capabilities.Analysis;
using Xunit;

namespace CapabilityExplorer.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class HostAcquisitionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Environment.CurrentDirectory, ".sandbox-cache", "host-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _host;
    private const string Secret = "fixture-acquisition-secret";
    public HostAcquisitionTests()
    {
        _root = Path.Combine(_directory, "workspace");
        _host = Path.Combine(_directory, "host/settings.json");
        Directory.CreateDirectory(_root);
    }
    private string Write(string relative, string text, bool executable = false)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        if (executable && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
    private string Legacy() => Write(".sandbox-cache/package-feeds/settings.json", JsonSerializer.Serialize(new { feeds = new[] {
        new { id = "private", kind = "nuget", name = "Private", url = "https://packages.example.invalid/v3/index.json", username = "fixture", secret = Secret }
    } }));

    [Fact]
    public void Migration_preserves_verified_private_backup_and_sanitizes_workspace_catalog_and_legacy()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var legacy = Legacy();
        var original = File.ReadAllText(legacy);
        var catalog = HostPackageFeeds.DefaultCatalog(_root);
        HostPackageFeeds.Import(_root, legacy, catalog, _host);
        Assert.Equal(original, File.ReadAllText(_host));
        Assert.Equal(original, File.ReadAllText(Assert.Single(Directory.GetFiles(Path.GetDirectoryName(_host)!, "import-backup-*.json"))));
        Assert.DoesNotContain(Secret, File.ReadAllText(legacy), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, File.ReadAllText(catalog), StringComparison.Ordinal);
        using var parsed = JsonDocument.Parse(File.ReadAllText(catalog));
        Assert.Equal(1, parsed.RootElement.GetProperty("version").GetInt32());
        Assert.True(parsed.RootElement.GetProperty("feeds")[0].GetProperty("hasCredential").GetBoolean());
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_host));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(_host)!));
        var prior = Environment.GetEnvironmentVariable("CE_PACKAGE_FEEDS_FILE");
        try
        {
            Environment.SetEnvironmentVariable("CE_PACKAGE_FEEDS_FILE", _host);
            Assert.Contains(Secret, HostPackageFeeds.Read(_root), StringComparison.Ordinal);
            HostPackageFeeds.Publish(_root);
            Assert.DoesNotContain(Secret, File.ReadAllText(catalog), StringComparison.Ordinal);
        }
        finally { Environment.SetEnvironmentVariable("CE_PACKAGE_FEEDS_FILE", prior); }
    }

    [Fact]
    public void Migration_failures_preserve_only_copy_and_workspace_overrides_are_rejected()
    {
        var legacy = Legacy();
        var original = File.ReadAllText(legacy);
        Assert.Throws<InvalidDataException>(() => HostPackageFeeds.Import(_root, legacy, HostPackageFeeds.DefaultCatalog(_root), Path.Combine(_root, "secrets.json")));
        Assert.Equal(original, File.ReadAllText(legacy));
        var prior = Environment.GetEnvironmentVariable("CE_PACKAGE_FEEDS_FILE");
        try
        {
            Environment.SetEnvironmentVariable("CE_PACKAGE_FEEDS_FILE", legacy);
            Assert.Throws<InvalidDataException>(() => HostPackageFeeds.Read(_root));
            Environment.SetEnvironmentVariable("CE_PACKAGE_FEEDS_FILE", _host);
            Assert.Throws<InvalidDataException>(() => HostPackageFeeds.Read(_root));
        }
        finally { Environment.SetEnvironmentVariable("CE_PACKAGE_FEEDS_FILE", prior); }
    }

    [Theory]
    [InlineData("http://insecure.example/feed", "fixture", "secret")]
    [InlineData("https://user:password@example.invalid/feed", "fixture", "secret")]
    [InlineData("https://example.invalid/feed", "", "secret")]
    [InlineData("https://example.invalid/feed", "fixture", "bad\nsecret")]
    public void Migration_rejects_invalid_url_username_and_credentials_before_persistence(string url, string username, string secret)
    {
        var input = Write("old.json", JsonSerializer.Serialize(new { feeds = new[] { new { id = "private", kind = "nuget", url, username, secret } } }));
        Assert.Throws<InvalidDataException>(() => HostPackageFeeds.Import(_root, input, HostPackageFeeds.DefaultCatalog(_root), _host));
        Assert.False(File.Exists(_host));
        Assert.False(File.Exists(HostPackageFeeds.DefaultCatalog(_root)));
    }

    [Fact]
    public void Migration_rejects_symlink_destination_without_touching_legacy()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var legacy = Legacy();
        Directory.CreateDirectory(Path.GetDirectoryName(_host)!);
        var target = Path.Combine(_directory, "target");
        File.WriteAllText(target, "untouched");
        File.CreateSymbolicLink(_host, target);
        Assert.Throws<IOException>(() => HostPackageFeeds.Import(_root, legacy, HostPackageFeeds.DefaultCatalog(_root), _host));
        Assert.Equal("untouched", File.ReadAllText(target));
        Assert.Contains(Secret, File.ReadAllText(legacy), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Named_account_token_resolution_uses_the_requested_gh_login_before_environment_tokens()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var priorPath = Environment.GetEnvironmentVariable("PATH");
        var priorGithubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        var priorGhToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        try
        {
            Write("bin/gh", "#!/bin/sh\n[ \"$*\" = 'auth token --hostname github.com --user fixture-account' ] || exit 10\nprintf fixture-token\n", true);
            Environment.SetEnvironmentVariable("PATH", Path.Combine(_root, "bin") + Path.PathSeparator + priorPath);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", "environment-token");
            Environment.SetEnvironmentVariable("GH_TOKEN", "environment-token");
            var start = new ProcessStartInfo(Path.Combine(Fixtures.RepositoryRoot, "scripts/github-token"))
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("account:fixture-account");
            using var process = Process.Start(start)!;
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("fixture-token", output);
            Assert.Contains("GitHub CLI account fixture-account", error, StringComparison.Ordinal);
            Assert.DoesNotContain("environment-token", output, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", priorPath);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", priorGithubToken);
            Environment.SetEnvironmentVariable("GH_TOKEN", priorGhToken);
        }
    }

    [Fact]
    public async Task Host_worker_resolves_credentials_only_for_controlled_git_and_sanitizes_remote_output()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var priorPath = Environment.GetEnvironmentVariable("PATH");
        var priorToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        try
        {
            Write("scripts/github-token", "#!/bin/sh\n[ \"$1\" = auto ] || exit 3\nprintf '%s' \"$GITHUB_TOKEN\"\n", true);
            Write("bin/git", """
                #!/bin/sh
                env | grep -q '^GIT_CONFIG_KEY_[0-9]*=http.https://github.com/.extraheader$' || exit 10
                env | grep -q '^GIT_CONFIG_VALUE_[0-9]*=AUTHORIZATION: basic eC1hY2Nlc3MtdG9rZW46Zml4dHVyZS1hY3F1aXNpdGlvbi1zZWNyZXQ=$' || exit 11
                case "$*" in
                  *clone*) exit 0 ;;
                  *rev-parse*) echo aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa ;;
                  *show*) printf '1700000000\nfixture-acquisition-secret\n' ;;
                  *) exit 12 ;;
                esac
                """ + "\n", true);
            var configuration = Write("capabilities.json", """{"capabilities":[{"id":"sales","name":"Sales","repositories":[{"id":"private","kind":"github","location":"https://github.com/fixture/private","ref":"HEAD"}]}]}""");
            Environment.SetEnvironmentVariable("PATH", Path.Combine(_root, "bin") + Path.PathSeparator + priorPath);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", Secret);
            var store = new FileRunStore(Path.Combine(_root, ".sandbox-cache/requests"));
            var accepted = store.AdmitCheck("sales", configuration).Check;
            using var owner = store.AcquireWorker();
            var claimed = store.ClaimCheck(owner)!;
            await HostSourceAcquisition.ExecuteCheck(_root, store, owner, claimed, CancellationToken.None);
            var completed = store.ReadCheck(accepted.RequestId);
            Assert.Equal("done", completed.State);
            Assert.Equal(new string('a', 40), completed.Snapshot!.Repositories[0].CommitSha);
            Assert.Null(completed.Snapshot.Repositories[0].Subject);
            foreach (var file in Directory.GetFiles(Path.Combine(_root, ".sandbox-cache/requests"), "*.json", SearchOption.AllDirectories))
                Assert.DoesNotContain(Secret, File.ReadAllText(file), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", priorPath);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", priorToken);
        }
    }

    [Fact]
    public async Task Real_local_git_fixture_checks_without_extraction_or_database()
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(repo);
        async Task<string> Git(params string[] args)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, error);
            return output.Trim();
        }
        await Git("init", "--quiet");
        File.WriteAllText(Path.Combine(repo, "fixture.txt"), "local fixture");
        await Git("add", "fixture.txt");
        await Git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "fixture");
        var sha = await Git("rev-parse", "HEAD");
        Write("scripts/github-token", "#!/bin/sh\nexit 0\n", true);
        var configuration = Write("capabilities.json", JsonSerializer.Serialize(new { capabilities = new[] {
            new { id = "sales", name = "Sales", repositories = new[] { new { id = "repo", location = repo, kind = "local", @ref = "HEAD" } } }
        } }));
        var store = new FileRunStore(Path.Combine(_root, ".sandbox-cache/requests"));
        store.AdmitCheck("sales", configuration);
        using var owner = store.AcquireWorker();
        var check = store.ClaimCheck(owner)!;
        await HostSourceAcquisition.ExecuteCheck(_root, store, owner, check, CancellationToken.None);
        var completed = store.ReadCheck(check.RequestId);
        Assert.Equal("done", completed.State);
        Assert.Equal(sha, completed.Snapshot!.Repositories[0].CommitSha);
        Assert.False(Directory.Exists(Path.Combine(_root, ".sandbox-cache/analyze-packages")));
    }

    [Fact]
    public async Task Cancelled_git_execution_becomes_unknown_and_cannot_publish_or_unlock_analysis()
    {
        var priorPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Write("scripts/github-token", "#!/bin/sh\nexit 0\n", true);
            var marker = Path.Combine(_root, "started");
            Write("bin/git", "#!/bin/sh\n: > '" + marker + "'\nsleep 30\n", true);
            Environment.SetEnvironmentVariable("PATH", Path.Combine(_root, "bin") + Path.PathSeparator + priorPath);
            var configuration = Write("capabilities.json", """{"capabilities":[{"id":"sales","name":"Sales","repositories":[{"id":"private","kind":"github","location":"https://github.com/fixture/private","ref":"HEAD"}]}]}""");
            var store = new FileRunStore(Path.Combine(_root, ".sandbox-cache/requests"));
            store.AdmitCheck("sales", configuration);
            using var owner = store.AcquireWorker();
            var check = store.ClaimCheck(owner)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var execution = HostSourceAcquisition.ExecuteCheck(_root, store, owner, check, timeout.Token);
            while (!File.Exists(marker)) await Task.Delay(20, timeout.Token);
            store.CancelCheck(check.RequestId);
            await execution.WaitAsync(timeout.Token);
            Assert.Equal("unknown", store.ReadCheck(check.RequestId).State);
            Assert.Null(store.ReadCheck(check.RequestId).Snapshot);
            Assert.Throws<InvalidOperationException>(() => store.ClaimNext(owner));
        }
        finally { Environment.SetEnvironmentVariable("PATH", priorPath); }
    }

    [Fact]
    public async Task Direct_export_cli_refuses_before_creating_shared_cache()
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { typeof(AnalysisWorkerCommand).Assembly.Location, "export", "--capability", "sales", "--out", "out" })
            start.ArgumentList.Add(argument);
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        await child.WaitForExitAsync();
        Assert.Equal(2, child.ExitCode);
        Assert.Contains("direct acquisition is disabled", await error, StringComparison.Ordinal);
        Assert.Equal("", await output);
        Assert.False(Directory.Exists(Path.Combine(_root, ".sandbox-cache")));
        Assert.False(Directory.Exists(Path.Combine(_root, "out")));
    }
    public void Dispose() => Directory.Delete(_directory, true);
}
