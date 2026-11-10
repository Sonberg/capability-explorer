using System.Text.Json;
using CapabilityExplorer.Extractors.CSharp;
using Microsoft.CodeAnalysis;
using Xunit;

namespace CapabilityExplorer.Extractors.Tests;

public sealed class DeterministicPackagesTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private readonly string _root = Directory.CreateTempSubdirectory("ce-pkg-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Packs => Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "packs"));

    private string Source(string? packageVersion = "1.*", string? nugetConfig = null, string package = "Fake.Lib")
    {
        var src = Path.Combine(_root, "src-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(src, "App"));
        var versionAttribute = packageVersion is null ? "" : $" Version=\"{packageVersion}\"";
        File.WriteAllText(Path.Combine(src, "App", "App.csproj"),
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="{package}"{versionAttribute} /></ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(src, "App", "Calc.cs"), $"namespace App; public class Calc {{ public int Run() => {package}.Lib.Value(); }}\n");
        if (nugetConfig is not null)
        {
            File.WriteAllText(Path.Combine(src, "nuget.config"), nugetConfig);
        }

        return src;
    }

    private static FakeFeed PublicFeed() => new FakeFeed()
        .Add("Fake.Lib", "1.0.0", "namespace Fake { public static class Lib { public static int Value() => 1; } }");

    private static async Task<FetchResult> Fetch(string source, string cache, HttpMessageHandler handler, IReadOnlyList<PackageFeed> feeds)
    {
        using var http = new HttpClient(handler);
        var models = ProjectModel.Discover(source);
        return await new PackageFetcher(cache, http, feeds, new PackageFeedPolicy("https://feed.test,https://private.test,https://cdn.test")).FetchAsync(models.Select(m => (m.RelativePath, m.PackageRoots())));
    }

    private static IReadOnlyList<UnitResult> Extract(string source, string cache, PackageLock? lockFile) =>
        new CSharpExtractor(new ExtractorOptions(source, "app", Sha, cache, Packs, lockFile)).Extract();

    private static string[] ConfigHashes(IEnumerable<UnitResult> results) =>
        results.Select(r => JsonDocument.Parse(r.Jsonl.Split('\n')[0]).RootElement.GetProperty("configHash").GetString()!).ToArray();

    [Fact]
    public async Task Same_commit_with_different_package_caches_yields_byte_identical_artifacts()
    {
        var source = Source();
        var feed = PublicFeed();
        PackageFeed[] feeds = [new("test", feed.IndexUrl)];

        // Cache A: empty before the packages step.
        var cacheA = Path.Combine(_root, "cache-a");
        var fetchedA = await Fetch(source, cacheA, new FeedRouter(feed), feeds);
        Assert.Empty(fetchedA.Failed);
        var outputA = Extract(source, cacheA, fetchedA.Lock);

        // Cache B: shared cache that other analyses filled with a newer Fake.Lib and unrelated packages.
        var cacheB = Path.Combine(_root, "cache-b");
        var other = new FakeFeed().Add("Fake.Lib", "1.5.0", "namespace Fake { public static class Lib { public static int Value() => 2; public static int Extra() => 3; } }")
            .Add("Unrelated", "9.9.9", "namespace Unrelated { public static class X { } }");
        foreach (var (id, version) in new[] { ("Fake.Lib", "1.5.0"), ("Unrelated", "9.9.9") })
        {
            await Fetch(Source(version, package: id), cacheB, new FeedRouter(other), [new("other", other.IndexUrl)]);
        }

        Assert.True(Directory.Exists(Path.Combine(cacheB, "fake.lib", "1.5.0")));
        var fetchedB = await Fetch(source, cacheB, new FeedRouter(feed), feeds);
        var outputB = Extract(source, cacheB, fetchedB.Lock);

        Assert.Equal(fetchedA.Lock.ToJson(), fetchedB.Lock.ToJson());
        Assert.Contains("\"1.0.0\"", fetchedA.Lock.ToJson());
        Assert.Equal(outputA.Select(r => r.Jsonl), outputB.Select(r => r.Jsonl));
        Assert.NotEmpty(outputA);

        // Control: without the lock the shared cache decides (the old behaviour), and the artifact says so.
        var legacy = Extract(source, cacheB, lockFile: null);
        Assert.NotEqual(ConfigHashes(outputA), ConfigHashes(legacy));
    }

    [Theory]
    [InlineData("1.*", "2.0.0", null)]
    [InlineData("1.*", "1.0.0-beta.1", null)]
    [InlineData("1.*", "1.9.0;1.10.0;2.0.0", "1.10.0")]
    [InlineData("1.0.*", "1.0.9;1.0.10;1.1.0", "1.0.10")]
    [InlineData(null, "1.9.0;1.10.0;2.0.0-beta.1", "1.10.0")]
    [InlineData("1.0.0-*", "1.0.0-beta.1;2.0.0", "1.0.0-beta.1")]
    [InlineData("(1.0.0,2.0.0)", "1.0.0;1.1.0;1.5.0;2.0.0", "1.1.0")]
    [InlineData("(1.0.0,)", "1.0.0;2.0.0;3.0.0", "2.0.0")]
    [InlineData("(1.0.0,2.0.0)", "1.0.0;2.0.0", null)]
    [InlineData("[1.0.0,2.0.0)", "1.0.0;1.1.0", "1.0.0")]
    [InlineData("[1.0.0,2.0.0)", "1.1.0;1.10.0;2.0.0", "1.1.0")]
    [InlineData("1.0.0", "1.1.0;1.10.0", "1.1.0")]
    [InlineData("[1.0.0,2.0.0)", "2.0.0;3.0.0", null)]
    [InlineData("[1.0.0]", "1.1.0", null)]
    [InlineData("[1.0.0,2.0.0)", "1.1.0-beta.1;1.2.0", "1.2.0")]
    public async Task Feed_selection_preserves_the_request_and_semantic_version_order(string? request, string available, string? expected)
    {
        var source = Source(request);
        var feed = new FakeFeed();
        var framework = Directory.EnumerateDirectories(Path.Combine(Packs, "Microsoft.NETCore.App.Ref"))
            .SelectMany(d => Directory.EnumerateDirectories(Path.Combine(d, "ref"))).Single();
        var references = Directory.EnumerateFiles(framework, "*.dll").Select(p => MetadataReference.CreateFromFile(p)).ToArray();
        foreach (var version in available.Split(';'))
            feed.Add("Fake.Lib", version, "namespace Fake.Lib { public static class Lib { public static int Value() => 1; } }", references);
        var cache = Path.Combine(_root, "cache");
        var fetched = await Fetch(source, cache, new FeedRouter(feed), [new("test", feed.IndexUrl)]);
        if (expected is null)
        {
            Assert.NotEmpty(fetched.Failed);
            Assert.Empty(fetched.Lock.For("App/App.csproj"));
        }
        else
        {
            Assert.Empty(fetched.Failed);
            Assert.Equal(expected, Assert.Single(fetched.Lock.For("App/App.csproj")).Version);
            var artifact = Assert.Single(Extract(source, cache, fetched.Lock)).Jsonl;
            var diagnostics = artifact.Split('\n').Where(line => line.Contains("\"kind\":\"diagnostic\"", StringComparison.Ordinal))
                .Select(line =>
                {
                    using var record = JsonDocument.Parse(line);
                    return record.RootElement.GetProperty("code").GetString();
                }).ToArray();
            Assert.Empty(diagnostics);
            Assert.Contains("\"status\":\"full\"", artifact);
        }
    }

    [Theory]
    [InlineData("unauthorized-package")]
    [InlineData("corrupt-package")]
    [InlineData("unauthorized-index")]
    [InlineData("invalid-index")]
    [InlineData("invalid-index-shape")]
    public async Task Inclusive_fallback_does_not_hide_feed_or_package_failures(string failure)
    {
        var source = Source("[1.0.0,2.0.0)");
        var feed = new FakeFeed().Add("Fake.Lib", "1.1.0", "namespace Fake.Lib { public static class Lib { public static int Value() => 1; } }");
        var handler = new InterceptHandler(new FeedRouter(feed), request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith(".nupkg", StringComparison.Ordinal) && failure == "unauthorized-package")
                return new(System.Net.HttpStatusCode.Unauthorized);
            if (path.EndsWith(".nupkg", StringComparison.Ordinal) && failure == "corrupt-package")
                return new(System.Net.HttpStatusCode.OK) { Content = new StringContent("not a package") };
            if (path.EndsWith("/fake.lib/index.json", StringComparison.Ordinal) && failure == "unauthorized-index")
                return new(System.Net.HttpStatusCode.Unauthorized);
            if (path.EndsWith("/fake.lib/index.json", StringComparison.Ordinal) && failure == "invalid-index")
                return new(System.Net.HttpStatusCode.OK) { Content = new StringContent("not JSON") };
            if (path.EndsWith("/fake.lib/index.json", StringComparison.Ordinal) && failure == "invalid-index-shape")
                return new(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"versions":42}""") };
            return null;
        });
        var fetched = await Fetch(source, Path.Combine(_root, "cache"), handler, [new("test", feed.IndexUrl)]);
        Assert.NotEmpty(fetched.Failed);
        Assert.Empty(fetched.Lock.For("App/App.csproj"));
        Assert.DoesNotContain(feed.Requests, r => r.Url.AbsolutePath.Contains("/1.1.0/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Inclusive_minimum_on_later_feed_wins_before_fallback()
    {
        var first = new FakeFeed().Add("Fake.Lib", "1.1.0", "namespace Fake.Lib { public static class Lib { public static int Value() => 1; } }");
        var second = new FakeFeed("private.test").Add("Fake.Lib", "1.0.0", "namespace Fake.Lib { public static class Lib { public static int Value() => 1; } }");
        var fetched = await Fetch(Source("[1.0.0,2.0.0)"), Path.Combine(_root, "cache"),
            new FeedRouter(first, second), [new("first", first.IndexUrl), new("second", second.IndexUrl)]);
        Assert.Empty(fetched.Failed);
        Assert.Equal("1.0.0", Assert.Single(fetched.Lock.For("App/App.csproj")).Version);
        Assert.DoesNotContain(first.Requests, r => r.Url.AbsolutePath.EndsWith("/fake.lib/index.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_minimum_listed_in_index_is_reported_as_inconsistent()
    {
        var feed = new FakeFeed().Add("Fake.Lib", "1.0.0", "namespace Fake.Lib { public static class Lib { public static int Value() => 1; } }")
            .Add("Fake.Lib", "1.1.0", "namespace Fake.Lib { public static class Lib { public static int Value() => 1; } }");
        var handler = new InterceptHandler(new FeedRouter(feed), request =>
            request.RequestUri!.AbsolutePath.EndsWith("/fake.lib.1.0.0.nupkg", StringComparison.Ordinal)
                ? new(System.Net.HttpStatusCode.NotFound) : null);
        var fetched = await Fetch(Source("[1.0.0,2.0.0)"), Path.Combine(_root, "cache"), handler, [new("test", feed.IndexUrl)]);
        Assert.Contains(fetched.Failed, failure => failure.Contains("lists the missing minimum", StringComparison.Ordinal));
        Assert.Empty(fetched.Lock.For("App/App.csproj"));
    }

    [Fact]
    public async Task Cancelling_fallback_index_lookup_cancels_the_fetch()
    {
        var feed = new FakeFeed().Add("Fake.Lib", "1.1.0", "namespace Fake.Lib { public static class Lib { public static int Value() => 1; } }");
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new InterceptHandler(new FeedRouter(feed), request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/fake.lib/index.json", StringComparison.Ordinal))
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
            return null;
        }));
        var fetcher = new PackageFetcher(Path.Combine(_root, "cache"), http, [new("test", feed.IndexUrl)], new PackageFeedPolicy("https://feed.test"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetcher.FetchAsync(
            [("App.csproj", (IReadOnlyList<PackageRef>)[new("Fake.Lib", "[1.0.0,2.0.0)", 1)])], cancellation.Token));
    }

    private sealed class InterceptHandler(HttpMessageHandler inner, Func<HttpRequestMessage, HttpResponseMessage?> intercept) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            intercept(request) is { } response ? Task.FromResult(response) : base.SendAsync(request, cancellationToken);
    }

    [Fact]
    public async Task Feed_selection_cache_keeps_distinct_complete_ranges_separate()
    {
        var feed = PublicFeed().Add("Fake.Lib", "2.0.0", "namespace Fake { public static class Lib { public static int Value() => 2; } }");
        using var http = new HttpClient(new FeedRouter(feed));
        var cache = Path.Combine(_root, "cache");
        IReadOnlyList<(string Project, IReadOnlyList<PackageRef> Roots)> projects =
        [
            ("A.csproj", [new("Fake.Lib", "(,2.0.0)", 1)]),
            ("B.csproj", [new("Fake.Lib", "(,3.0.0)", 1)]),
        ];
        var fetched = await new PackageFetcher(cache, http, [new("test", feed.IndexUrl)],
            new PackageFeedPolicy("https://feed.test")).FetchAsync(projects);
        Assert.Empty(fetched.Failed);
        Assert.Equal("1.0.0", Assert.Single(fetched.Lock.For("A.csproj")).Version);
        Assert.Equal("2.0.0", Assert.Single(fetched.Lock.For("B.csproj")).Version);
        foreach (var project in projects)
        {
            var resolved = new PackageResolver(cache).Resolve(project.Roots, fetched.Lock.For(project.Project));
            Assert.Equal(fetched.Lock.For(project.Project), resolved.Resolved);
            Assert.Empty(resolved.Issues);
        }
    }

    [Fact]
    public async Task Feed_selection_cache_does_not_replace_an_invalid_empty_request_with_an_omitted_one()
    {
        var feed = PublicFeed();
        using var http = new HttpClient(new FeedRouter(feed));
        IReadOnlyList<(string Project, IReadOnlyList<PackageRef> Roots)> projects =
        [
            ("A.csproj", [new("Fake.Lib", null, 1)]),
            ("B.csproj", [new("Fake.Lib", "", 1)]),
        ];
        var fetched = await new PackageFetcher(Path.Combine(_root, "cache"), http, [new("test", feed.IndexUrl)],
            new PackageFeedPolicy("https://feed.test")).FetchAsync(projects);
        Assert.Single(fetched.Failed);
        Assert.Equal("1.0.0", Assert.Single(fetched.Lock.For("A.csproj")).Version);
        Assert.Empty(fetched.Lock.For("B.csproj"));
    }

    [Fact]
    public async Task The_resolved_package_set_is_part_of_the_config_hash()
    {
        var source = Source("1.0.0");
        var feed = PublicFeed().Add("Fake.Lib", "1.1.0", "namespace Fake { public static class Lib { public static int Value() => 1; } }");
        var cache = Path.Combine(_root, "cache");
        await Fetch(source, cache, new FeedRouter(feed), [new("test", feed.IndexUrl)]);
        await Fetch(Source("1.1.0"), cache, new FeedRouter(feed), [new("test", feed.IndexUrl)]);

        var one = new PackageLock();
        one.Set("App/App.csproj", [new LockedPackage("Fake.Lib", "1.0.0")]);
        var two = new PackageLock();
        two.Set("App/App.csproj", [new LockedPackage("Fake.Lib", "1.1.0")]);

        Assert.NotEqual(ConfigHashes(Extract(source, cache, one)), ConfigHashes(Extract(source, cache, two)));
        Assert.Equal(ConfigHashes(Extract(source, cache, one)), ConfigHashes(Extract(source, cache, one)));
    }

    [Fact]
    public async Task A_locked_package_missing_from_the_folder_fails_instead_of_changing_the_output()
    {
        var source = Source("1.0.0");
        var feed = PublicFeed();
        var fetched = await Fetch(source, Path.Combine(_root, "cache"), new FeedRouter(feed), [new("test", feed.IndexUrl)]);
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        var e = Assert.Throws<InvalidOperationException>(() => Extract(source, empty, fetched.Lock));
        Assert.Contains("Fake.Lib 1.0.0", e.Message);
    }

    [Fact]
    public void The_lock_file_is_canonical_and_round_trips()
    {
        var a = new PackageLock();
        a.Set("b/B.csproj", [new LockedPackage("Zed", "1.0.0"), new LockedPackage("alpha", "2.0.0")]);
        a.Set("a/A.csproj", [new LockedPackage("Mid", "3.0.0")]);
        var b = new PackageLock();
        b.Set("a/A.csproj", [new LockedPackage("Mid", "3.0.0")]);
        b.Set("b/B.csproj", [new LockedPackage("alpha", "2.0.0"), new LockedPackage("Zed", "1.0.0")]);
        Assert.Equal(a.ToJson(), b.ToJson());
        Assert.Equal(a.ToJson(), PackageLock.Parse(a.ToJson()).ToJson());
    }

    [Fact]
    public async Task Private_feed_credentials_come_from_the_environment_and_go_only_to_that_feed()
    {
        const string user = "svc-reader";
        const string secret = "s3cr3t-token-value";
        var config = """
            <configuration>
              <packageSources>
                <clear />
                <add key="corp" value="https://private.test/v3/index.json" />
              </packageSources>
              <packageSourceCredentials>
                <corp>
                  <add key="Username" value="%CORP_USER%" />
                  <add key="ClearTextPassword" value="%CORP_TOKEN%" />
                </corp>
              </packageSourceCredentials>
            </configuration>
            """;
        var source = Source("1.0.0", config);
        var env = new Dictionary<string, string> { ["CORP_USER"] = user, ["CORP_TOKEN"] = secret };
        var feeds = NuGetConfig.Discover(source, ProjectModel.Discover(source).Select(m => Path.GetDirectoryName(m.FullPath)!), k => env.GetValueOrDefault(k), new PackageFeedPolicy("https://private.test"));
        var feedConfig = Assert.Single(feeds);
        Assert.Equal("corp", feedConfig.Name);
        Assert.DoesNotContain(secret, feedConfig.Credentials!.ToString() + feedConfig);

        var priv = new FakeFeed("private.test", requiredBasic: $"{user}:{secret}", redirectNupkgsTo: "cdn.test")
            .Add("Fake.Lib", "1.0.0", "namespace Fake { public static class Lib { public static int Value() => 1; } }");
        var cache = Path.Combine(_root, "cache");
        var fetched = await Fetch(source, cache, new FeedRouter(priv), feeds);

        Assert.Empty(fetched.Failed);
        Assert.Equal("1.0.0", Assert.Single(fetched.Lock.For("App/App.csproj")).Version);
        Assert.Contains(priv.Requests, r => r.Url.Host == "private.test" && r.Authorization is not null);
        Assert.DoesNotContain(priv.Requests, r => r.Url.Host == "cdn.test" && r.Authorization is not null); // never to the redirect target
        var artifacts = string.Concat(Extract(source, cache, fetched.Lock).Select(r => r.Jsonl));
        Assert.DoesNotContain(secret, artifacts + fetched.Lock.ToJson() + string.Join("\n", fetched.Failed));
        Assert.DoesNotContain(user, artifacts + fetched.Lock.ToJson());
    }

    [Fact]
    public async Task Without_credentials_a_private_package_is_reported_unresolved_not_guessed()
    {
        var config = """<configuration><packageSources><clear /><add key="corp" value="https://private.test/v3/index.json" /></packageSources></configuration>""";
        var source = Source("1.0.0", config);
        var feeds = NuGetConfig.Discover(source, ProjectModel.Discover(source).Select(m => Path.GetDirectoryName(m.FullPath)!), _ => null, new PackageFeedPolicy("https://private.test,https://corp.test"));
        var priv = new FakeFeed("private.test", requiredBasic: "u:p").Add("Fake.Lib", "1.0.0", "namespace Fake { public static class Lib { public static int Value() => 1; } }");
        var cache = Path.Combine(_root, "cache");
        var fetched = await Fetch(source, cache, new FeedRouter(priv), feeds);
        Assert.Empty(fetched.Lock.For("App/App.csproj"));
        Assert.NotEmpty(fetched.Failed);
        var output = Extract(source, cache, fetched.Lock);
        Assert.Contains("UNRESOLVED_PACKAGE", Assert.Single(output).Jsonl);
    }

    [Fact]
    public void Only_https_feeds_are_used_and_default_nuget_org_is_kept_unless_cleared()
    {
        var config = """
            <configuration><packageSources>
              <add key="plain" value="http://insecure.test/index.json" />
              <add key="local" value="/tmp/feed" />
              <add key="corp" value="https://corp.test/v3/index.json" />
            </packageSources></configuration>
            """;
        var source = Source("1.0.0", config);
        var feeds = NuGetConfig.Discover(source, ProjectModel.Discover(source).Select(m => Path.GetDirectoryName(m.FullPath)!), _ => null, new PackageFeedPolicy("https://private.test,https://corp.test"));
        Assert.Equal(["nuget.org", "corp"], feeds.Select(f => f.Name));
    }
}
