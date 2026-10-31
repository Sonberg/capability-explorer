using System.Text.Json;
using System.Text.RegularExpressions;

namespace CapabilityExplorer.Binding;

/// <summary>One human-maintained mapping from an unbound hint to the concrete resource it names.</summary>
/// <param name="Hint">The full hint segment, e.g. <c>@unbound:pricing-node/@fixture/pricing:env=FORECAST_API_URL</c>.</param>
/// <param name="Target">What replaces that segment in a key: a service ref (<c>forecast-cs/Forecast.Api</c>), a namespace or a store.</param>
/// <param name="Note">Why the human believes this (shown next to the link).</param>
public sealed record BindingEntry(string Hint, string Target, string? Note);

/// <summary>
/// A service map (<c>bindings.json</c>): <c>{"version":1,"bindings":[{"hint":"@unbound:…","target":"…","note":"…"}]}</c>.
/// It is the only input besides the artifacts, so binding stays reproducible.
/// </summary>
public sealed partial class BindingFile
{
    public const int Version = 1;

    private BindingFile(IReadOnlyList<BindingEntry> entries, string sha256)
    {
        Entries = entries;
        Sha256 = sha256;
    }

    public IReadOnlyList<BindingEntry> Entries { get; }

    /// <summary>Hash of the file's bytes; part of a binding snapshot's identity.</summary>
    public string Sha256 { get; }

    public static BindingFile Empty { get; } = new([], Hash(""));

    [GeneratedRegex(@"^@unbound:[a-z0-9][a-z0-9._-]*/[^:|]+:[a-z]+=[^|]+$", RegexOptions.CultureInvariant)]
    private static partial Regex HintPattern();

    public static BindingFile Load(string path) => Parse(File.ReadAllText(path));

    public static BindingFile Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException e)
        {
            throw new FormatException($"bindings file is not valid JSON: {e.Message}", e);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("bindings", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("bindings file must be an object with a \"bindings\" array");
            }

            if (root.TryGetProperty("version", out var v) && !(v.ValueKind == JsonValueKind.Number && v.GetInt32() == Version))
            {
                throw new FormatException($"unsupported bindings version {v.GetRawText()} (expected {Version})");
            }

            var entries = new List<BindingEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in list.EnumerateArray())
            {
                var hint = Text(item, "hint") ?? throw new FormatException("binding without a \"hint\"");
                var target = Text(item, "target") ?? throw new FormatException($"binding for '{hint}' has no \"target\"");
                if (!HintPattern().IsMatch(hint))
                {
                    throw new FormatException($"'{hint}' is not an unbound hint (expected @unbound:<repo>/<unit>:<kind>=<value>)");
                }

                if (target.Length == 0 || target.Contains('|', StringComparison.Ordinal) || target.StartsWith("@unbound:", StringComparison.Ordinal))
                {
                    throw new FormatException($"target '{target}' for '{hint}' must be a concrete name without '|'");
                }

                if (!seen.Add(hint))
                {
                    throw new FormatException($"'{hint}' is bound twice");
                }

                entries.Add(new BindingEntry(hint, target, Text(item, "note")));
            }

            return new BindingFile(entries, Hash(json));
        }
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static string Hash(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
}
