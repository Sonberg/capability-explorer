using CapabilityExplorer.Capabilities.Analysis;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class AnalysisWorkerDefaultsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath().Replace("/var/", "/private/var/", StringComparison.Ordinal), "ce-defaults-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_directory, "requests");

    [Fact]
    public void Missing_profile_has_no_defaults_and_does_not_claim_liveness()
    {
        var store = new FileRunStore(Root);
        Assert.Null(store.ReadWorkerDefaults());
        using var owner = store.AcquireWorker();
        store.PublishWorkerDefaults(owner, new(Packages: false));
        owner.Dispose();
        Assert.False(store.ReadWorkerDefaults()!.Settings.Packages);
        using var anotherOwner = store.AcquireWorker();
        Assert.NotNull(anotherOwner);
    }
    [Fact]
    public void Private_profile_roundtrips_nonsecret_policy_and_nullable_test_override()
    {
        var store = new FileRunStore(Root);
        using var owner = store.AcquireWorker();
        var settings = new AnalysisSettings(Packages: false, Locked: true, GitHubAuth: "none", NugetPassEnv: "PRIVATE_FEED_TOKEN",
            NugetAllowedOrigins: "https://packages.example.test", ExtractionJobs: 3, CapabilityWorkers: 2);
        store.PublishWorkerDefaults(owner, settings);
        Assert.Equal(new WorkerDefaults(settings, null), store.ReadWorkerDefaults());
        store.PublishWorkerDefaults(owner, settings with { IncludeTests = true }, false);
        Assert.Equal(false, store.ReadWorkerDefaults()!.IncludeTestsOverride);
        Assert.True(store.ReadWorkerDefaults()!.Settings.IncludeTests);
        Assert.Equal(2, store.ReadWorkerDefaults()!.Settings.CapabilityWorkers);
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(Root, "worker-defaults.json")));
    }
    [Fact]
    public void Older_profile_without_capability_worker_limit_uses_conservative_default()
    {
        var store = new FileRunStore(Root);
        File.WriteAllText(Path.Combine(Root, "worker-defaults.json"),
            """{"settings":{"includeTests":false,"packages":true,"locked":false,"runtimeIdentity":null,"gitHubAuth":"auto","nugetPassEnv":"","nugetAllowedOrigins":null,"extractionJobs":2},"includeTestsOverride":null}""");
        Assert.Equal(2, store.ReadWorkerDefaults()!.Settings.CapabilityWorkers);
    }
    [Fact]
    public void Foreign_owner_and_credential_values_are_refused_without_profile_writes()
    {
        var store = new FileRunStore(Root);
        var foreign = new FileRunStore(Path.Combine(_directory, "foreign"));
        using var foreignOwner = foreign.AcquireWorker();
        Assert.Throws<InvalidOperationException>(() => store.PublishWorkerDefaults(foreignOwner, new()));
        using var owner = store.AcquireWorker();
        Assert.Throws<InvalidDataException>(() => store.PublishWorkerDefaults(owner, new(ExtractionJobs: 5)));
        Assert.Throws<InvalidDataException>(() => store.PublishWorkerDefaults(owner, new(ExtractionJobs: 4, CapabilityWorkers: 5)));
        Assert.Throws<InvalidDataException>(() => store.PublishWorkerDefaults(owner, new(NugetPassEnv: "TOKEN=secret")));
        Assert.Throws<InvalidDataException>(() => store.PublishWorkerDefaults(owner, new(NugetAllowedOrigins: "https://user:secret@packages.example.test")));
        Assert.Throws<InvalidDataException>(() => store.PublishWorkerDefaults(owner, new(NugetPassEnv: "TOKEN\n")));
        Assert.Null(store.ReadWorkerDefaults());
    }
    [Fact]
    public void Profile_links_are_refused_before_read_or_publish()
    {
        if (OperatingSystem.IsWindows()) return;
        var store = new FileRunStore(Root);
        var target = Path.Combine(_directory, "target");
        File.WriteAllText(target, "preserve");
        File.CreateSymbolicLink(Path.Combine(Root, "worker-defaults.json"), target);
        Assert.Throws<IOException>(() => store.ReadWorkerDefaults());
        using var owner = store.AcquireWorker();
        Assert.Throws<IOException>(() => store.PublishWorkerDefaults(owner, new()));
        Assert.Equal("preserve", File.ReadAllText(target));
    }
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
