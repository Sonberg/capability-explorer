using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;

namespace CapabilityExplorer.Interpretation;

/// <summary>
/// Microsoft Foundry embeddings deployment through the OpenAI-compatible v1 endpoint (<c>{endpoint}/openai/v1/embeddings</c>).
/// Same authentication as <see cref="FoundryChatModel"/>: an API key when supplied, otherwise an Entra ID token.
/// </summary>
public sealed class FoundryEmbeddingModel
{
    private const string TokenScope = "https://cognitiveservices.azure.com/.default";
    private readonly HttpClient _http;
    private readonly Uri _url;
    private readonly string _deployment;
    private readonly string? _apiKey;
    private readonly TokenCredential? _credential;

    public FoundryEmbeddingModel(HttpClient http, Uri endpoint, string deployment, string? apiKey, TokenCredential? credential)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (apiKey is null && credential is null)
        {
            throw new ArgumentException("an API key or a token credential is required");
        }

        _http = http;
        _url = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/openai/v1/embeddings");
        _deployment = deployment;
        _apiKey = apiKey;
        _credential = credential;
    }

    /// <summary>Stored with every vector: vectors from different models are never compared.</summary>
    public string Id => $"foundry:{_deployment}";

    /// <summary>One vector per input, in input order.</summary>
    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var body = new JsonObject { ["model"] = _deployment, ["input"] = new JsonArray(inputs.Select(i => (JsonNode)i).ToArray()) };
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
                var vectors = new float[inputs.Count][];
                foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
                {
                    vectors[item.GetProperty("index").GetInt32()] = item.GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle()).ToArray();
                }

                return vectors.Any(v => v is null) ? throw new InterpretationUnavailableException("Foundry response is missing embeddings") : vectors;
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
            {
                throw new InterpretationUnavailableException($"unexpected Foundry response shape: {Truncate(text)}", e);
            }
        }
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
