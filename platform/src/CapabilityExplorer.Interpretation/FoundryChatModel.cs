using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;

namespace CapabilityExplorer.Interpretation;

/// <summary>
/// Microsoft Foundry deployment through the OpenAI-compatible v1 chat completions endpoint
/// (<c>{endpoint}/openai/v1/chat/completions</c>), with JSON-schema constrained output.
/// Authenticates with an API key when one is supplied, otherwise with an Entra ID token.
/// </summary>
public sealed class FoundryChatModel : IInterpretationModel
{
    private const string TokenScope = "https://cognitiveservices.azure.com/.default";
    private readonly HttpClient _http;
    private readonly Uri _url;
    private readonly string _deployment;
    private readonly string? _apiKey;
    private readonly TokenCredential? _credential;

    public FoundryChatModel(HttpClient http, Uri endpoint, string deployment, string? apiKey, TokenCredential? credential)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (apiKey is null && credential is null)
        {
            throw new ArgumentException("an API key or a token credential is required");
        }

        _http = http;
        _url = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/openai/v1/chat/completions");
        _deployment = deployment;
        _apiKey = apiKey;
        _credential = credential;
    }

    public string Id => $"foundry:{_deployment}";

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
        if (_apiKey is not null)
        {
            message.Headers.Add("api-key", _apiKey);
        }
        else
        {
            var token = await _credential!.GetTokenAsync(new TokenRequestContext([TokenScope]), cancellationToken);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, cancellationToken);
        }
        catch (HttpRequestException e)
        {
            throw new InterpretationUnavailableException($"Foundry request failed: {e.Message}", e);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InterpretationUnavailableException($"Foundry returned {(int)response.StatusCode}: {Truncate(text)}");
            }

            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                var content = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
                    ?? throw new InterpretationUnavailableException("Foundry response has no content");
                int? Usage(string name) => root.TryGetProperty("usage", out var u) && u.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
                return new ModelResponse(content, Usage("prompt_tokens"), Usage("completion_tokens"));
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
            {
                throw new InterpretationUnavailableException($"unexpected Foundry response shape: {Truncate(text)}", e);
            }
        }
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
