using CapabilityExplorer.Extractors.CSharp;
using System.Net;
using System.IO.Compression;
using Xunit;

namespace CapabilityExplorer.Extractors.Tests;

public sealed class PackageSecurityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ce-security-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public void Given_an_existing_package_path_when_checked_then_each_ancestor_requires_one_filesystem_inspection()
    {
        var cache = Path.Combine(root,"cache");
        var package = Path.Combine(cache,"safe","1.0.0");
        Directory.CreateDirectory(package);
        var inspections = 0;
        PackageSafety.NoLinks(cache,package,() => inspections++);
        Assert.Equal(3,inspections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Given_a_linked_package_ancestor_when_checked_then_existing_and_missing_targets_are_rejected(bool missing)
    {
        var cache = Directory.CreateDirectory(Path.Combine(root,"cache")).FullName;
        var target = Path.Combine(root,"target");
        if (!missing) Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(Path.Combine(cache,"safe"),target);
        Assert.Throws<InvalidDataException>(() => PackageSafety.NoLinks(cache,Path.Combine(cache,"safe","1.0.0")));
    }

    [Fact]
    public void Given_a_missing_package_path_when_checked_then_it_remains_available_for_safe_acquisition()
    {
        var cache = Directory.CreateDirectory(Path.Combine(root,"cache")).FullName;
        PackageSafety.NoLinks(cache,Path.Combine(cache,"safe","1.0.0"));
        Assert.False(Directory.Exists(Path.Combine(cache,"safe")));
    }

    [Theory]
    [InlineData("../outside", "1.0.0")]
    [InlineData("Safe", "../outside")]
    public void Locked_package_paths_cannot_escape_cache(string id, string version)
    {
        Directory.CreateDirectory(Path.GetFullPath(Path.Combine(root, "cache", id.ToLowerInvariant(), version.ToLowerInvariant())));
        Assert.Throws<InvalidDataException>(() => new PackageResolver(Path.Combine(root, "cache")).Resolve([], [new(id, version)]));
    }
    [Fact]
    public async Task Fetch_rejects_path_in_package_id_before_network_or_cache_access()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var result = await new PackageFetcher(Path.Combine(root, "cache"), http).FetchAsync([("App.csproj", (IReadOnlyList<PackageRef>)[new("../outside", "1.0.0", 1)])]);
        Assert.Empty(handler.Requests);
        Assert.NotEmpty(result.Failed);
    }
    [Fact]
    public async Task Feed_base_must_be_https_before_network()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var result = await new PackageFetcher(root, http, [new("unsafe", "https://feed.test/index.json", null, "http://feed.test/flat/")], new PackageFeedPolicy("https://feed.test"))
            .FetchAsync([("App.csproj", (IReadOnlyList<PackageRef>)[new("Safe", "1.0.0", 1)])]);
        Assert.Empty(handler.Requests);
        Assert.NotEmpty(result.Failed);
    }
    [Fact]
    public async Task Excessive_archive_entries_are_rejected_without_publishing_cache()
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            for (var n = 0; n < 10001; n++) zip.CreateEntry($"ignored/{n}");
        using var http = new HttpClient(new ResponseHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes.ToArray()) }));
        var result = await new PackageFetcher(root, http, [new("test", "https://feed.test/", null, "https://feed.test/flat/")], new PackageFeedPolicy("https://feed.test,https://feed.test:444"))
            .FetchAsync([("App.csproj", (IReadOnlyList<PackageRef>)[new("Safe", "1.0.0", 1)])]);
        Assert.NotEmpty(result.Failed);
        Assert.False(Directory.Exists(Path.Combine(root, "safe", "1.0.0")));
    }
    [Fact]
    public async Task Credentials_do_not_follow_redirect_to_another_port()
    {
        var authorizations = new List<bool>();
        using var http = new HttpClient(new ResponseHandler(request =>
        {
            authorizations.Add(request.Headers.Authorization is not null);
            if (authorizations.Count == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri("https://feed.test:444/elsewhere");
                return response;
            }
            return new(HttpStatusCode.NotFound);
        }));
        await new PackageFetcher(root, http, [new("test", "https://feed.test/", new("fake", "synthetic-password"), "https://feed.test/flat/")], new PackageFeedPolicy("https://feed.test,https://feed.test:444"))
            .FetchAsync([("App.csproj", (IReadOnlyList<PackageRef>)[new("Safe", "1.0.0", 1)])]);
        Assert.Equal(new[] { true, false }, authorizations);
    }
    [Fact]
    public async Task Unbounded_feed_metadata_is_rejected()
    {
        using var http = new HttpClient(new ResponseHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(new string(' ', 4 * 1024 * 1024 + 1)) }));
        var result = await new PackageFetcher(root, http, [new("test", "https://feed.test/")], new PackageFeedPolicy("https://feed.test"))
            .FetchAsync([("App.csproj", (IReadOnlyList<PackageRef>)[new("Safe", "1.0.0", 1)])]);
        Assert.NotEmpty(result.Failed);
        Assert.False(Directory.Exists(root));
    }
    [Fact]
    public void Repository_cannot_authorize_credential_destination()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "nuget.config"), """
            <configuration><packageSources><clear/><add key="bad" value="https://attacker.test/v3/index.json"/></packageSources>
            <packageSourceCredentials><bad><add key="Username" value="%USER%"/><add key="ClearTextPassword" value="%TOKEN%"/></bad></packageSourceCredentials></configuration>
            """);
        var reads = 0;
        Assert.Throws<InvalidDataException>(() => NuGetConfig.Discover(root, [], _ => { reads++; return "synthetic"; }));
        Assert.Equal(0, reads);
    }
    [Fact]
    public void Credentials_must_come_from_explicit_environment_references()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "nuget.config"), """
            <configuration><packageSources><clear/><add key="feed" value="https://feed.test/index.json"/></packageSources>
            <packageSourceCredentials><feed><add key="Username" value="literal-user"/><add key="ClearTextPassword" value="literal-secret"/></feed></packageSourceCredentials></configuration>
            """);
        var feeds = NuGetConfig.Discover(root, [], _ => throw new InvalidOperationException("No environment reference expected"), new PackageFeedPolicy("https://feed.test"));
        Assert.Null(Assert.Single(feeds).Credentials);
    }
    [Fact]
    public void Operator_configured_nuget_feed_keeps_credentials_out_of_feed_descriptions()
    {
        var settings = OperatorPackageFeedSettings.Load("""
            {"feeds":[{"id":"private","kind":"nuget","name":"Private","url":"https://feed.test/v3/index.json","username":"build","secret":"synthetic-token"}]}
            """);
        var feed = Assert.Single(settings.NuGetFeeds());
        Assert.Equal("https://feed.test/v3/index.json", feed.Url);
        Assert.Equal("build", feed.Credentials!.Username);
        Assert.Equal("synthetic-token", feed.Credentials.Password);
        Assert.DoesNotContain("synthetic-token", feed.ToString(), StringComparison.Ordinal);
    }
    [Fact]
    public async Task Authorized_feed_cannot_redirect_to_unauthorized_origin()
    {
        var calls = 0;
        using var http = new HttpClient(new ResponseHandler(_ =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://internal.test/secret");
            return response;
        }));
        var result = await new PackageFetcher(root, http, [new("test", "https://feed.test/", null, "https://feed.test/flat/")], new PackageFeedPolicy("https://feed.test"))
            .FetchAsync([("App.csproj", (IReadOnlyList<PackageRef>)[new("Safe", "1.0.0", 1)])]);
        Assert.Equal(1, calls);
        Assert.Contains(result.Failed, text => text.Contains("CE_NUGET_ALLOWED_ORIGINS", StringComparison.Ordinal));
    }
    [Theory]
    [InlineData("http://feed.test")]
    [InlineData("https://feed.test/path")]
    [InlineData("https://user:secret@feed.test")]
    public void Origin_authorization_requires_exact_https_origin(string origin) => Assert.Throws<InvalidDataException>(() => new PackageFeedPolicy(origin));

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Requests.Add(request.RequestUri!); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)); }
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
