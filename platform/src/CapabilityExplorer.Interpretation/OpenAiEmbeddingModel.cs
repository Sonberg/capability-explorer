using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;

namespace CapabilityExplorer.Interpretation;

/// <summary>
/// Embeddings on an OpenAI-compatible API (<c>{base}/embeddings</c>): a local Ollama by default, or Microsoft Foundry.
/// See <see cref="OpenAiApi"/> for URLs and auth.
/// </summary>
public sealed class OpenAiEmbeddingModel
{
    private readonly HttpClient _http;
    private readonly Uri _url;
    private readonly string _deployment;
    private readonly string? _apiKey;
    private readonly TokenCredential? _credential;
    private readonly string _provider;

    public OpenAiEmbeddingModel(HttpClient http, Uri endpoint, string model, string? apiKey, TokenCredential? credential, string provider = "openai")
    {
        _http = http;
        _url = OpenAiApi.Url(endpoint, "embeddings");
        _deployment = model;
        _apiKey = apiKey;
        _credential = credential;
        _provider = provider;
    }

    /// <summary>Stored with every vector: vectors from different models are never compared.</summary>
    public string Id => $"{_provider}:{_deployment}";

    /// <summary>One vector per input, in input order.</summary>
    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var body = new JsonObject { ["model"] = _deployment, ["input"] = new JsonArray(inputs.Select(i => (JsonNode)i).ToArray()) };
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
                var vectors = new float[inputs.Count][];
                var seen = new HashSet<int>();
                foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
                {
                    var index = item.GetProperty("index").GetInt32();
                    if (index < 0 || index >= vectors.Length || !seen.Add(index))
                        throw new InterpretationUnavailableException("model response has invalid embedding indexes");
                    vectors[index] = item.GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle()).ToArray();
                }

                if (vectors.Any(v => v is null || v.Length is < 1 or > 16000 || v.Any(x => !float.IsFinite(x)))
                    || vectors.Any(v => v.Length != vectors[0].Length))
                    throw new InterpretationUnavailableException("model response has invalid embeddings");
                return vectors;
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
            {
                throw new InterpretationUnavailableException("unexpected model response shape", e);
            }
        }
    }

}
