using CapabilityExplorer.Capabilities;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class SourceParserTests
{
    [Theory]
    [InlineData("https://github.com/octocat/Hello-World", "hello-world", "https://github.com/octocat/Hello-World", "HEAD")]
    [InlineData("https://github.com/octocat/Hello-World.git", "hello-world", "https://github.com/octocat/Hello-World", "HEAD")]
    [InlineData("https://github.com/octocat/Hello-World/", "hello-world", "https://github.com/octocat/Hello-World", "HEAD")]
    [InlineData("https://github.com/octocat/Hello-World/tree/release/2.x", "hello-world", "https://github.com/octocat/Hello-World", "release/2.x")]
    [InlineData("https://github.com/octocat/Hello-World v1.2.0", "hello-world", "https://github.com/octocat/Hello-World", "v1.2.0")]
    [InlineData("sales=https://github.com/acme/sales-api", "sales", "https://github.com/acme/sales-api", "HEAD")]
    public void GitHub_urls(string line, string id, string location, string reference)
    {
        var (sources, errors) = SourceParser.Parse([line]);
        Assert.Empty(errors);
        var s = Assert.Single(sources);
        Assert.Equal((id, location, SourceKind.GitHub, reference), (s.Id, s.Location, s.Kind, s.Ref));
    }

    [Theory]
    [InlineData("/home/me/code/Sales.Api", "sales.api", "HEAD")]
    [InlineData("fixtures/.repos/api-cs v1-base", "api-cs", "v1-base")]
    [InlineData("web=../web-client/", "web", "HEAD")]
    public void Local_paths(string line, string id, string reference)
    {
        var s = Assert.Single(SourceParser.Parse([line]).Sources);
        Assert.Equal((id, SourceKind.Local, reference), (s.Id, s.Kind, s.Ref));
    }

    [Theory]
    [InlineData("https://gitlab.com/acme/repo", "only https://github.com")]
    [InlineData("git@github.com:acme/repo.git", "only https://github.com")]
    [InlineData("https://github.com/acme/repo main extra", "expected '[id=]location [ref]'")]
    [InlineData("https://github.com/acme/repo ../../etc", "not an acceptable branch")]
    [InlineData("https://github.com/acme/repo -upload-pack=evil", "not an acceptable branch")]
    [InlineData("/data/ÄÖÅ", "must match")]
    public void Rejected_lines_say_why(string line, string message)
    {
        var (sources, errors) = SourceParser.Parse([line]);
        Assert.Empty(sources);
        Assert.Contains(message, Assert.Single(errors).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Blank_lines_and_comments_are_ignored_and_duplicates_rejected()
    {
        var (sources, errors) = SourceParser.Parse(["# sales flow", "", "https://github.com/a/api", "https://github.com/b/api"]);
        Assert.Single(sources);
        Assert.Equal(4, Assert.Single(errors).Line);
    }
}

public sealed class CapabilityStoreTests
{
    private static (string Definitions, string Snapshots) Paths()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        return (Path.Combine(dir, "capabilities.json"), Path.Combine(dir, "ws", "snapshots.json"));
    }

    private static RepositorySource Repo(string id, string location) => new(id, location, SourceKind.Local);

    [Fact]
    public async Task Definitions_round_trip_and_snapshots_live_in_their_own_file()
    {
        var (defs, snaps) = Paths();
        using (var store = CapabilityStore.Open(defs, snaps))
        {
            var c = await store.AddAsync("Weekly sales", " desc ", [Repo("api-cs", "/r/api-cs")]);
            Assert.Equal(("weekly-sales", "desc"), (c.Id, c.Description));
            var before = await File.ReadAllTextAsync(defs);
            await store.SaveSnapshotAsync(new CapabilitySnapshot(c.Id, DateTimeOffset.UnixEpoch, [new LockedRepository("api-cs", "HEAD", new string('a', 40), "s", null, null)]));
            Assert.Equal(before, await File.ReadAllTextAsync(defs)); // checking never rewrites configuration
        }

        using var reopened = CapabilityStore.Open(defs, snaps);
        Assert.Equal("Weekly sales", Assert.Single(reopened.Capabilities).Name);
        Assert.Equal(new string('a', 40), reopened.Snapshot("weekly-sales")!.Repositories[0].CommitSha);
        Assert.Equal("/r/api-cs", reopened.FindRepository("api-cs")!.Location);
    }

    [Fact]
    public async Task Repository_ids_are_global_and_capability_ids_unique()
    {
        var (defs, snaps) = Paths();
        using var store = CapabilityStore.Open(defs, snaps);
        await store.AddAsync("Sales", null, [Repo("api-cs", "/r/api-cs")]);
        var second = await store.AddAsync("Sales", null, [Repo("api-cs", "/r/api-cs")]); // same location: allowed
        Assert.Equal("sales-2", second.Id);
        var e = await Assert.ThrowsAsync<CapabilityConflictException>(() => store.AddAsync("Other", null, [Repo("api-cs", "/elsewhere/api-cs")]));
        Assert.Contains("already refers to /r/api-cs", e.Message, StringComparison.Ordinal);
        Assert.True(await store.RemoveAsync("sales"));
        Assert.False(await store.RemoveAsync("sales"));
    }
}

public sealed class SourceSyncTests
{
    private static string FixtureRepo(string id) => Fixtures.RepoDirectory(id);

    [Fact]
    public async Task Local_repositories_lock_to_the_ref_they_name()
    {
        var sync = new SourceSync(Directory.CreateTempSubdirectory().FullName);
        var head = await sync.LockAsync(new RepositorySource("api-cs", FixtureRepo("api-cs"), SourceKind.Local));
        Assert.Equal(Fixtures.Lock["api-cs"]["v3-filter"], head.CommitSha);
        Assert.Equal("Exclude returns from weekly average", head.Subject);

        var tagged = await sync.LockAsync(new RepositorySource("api-cs", FixtureRepo("api-cs"), SourceKind.Local, "v1-base"));
        Assert.Equal(Fixtures.Lock["api-cs"]["v1-base"], tagged.CommitSha);
    }

    [Fact]
    public async Task Failures_are_reported_per_repository()
    {
        var sync = new SourceSync(Directory.CreateTempSubdirectory().FullName);
        var missing = await sync.LockAsync(new RepositorySource("x", "/does/not/exist", SourceKind.Local));
        Assert.Contains("not a git repository", missing.Error, StringComparison.Ordinal);
        var badRef = await sync.LockAsync(new RepositorySource("api-cs", FixtureRepo("api-cs"), SourceKind.Local, "no-such-tag"));
        Assert.Contains("not found", badRef.Error, StringComparison.Ordinal);

        var snapshot = await sync.CheckAsync(new CapabilityDefinition("c", "C", null, [
            new RepositorySource("api-cs", FixtureRepo("api-cs"), SourceKind.Local),
            new RepositorySource("x", "/does/not/exist", SourceKind.Local),
        ]));
        Assert.Equal("selected-code-revisions", snapshot.Kind);
        Assert.NotNull(snapshot.Repositories[0].CommitSha);
        Assert.NotNull(snapshot.Repositories[1].Error);
    }

    [Fact]
    public void GitHub_sources_become_bare_clones_under_the_workspace()
    {
        var sync = new SourceSync("/ws");
        Assert.Equal("/ws/github/octocat/Hello-World.git", sync.GitDirectory(new RepositorySource("hello-world", "https://github.com/octocat/Hello-World", SourceKind.GitHub)));
    }
}
