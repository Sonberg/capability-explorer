using System.Text.Json;
using CapabilityExplorer.Explorer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CapabilityExplorer.Tests;

/// <summary>The explorer picks up artifacts written while it runs (`make analyze` in another container).</summary>
public sealed class LiveReloadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ce-live-" + Guid.NewGuid().ToString("N"));

    public LiveReloadTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Factory(string golden, string analysed, string workspace) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("artifacts", $"{golden},{analysed}");
            builder.UseSetting("repos", Path.Combine(Fixtures.RepositoryRoot, "fixtures", ".repos"));
            builder.UseSetting("capabilities", Path.Combine(workspace, "capabilities.json"));
            builder.UseSetting("workspace", workspace);
        }
    }

    private static async Task<JsonElement> Get(HttpClient client, string path) =>
        JsonDocument.Parse(await client.GetStringAsync(new Uri(path, UriKind.Relative))).RootElement.Clone();

    [Fact]
    public async Task New_artifacts_raise_the_generation_and_replace_the_same_unit()
    {
        // Start with only the forecast golden set; the analysed directory does not exist yet.
        var golden = Path.Combine(_dir, "golden");
        var analysed = Path.Combine(_dir, "analysed");
        Directory.CreateDirectory(golden);
        var source = Path.Combine(Fixtures.ExpectedDirectory, "forecast-cs", "v1-base", "Forecast.Api.jsonl");
        File.Copy(source, Path.Combine(golden, "Forecast.Api.jsonl"));

        using var factory = new Factory(golden, analysed, Path.Combine(_dir, "workspace"));
        using var client = factory.CreateClient();
        var state = factory.Services.GetRequiredService<ExplorerState>();

        var before = await Get(client, "/api/meta");
        Assert.Equal(0, before.GetProperty("generation").GetInt64());
        Assert.Equal(1, before.GetProperty("artifacts").GetInt32());
        Assert.False(state.Refresh());

        // The same unit at the same commit, written later into the analysed directory, replaces the golden one.
        Directory.CreateDirectory(Path.Combine(analysed, "forecast-cs"));
        File.Copy(source, Path.Combine(analysed, "forecast-cs", "Forecast.Api.jsonl"));
        // Another revision is added alongside.
        File.Copy(Path.Combine(Fixtures.ExpectedDirectory, "forecast-cs", "v2-contract", "Forecast.Api.jsonl"), Path.Combine(analysed, "forecast-cs", "v2.jsonl"));
        state.Refresh(); // the background poll may have got there first

        var after = await Get(client, "/api/meta");
        Assert.True((await Get(client, "/api/generation")).GetProperty("generation").GetInt64() >= 1);
        Assert.Equal(2, after.GetProperty("artifacts").GetInt32());
        Assert.Equal(2, after.GetProperty("revisions").GetArrayLength());
        var labels = (await Get(client, "/api/artifacts")).EnumerateArray().Select(a => a.GetProperty("label").GetString()).ToList();
        Assert.Contains("forecast-cs/Forecast.Api.jsonl", labels);
        Assert.DoesNotContain("Forecast.Api.jsonl", labels);
    }
}
