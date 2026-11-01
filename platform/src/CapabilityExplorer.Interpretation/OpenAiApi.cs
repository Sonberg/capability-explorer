using System.Net.Http.Headers;
using Azure.Core;

namespace CapabilityExplorer.Interpretation;

/// <summary>
/// Shared by the chat and embedding clients: any OpenAI-compatible API (Ollama, Microsoft Foundry, OpenAI).
/// </summary>
internal static class OpenAiApi
{
    // Injected HttpClient instances are trusted caller dependencies and must also disable automatic redirects.
    public static HttpClient CreateClient(TimeSpan? timeout = null) => new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = timeout ?? TimeSpan.FromSeconds(180), MaxResponseContentBufferSize = 8 * 1024 * 1024 };

    private const string TokenScope = "https://cognitiveservices.azure.com/.default";

    /// <summary>
    /// The endpoint is the API base (http://localhost:11434/v1, https://res.services.ai.azure.com/openai/v1).
    /// A bare host with no path is a Foundry resource, whose v1 API lives under /openai/v1.
    /// </summary>
    public static Uri Url(Uri endpoint, string resource)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0
            || !(endpoint.Scheme == Uri.UriSchemeHttps || (endpoint.Scheme == Uri.UriSchemeHttp
                && (endpoint.IsLoopback || endpoint.Host == "host.docker.internal" || !endpoint.Host.Contains('.', StringComparison.Ordinal)))))
            throw new InvalidOperationException("Model endpoint must be HTTPS (HTTP only for local servers), without user information, query or fragment");
        var root = endpoint.AbsoluteUri.TrimEnd('/');
        return new Uri(endpoint.AbsolutePath is "" or "/" ? $"{root}/openai/v1/{resource}" : $"{root}/{resource}");
    }

    /// <summary>API key header, else an Entra ID token, else nothing (a local server needs no auth).</summary>
    public static async Task AuthorizeAsync(HttpRequestMessage message, string? apiKey, TokenCredential? credential, CancellationToken cancellationToken)
    {
        if ((apiKey is not null || credential is not null) && message.RequestUri!.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Model credentials require HTTPS");
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
