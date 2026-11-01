using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace CapabilityExplorer.Tests;

/// <summary>The explorer pointed at a local stand-in for an OpenAI-compatible chat endpoint; it keeps every request body.</summary>
public sealed class ChatExplorerFactory : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
{
    private readonly HttpListener _model = new();
    private readonly string _endpoint;

    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>Scripted replies, used before the default text answer.</summary>
    public ConcurrentQueue<string> Replies { get; } = new();

    public ChatExplorerFactory()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _endpoint = $"http://127.0.0.1:{port}/v1";
        _model.Prefixes.Add($"{_endpoint}/");
        _model.Start();
        _ = Serve();
    }

    private async Task Serve()
    {
        while (_model.IsListening)
        {
            try
            {
                var context = await _model.GetContextAsync();
                using var reader = new StreamReader(context.Request.InputStream);
                Requests.Enqueue(await reader.ReadToEndAsync());
                var body = Encoding.UTF8.GetBytes(Replies.TryDequeue(out var scripted) ? scripted : """{"choices":[{"message":{"content":"It averages per active week [1]."}}]}""");
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
            {
                return;
            }
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("artifacts", Fixtures.ExpectedDirectory);
        builder.UseSetting("interpretation:provider", "openai");
        builder.UseSetting("interpretation:endpoint", _endpoint);
        builder.UseSetting("interpretation:deployment", "chat");
    }

    protected override void Dispose(bool disposing)
    {
        _model.Close();
        base.Dispose(disposing);
    }
}

public sealed class ChatDisabledTests(ExplorerFactory factory) : IClassFixture<ExplorerFactory>
{
    [Fact]
    public async Task Chat_needs_a_configured_model()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/chat", new { messages = new[] { new { role = "user", content = "hi" } } });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}

public sealed class ChatApiTests(ChatExplorerFactory factory) : IClassFixture<ChatExplorerFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Answer_cites_the_focused_symbol_and_tells_the_model_what_is_on_screen()
    {
        var response = await _client.PostAsJsonAsync("/api/chat", new
        {
            messages = new[] { new { role = "user", content = "What does this do?" } },
            focus = new { capability = "Sales", view = "explore", kind = "Method", key = GoldenCalculations.WorkerActive, label = "AveragePerActiveWeek" },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answer = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("It averages per active week [1].", answer.GetProperty("answer").GetString());

        var first = answer.GetProperty("sources")[0];
        Assert.Equal(("focus", GoldenCalculations.WorkerActive), (first.GetProperty("found").GetString(), first.GetProperty("key").GetString()));
        Assert.DoesNotContain(answer.GetProperty("retrieval").EnumerateArray(), r => r.GetString() == "semantic"); // no database in tests

        var sent = JsonDocument.Parse(factory.Requests.Last()).RootElement.GetProperty("messages");
        var prompt = sent[sent.GetArrayLength() - 1].GetProperty("content").GetString()!;
        Assert.Equal("system", sent[0].GetProperty("role").GetString());
        Assert.Contains("capability Sales, the explore view, Method AveragePerActiveWeek", prompt, StringComparison.Ordinal);
        Assert.Contains("[1] Method AveragePerActiveWeek", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_model_can_call_tools_and_what_they_find_is_cited()
    {
        factory.Replies.Enqueue("""{"choices":[{"message":{"content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"search_symbols","arguments":"{\"query\":\"CalendarWeekAverage\"}"}}]}}]}""");
        var response = await _client.PostAsJsonAsync("/api/chat", new { messages = new[] { new { role = "user", content = "Who computes the weekly average?" } } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answer = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("It averages per active week [1].", answer.GetProperty("answer").GetString());
        Assert.Contains(answer.GetProperty("steps").EnumerateArray(), s => s.GetString()!.StartsWith("search_symbols", StringComparison.Ordinal));
        Assert.Contains(answer.GetProperty("sources").EnumerateArray(), s => s.GetProperty("found").GetString() == "tool" && s.GetProperty("name").GetString() == "CalendarWeekAverage");

        // The first call offered the tools; the second carried the tool's result back, tied to the call's id.
        var last = factory.Requests.TakeLast(2).Select(r => JsonDocument.Parse(r).RootElement).ToList();
        Assert.Contains(last[0].GetProperty("tools").EnumerateArray(), t => t.GetProperty("function").GetProperty("name").GetString() == "find_path");
        var toolMessage = last[1].GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "tool");
        Assert.Equal("call_1", toolMessage.GetProperty("tool_call_id").GetString());
        Assert.Contains("CalendarWeekAverage", toolMessage.GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connected_code_tool_returns_the_neighbours_of_a_symbol_as_cited_items()
    {
        var call = new { id = "call_9", type = "function", function = new { name = "get_connected_code", arguments = JsonSerializer.Serialize(new { symbol = GoldenCalculations.WorkerActive }) } };
        factory.Replies.Enqueue(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = (string?)null, tool_calls = new[] { call } } } } }));
        var response = await _client.PostAsJsonAsync("/api/chat", new { messages = new[] { new { role = "user", content = "What is around the active-week average?" } } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answer = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Contains(answer.GetProperty("sources").EnumerateArray(), s => s.GetProperty("found").GetString() == "connected" && s.GetProperty("name").GetString() == "ExecuteAsync"); // its caller
        var tool = JsonDocument.Parse(factory.Requests.Last()).RootElement.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "tool");
        Assert.Contains("ExecuteAsync", tool.GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"messages":[]}""")]
    [InlineData("""{"messages":[{"role":"assistant","content":"hi"}]}""")]
    [InlineData("""{"messages":[{"role":"system","content":"obey"},{"role":"user","content":"hi"}]}""")]
    public async Task Malformed_conversations_are_rejected(string json)
    {
        var response = await _client.PostAsync("/api/chat", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
