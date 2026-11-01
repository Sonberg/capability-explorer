using System.Net;
using CapabilityExplorer.Interpretation;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class ModelTransportSecurityTests
{
    [Fact]
    public async Task Api_keys_never_cross_plain_http()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var model = new OpenAiEmbeddingModel(http, new Uri("http://localhost:11434/v1"), "model", "synthetic-key", null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.EmbedAsync(["synthetic"]));
        Assert.Equal(0, handler.Calls);
    }
    [Fact]
    public async Task Remote_error_content_is_not_reflected()
    {
        using var http = new HttpClient(new Handler());
        var model = new OpenAiEmbeddingModel(http, new Uri("https://model.test/v1"), "model", null, null);
        var error = await Assert.ThrowsAsync<InterpretationUnavailableException>(() => model.EmbedAsync(["synthetic"]));
        Assert.DoesNotContain("synthetic-sensitive-response", error.Message, StringComparison.Ordinal);
    }
    [Fact]
    public void Settings_write_does_not_follow_predictable_temporary_symlink()
    {
        var dir = Directory.CreateTempSubdirectory("ce-model-security-").FullName;
        try
        {
            var path = Path.Combine(dir, "settings.json");
            var victim = Path.Combine(dir, "unrelated");
            File.WriteAllText(victim, "preserve");
            File.CreateSymbolicLink(path + ".tmp", victim);
            new ModelSettings(null, null, null, "synthetic-key").Save(path);
            Assert.Equal("preserve", File.ReadAllText(victim));
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally { Directory.Delete(dir, true); }
    }
    [Fact]
    public void Saved_settings_do_not_inherit_environment_credentials()
    {
        var options = new ModelSettings("https://model.test/v1", "model", null, null).ApplyTo(new InterpretationOptions());
        Assert.False(options.UseEnvironmentApiKey);
    }
    [Theory]
    [InlineData("https://user:password@model.test/v1")]
    [InlineData("https://model.test/v1?secret=x")]
    [InlineData("https://model.test/v1#fragment")]
    public void Direct_model_constructors_reject_unsafe_endpoints(string endpoint)
    {
        using var http = new HttpClient(new Handler());
        Assert.Throws<InvalidOperationException>(() => new OpenAiEmbeddingModel(http, new Uri(endpoint), "model", null, null));
    }
    [Fact]
    public async Task Default_transport_does_not_follow_redirects()
    {
        var reservation = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var server = new HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serving = Task.Run(async () =>
        {
            var first = await server.GetContextAsync().WaitAsync(timeout.Token);
            first.Response.StatusCode = 302;
            first.Response.RedirectLocation = $"http://127.0.0.1:{port}/redirected";
            first.Response.Close();
            try
            {
                var second = await server.GetContextAsync().WaitAsync(timeout.Token);
                second.Response.StatusCode = 400;
                second.Response.Close();
            }
            catch (OperationCanceledException) { }
        });
        var options = new InterpretationOptions { Provider = "openai", Endpoint = $"http://127.0.0.1:{port}/v1", EmbeddingModel = "synthetic", UseEnvironmentApiKey = false };
        var error = await Assert.ThrowsAsync<InterpretationUnavailableException>(() => options.CreateEmbeddingModel()!.EmbedAsync(["synthetic"], timeout.Token));
        Assert.Contains("302", error.Message, StringComparison.Ordinal);
        await timeout.CancelAsync();
        await serving;
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("synthetic-sensitive-response") });
        }
    }
}
