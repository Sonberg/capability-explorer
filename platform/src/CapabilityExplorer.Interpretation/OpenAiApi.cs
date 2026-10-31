using System.Net.Http.Headers;
using Azure.Core;

namespace CapabilityExplorer.Interpretation;

/// <summary>
/// Shared by the chat and embedding clients: any OpenAI-compatible API (Ollama, Microsoft Foundry, OpenAI).
/// </summary>
internal static class OpenAiApi
{
    private const string TokenScope = "https://cognitiveservices.azure.com/.default";

    /// <summary>
    /// The endpoint is the API base (http://localhost:11434/v1, https://res.services.ai.azure.com/openai/v1).
    /// A bare host with no path is a Foundry resource, whose v1 API lives under /openai/v1.
    /// </summary>
    public static Uri Url(Uri endpoint, string resource)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var root = endpoint.AbsoluteUri.TrimEnd('/');
        return new Uri(endpoint.AbsolutePath is "" or "/" ? $"{root}/openai/v1/{resource}" : $"{root}/{resource}");
    }

    /// <summary>API key header, else an Entra ID token, else nothing (a local server needs no auth).</summary>
    public static async Task AuthorizeAsync(HttpRequestMessage message, string? apiKey, TokenCredential? credential, CancellationToken cancellationToken)
    {
        if (apiKey is not null && IsAzure(message.RequestUri!))
        {
            message.Headers.Add("api-key", apiKey);
        }
        else if (apiKey is not null)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey); // OpenAI and most compatible servers
        }
        else if (credential is not null)
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext([TokenScope]), cancellationToken);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }
    }

    /// <summary>Entra ID applies to Azure hosts only; elsewhere no key means no auth.</summary>
    public static bool IsAzure(Uri endpoint) => endpoint.Host.EndsWith(".azure.com", StringComparison.OrdinalIgnoreCase);
}
