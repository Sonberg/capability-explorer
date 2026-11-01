using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;

namespace CapabilityExplorer.Interpretation;

/// <summary>
/// Chat completions with JSON-schema constrained output on an OpenAI-compatible API: a local Ollama
/// (<c>http://localhost:11434/v1</c>) or a Microsoft Foundry deployment. See <see cref="OpenAiApi"/> for URLs and auth.
/// </summary>
public sealed class FoundryChatModel : IInterpretationModel
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
        var body = new JsonObject
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
        };

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

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InterpretationUnavailableException($"model API returned {(int)response.StatusCode}");
            }

            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                var content = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
                    ?? throw new InterpretationUnavailableException("model response has no content");
                int? Usage(string name) => root.TryGetProperty("usage", out var u) && u.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
                return new ModelResponse(content, Usage("prompt_tokens"), Usage("completion_tokens"));
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
            {
                throw new InterpretationUnavailableException("unexpected model response shape", e);
            }
        }
    }

}
