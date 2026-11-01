using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;

namespace CapabilityExplorer.Interpretation;

/// <summary>
/// Chat completions with JSON-schema constrained output on an OpenAI-compatible API: a local Ollama
/// (<c>http://localhost:11434/v1</c>) or a Microsoft Foundry deployment. See <see cref="OpenAiApi"/> for URLs and auth.
/// </summary>
public sealed class FoundryChatModel : IInterpretationModel, IChatModel
{
    private readonly HttpClient _http;
    private readonly Uri _url;
    private readonly string _deployment;
    private readonly string? _apiKey;
    private readonly TokenCredential? _credential;
    private readonly string _provider;

    /// <param name="provider">Prefix of <see cref="Id"/> (part of every input hash): "foundry" or "openai".</param>
    public FoundryChatModel(HttpClient http, Uri endpoint, string deployment, string? apiKey, TokenCredential? credential, string provider = "foundry")
    {
        _http = http;
        _url = OpenAiApi.Url(endpoint, "chat/completions");
        _deployment = deployment;
        _apiKey = apiKey;
        _credential = credential;
        _provider = provider;
    }

    public string Id => $"{_provider}:{_deployment}";

    public async Task<ModelResponse> CompleteJsonAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var text = await PostAsync(new JsonObject
        {
            ["model"] = _deployment,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = request.System },
                new JsonObject { ["role"] = "user", ["content"] = request.User }),
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = request.SchemaName,
                    ["strict"] = true,
                    ["schema"] = JsonNode.Parse(request.Schema.GetRawText()),
                },
            },
        }, cancellationToken);
        return Parse(text, root =>
        {
            var content = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
                ?? throw new InterpretationUnavailableException("model response has no content");
            return new ModelResponse(content, Usage(root, "prompt_tokens"), Usage(root, "completion_tokens"));
        });
    }

    public async Task<ChatResult> ChatAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool>? tools = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var body = new JsonObject
        {
            ["model"] = _deployment,
            ["messages"] = new JsonArray(messages.Select(Message).ToArray()),
        };
        if (tools is { Count: > 0 })
        {
            body["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = JsonNode.Parse(t.Parameters.GetRawText()) },
            }).ToArray());
        }

        var text = await PostAsync(body, cancellationToken);
        return Parse(text, root =>
        {
            var message = root.GetProperty("choices")[0].GetProperty("message");
            var content = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            var calls = message.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array
                ? tc.EnumerateArray().Select(call => new ToolCall(
                    call.GetProperty("id").GetString() ?? "",
                    call.GetProperty("function").GetProperty("name").GetString() ?? "",
                    call.GetProperty("function").GetProperty("arguments").GetString() ?? "{}")).ToList()
                : [];
            if (content is null && calls.Count == 0)
            {
                throw new InterpretationUnavailableException("model response has neither content nor tool calls");
            }

            return new ChatResult(content, calls, Usage(root, "prompt_tokens"), Usage(root, "completion_tokens"));
        });
    }

    private static JsonNode Message(ChatMessage m)
    {
        var json = new JsonObject { ["role"] = m.Role, ["content"] = m.Content };
        if (m.ToolCalls is { Count: > 0 })
        {
            json["tool_calls"] = new JsonArray(m.ToolCalls.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments },
            }).ToArray());
        }

        if (m.ToolCallId is not null)
        {
            json["tool_call_id"] = m.ToolCallId;
        }

        return json;
    }

    private static int? Usage(JsonElement root, string name) =>
        root.TryGetProperty("usage", out var u) && u.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static T Parse<T>(string text, Func<JsonElement, T> read)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return read(doc.RootElement);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
        {
            throw new InterpretationUnavailableException("unexpected model response shape", e);
        }
    }

    private async Task<string> PostAsync(JsonObject body, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, _url) { Content = JsonContent.Create(body) };
        await OpenAiApi.AuthorizeAsync(message, _apiKey, _credential, cancellationToken);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, cancellationToken);
        }
        catch (HttpRequestException e)
        {
            throw new InterpretationUnavailableException("model request failed", e);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InterpretationUnavailableException("the model did not answer in time (a model on CPU can be slow: try a smaller model, or use the native Ollama app, which uses the GPU)", e);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InterpretationUnavailableException($"model API returned {(int)response.StatusCode}");
            }
            return text;
        }
    }

}
