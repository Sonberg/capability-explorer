using System.Net;
using CapabilityExplorer.Explorer;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class SecurityBoundaryTests(ExplorerFactory factory) : IClassFixture<ExplorerFactory>
{
    [Theory]
    [InlineData("--output=/tmp/unwanted")]
    [InlineData("HEAD")]
    public void Git_revisions_are_object_ids_not_options_or_revision_expressions(string revision)
    {
        var called = false;
        var git = new GitSource(_ => { called = true; return null; });
        Assert.Null(git.ReadFile("repo", revision, "file.cs"));
        Assert.Null(git.Subject("repo", revision));
        Assert.Null(git.CommitTime("repo", revision));
        Assert.Empty(git.Tags("repo", revision));
        Assert.False(called);
    }
    [Theory]
    [InlineData("../file.cs")]
    [InlineData("/etc/passwd")]
    [InlineData("folder/../../file.cs")]
    public void Git_source_paths_are_relative_blob_paths(string path)
    {
        var called = false;
        var git = new GitSource(_ => { called = true; return null; });
        Assert.Null(git.ReadFile("repo", new string('a', 40), path));
        Assert.False(called);
    }
    [Theory]
    [InlineData("GET", "/api/meta", "rebind.attacker.test", null)]
    [InlineData("POST", "/api/settings/model/test", "localhost", "https://attacker.test")]
    [InlineData("POST", "/api/settings/model/test", "localhost", "null")]
    public async Task Foreign_browser_requests_are_rejected(string method, string path, string host, string? origin)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Host = host;
        if (origin is not null) request.Headers.Add("Origin", origin);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }
}
