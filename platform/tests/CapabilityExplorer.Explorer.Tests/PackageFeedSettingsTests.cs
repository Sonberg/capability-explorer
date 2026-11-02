using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CapabilityExplorer.Explorer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class PackageFeedSettingsApiTests
{
    private static async Task WithApi(Func<HttpClient, string, Task> test)
    {
        var directory = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "test-state", Guid.NewGuid().ToString("N"))).FullName;
        var catalogPath = Path.Combine(directory, "catalog.json");
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("artifacts", Fixtures.ExpectedDirectory);
            builder.UseSetting("packageFeedCatalog", catalogPath);
        });
        using var client = factory.CreateClient();
        try { await test(client, catalogPath); }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public Task Public_catalogue_is_credential_free_and_reloaded_without_restart() => WithApi(async (client, path) =>
    {
        await File.WriteAllTextAsync(path, """
            {"version":1,"feeds":[{"id":"private","kind":"nuget","name":"Private NuGet","url":"https://packages.example.test/v3/index.json","username":"build","hasCredential":true}]}
            """);
        var status = await client.GetFromJsonAsync<JsonElement>("/api/settings/package-feeds");
        Assert.False(status.TryGetProperty("problem", out _));
        Assert.True(status.GetProperty("feeds")[0].GetProperty("hasCredential").GetBoolean());
        Assert.False(status.GetProperty("feeds")[0].TryGetProperty("secret", out _));
        await File.WriteAllTextAsync(path, """{"version":1,"feeds":[]}""");
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/api/settings/package-feeds")).GetProperty("feeds").EnumerateArray());
    });

    [Theory]
    [InlineData("""{"feeds":[{"secret":"synthetic-private-feed-token"}]}""")]
    [InlineData("""{"feeds":[{"clearCredential":true}]}""")]
    [InlineData("""{"feeds":[]}""")]
    public Task Every_legacy_update_gets_explicit_migration_response_without_persisting(string input) => WithApi(async (client, path) =>
    {
        var response = await client.PutAsync(new Uri("/api/settings/package-feeds", UriKind.Relative),
            new StringContent(input, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("host", body, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-private-feed-token", body, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
    });

    [Theory]
    [InlineData("""{"version":1,"feeds":[{"id":"a","kind":"nuget","name":"A","url":"https://example.test","secret":"synthetic-secret","hasCredential":true}]}""")]
    [InlineData("""{"version":1,"feeds":[{"id":"a","kind":"nuget","name":"A","url":"https://example.test?token=synthetic-secret","hasCredential":true}]}""")]
    [InlineData("""{"version":1,"feeds":[{"id":"a","kind":"other","name":"A","url":"https://example.test","hasCredential":false}]}""")]
    [InlineData("""{"version":1,"feeds":[{"id":"a","kind":"nuget","name":"A","url":"https://example.test"}]}""")]
    [InlineData("""{"version":1,"version":1,"feeds":[]}""")]
    [InlineData("""{"version":2,"feeds":[]}""")]
    [InlineData("""{"version":1,"feeds":null}""")]
    [InlineData("""{"version":1}""")]
    [InlineData("{broken")]
    public Task Corruption_and_secret_bearing_catalogues_are_not_silent_empty_success(string content) => WithApi(async (client, path) =>
    {
        await File.WriteAllTextAsync(path, content);
        var status = await client.GetFromJsonAsync<JsonElement>("/api/settings/package-feeds");
        Assert.NotEmpty(status.GetProperty("problem").GetString()!);
        Assert.Empty(status.GetProperty("feeds").EnumerateArray());
        Assert.DoesNotContain("synthetic-secret", status.ToString(), StringComparison.Ordinal);
    });

    [Fact]
    public Task Oversize_catalogue_is_bounded() => WithApi(async (client, path) =>
    {
        await File.WriteAllTextAsync(path, new string(' ', 128 * 1024 + 1));
        var status = await client.GetFromJsonAsync<JsonElement>("/api/settings/package-feeds");
        Assert.Contains("size limit", status.GetProperty("problem").GetString(), StringComparison.Ordinal);
    });

    [Fact]
    public Task Legacy_secret_file_is_never_opened_and_catalogue_symlinks_are_rejected() => WithApi(async (client, path) =>
    {
        var legacy = Path.Combine(Path.GetDirectoryName(path)!, "settings.json");
        await File.WriteAllTextAsync(legacy, """{"feeds":[{"secret":"synthetic-secret"}]}""");
        var status = new PackageFeedSettingsStore(legacy).Status();
        Assert.NotNull(status.Problem);
        Assert.Empty(status.Feeds);
        File.CreateSymbolicLink(path, legacy);
        var linked = await client.GetFromJsonAsync<JsonElement>("/api/settings/package-feeds");
        Assert.Contains("symbolic", linked.GetProperty("problem").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-secret", linked.ToString(), StringComparison.Ordinal);
    });
}
