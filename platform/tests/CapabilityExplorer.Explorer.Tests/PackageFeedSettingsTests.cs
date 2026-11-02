using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class PackageFeedSettingsApiTests
{
    private static async Task WithApi(Func<HttpClient, string, Task> test)
    {
        var directory = Directory.CreateTempSubdirectory("ce-package-feeds").FullName;
        var settingsPath = Path.Combine(directory, "feeds.json");
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("artifacts", Fixtures.ExpectedDirectory);
            builder.UseSetting("packageFeeds", settingsPath);
        });
        using var client = factory.CreateClient();
        try { await test(client, settingsPath); }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public Task Credentials_are_write_only_and_blank_updates_keep_saved_values() => WithApi(async (client, settingsPath) =>
    {
        const string secret = "synthetic-private-feed-token";
        var saved = await client.PutAsJsonAsync("/api/settings/package-feeds", new
        {
            feeds = new object[]
            {
                new { id = "nuget-private", kind = "nuget", name = "Private NuGet", url = "https://packages.example.test/v3/index.json", username = "build", secret },
                new { id = "npm-private", kind = "npm", name = "Private npm", url = "https://packages.example.test/npm/", scope = "@company, @internal", secret },
            },
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var body = await saved.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        Assert.Contains("hasCredential", body, StringComparison.Ordinal);
        Assert.Contains(secret, await File.ReadAllTextAsync(settingsPath), StringComparison.Ordinal);

        var blankUpdate = await client.PutAsJsonAsync("/api/settings/package-feeds", new
        {
            feeds = new[] { new { id = "nuget-private", kind = "nuget", name = "Private NuGet", url = "https://packages.example.test/v3/index.json", username = "build", secret = "", clearCredential = false } },
        });
        Assert.Equal(HttpStatusCode.OK, blankUpdate.StatusCode);
        Assert.Contains(secret, await File.ReadAllTextAsync(settingsPath), StringComparison.Ordinal);

        var fetched = await client.GetFromJsonAsync<JsonElement>("/api/settings/package-feeds");
        Assert.DoesNotContain(secret, fetched.ToString(), StringComparison.Ordinal);
        Assert.True(fetched.GetProperty("feeds")[0].GetProperty("hasCredential").GetBoolean());
    });

    [Theory]
    [InlineData("http://packages.example.test/v3/index.json")]
    [InlineData("https://user:password@packages.example.test/v3/index.json")]
    [InlineData("https://packages.example.test/v3/index.json?token=bad")]
    public Task Insecure_or_credential_bearing_urls_are_rejected(string url) => WithApi(async (client, settingsPath) =>
    {
        var response = await client.PutAsJsonAsync("/api/settings/package-feeds", new
        {
            feeds = new[] { new { id = "private", kind = "nuget", name = "Private", url, username = "build", secret = "synthetic-secret" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(File.Exists(settingsPath));
    });

    [Fact]
    public Task Duplicate_npm_scopes_are_rejected() => WithApi(async (client, settingsPath) =>
    {
        var response = await client.PutAsJsonAsync("/api/settings/package-feeds", new
        {
            feeds = new[]
            {
                new { id = "one", kind = "npm", name = "One", url = "https://packages.example.test/npm/", scope = "@company" },
                new { id = "two", kind = "npm", name = "Two", url = "https://other.example.test/npm/", scope = "@company" },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(File.Exists(settingsPath));
    });
}