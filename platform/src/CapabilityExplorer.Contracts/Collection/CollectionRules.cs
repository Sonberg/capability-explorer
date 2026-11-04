using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CapabilityExplorer.Contracts.Collection;

internal static partial class CollectionRules
{
    public const string PlanSchema = "ce.collection.plan.v1";
    public const string BundleSchema = "ce.collection.v1";
    public const int MaxPlanBytes = 1_048_576;
    public const int MaxInputBytes = 16_777_216;
    public const long MaxTotalBytes = 134_217_728;
    public const int MaxInputs = 128;
    public const int MaxRepositories = 32;

    private static readonly HashSet<string> Profiles = new(StringComparer.Ordinal)
    {
        "ce.v1", "ce.models.v1", "ce.candidates.v1",
    };

    private static readonly HashSet<string> ContentKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "content", "rawXml", "xml", "source", "sourceText", "rawSource", "expression",
        "expressions", "code", "snippet", "sourceCode", "rawPredicate", "literals", "symbols",
    };

    private static readonly HashSet<string> ForbiddenKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "token", "secret", "authorization", "accessToken", "refreshToken",
        "author", "authorEmail", "committer", "committerEmail", "gitAuthor",
    };

    public static bool IsSha256(string? value) => value is not null && Sha256Pattern().IsMatch(value);
    public static bool IsCommitSha(string? value) => value is not null && CommitPattern().IsMatch(value);

    public static bool IsId(string? value) => value is not null && IdentifierPattern().IsMatch(value);

    public static bool IsRepositoryId(string? value) =>
        value is not null && RepositoryIdentifierPattern().IsMatch(value);

    public static bool IsLogicalPath(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 1024 || value.StartsWith('/') || value.Contains('\\') ||
            value.Contains(':') || value.Any(char.IsControl))
        {
            return false;
        }

        return value.Split('/').All(segment => segment.Length > 0 && segment is not "." and not "..");
    }

    public static bool IsProfile(string? profile) => profile is not null && Profiles.Contains(profile);

    public static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string Digest(string value) => Digest(Encoding.UTF8.GetBytes(value));

    public static async Task<byte[]> ReadBoundedAsync(
        string path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is < 1 || stream.Length > maximumBytes)
        {
            throw new IOException("file size is outside bounds");
        }

        using var buffer = new MemoryStream((int)Math.Min(stream.Length, maximumBytes));
        var chunk = new byte[81920];
        while (true)
        {
            var remaining = maximumBytes - buffer.Length;
            var requested = (int)Math.Min(chunk.Length, remaining + 1);
            var read = await stream.ReadAsync(chunk.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maximumBytes)
            {
                throw new IOException("file size is outside bounds");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    public static bool HasSensitiveOrForbiddenData(JsonElement value, out bool hasContent, out bool hasForbidden)
    {
        var containsContent = false;
        var containsForbidden = false;
        Visit(value);
        hasContent = containsContent;
        hasForbidden = containsForbidden;
        return containsContent || containsForbidden;

        void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (ContentKeys.Contains(property.Name) && property.Value.ValueKind is not JsonValueKind.Null)
                    {
                        containsContent = true;
                    }

                    if (ForbiddenKeys.Contains(property.Name) && property.Value.ValueKind is not JsonValueKind.Null)
                    {
                        containsForbidden = true;
                    }

                    Visit(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in element.EnumerateArray())
                {
                    Visit(child);
                }
            }
        }
    }

    public static IReadOnlyList<string> ValidateProfile(
        string profile,
        ReadOnlySpan<byte> bytes,
        string repositoryId,
        string commitSha,
        string unitPath,
        string? contentProfile,
        IReadOnlyCollection<string> permittedContentProfiles)
    {
        var problems = new List<string>();
        string json;
        try
        {
            json = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return ["artifact is not valid UTF-8"];
        }

        if (profile == "ce.v1")
        {
            var report = ArtifactValidator.Validate(json);
            if (!report.IsValid)
            {
                problems.AddRange(report.Problems.Take(8).Select(p => $"ce.v1 {p}"));
            }
            else if (report.Artifact!.Header.RepositoryId != repositoryId ||
                report.Artifact.Header.CommitSha != commitSha ||
                report.Artifact.Header.Unit.Path != unitPath)
            {
                problems.Add("ce.v1 header identity does not match the collection input");
            }
        }
        else
        {
            ValidateJsonProfile(json, profile, repositoryId, commitSha, unitPath, problems);
        }

        var hasSensitiveContent = false;
        var hasForbiddenData = false;
        try
        {
            if (profile == "ce.v1")
            {
                foreach (var line in json.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
                    _ = HasSensitiveOrForbiddenData(document.RootElement, out var lineContent, out var lineForbidden);
                    hasSensitiveContent |= lineContent;
                    hasForbiddenData |= lineForbidden;
                }
            }
            else
            {
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
                _ = HasSensitiveOrForbiddenData(document.RootElement, out hasSensitiveContent, out hasForbiddenData);
                if (document.RootElement.ValueKind == JsonValueKind.Object && profile == "ce.candidates.v1" &&
                    document.RootElement.TryGetProperty("candidates", out var candidates) &&
                    candidates.ValueKind == JsonValueKind.Array && candidates.GetArrayLength() > 0)
                {
                    hasSensitiveContent = true;
                }

                if (document.RootElement.ValueKind == JsonValueKind.Object && profile == "ce.models.v1" &&
                    document.RootElement.TryGetProperty("contentProfile", out var modelContentProfile) &&
                    modelContentProfile.ValueKind == JsonValueKind.String &&
                    modelContentProfile.GetString() == "model-content")
                {
                    hasSensitiveContent = true;
                }
            }
        }
        catch (JsonException)
        {
            problems.Add("artifact is not valid JSON");
        }

        if (hasForbiddenData)
        {
            problems.Add("credential or personal Git author data is not exportable");
        }

        if (hasSensitiveContent && (contentProfile is null || !IsId(contentProfile) ||
            !permittedContentProfiles.Contains(contentProfile, StringComparer.Ordinal)))
        {
            problems.Add("content-bearing artifact requires an explicitly permitted contentProfile");
        }

        return problems;
    }

    private static void ValidateJsonProfile(
        string json,
        string profile,
        string repositoryId,
        string commitSha,
        string unitPath,
        List<string> problems)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                problems.Add("profile root must be a JSON object");
                return;
            }

            RequireString(root, "schema", profile, problems);
            RequireString(root, "repositoryId", repositoryId, problems);
            RequireString(root, "commitSha", commitSha, problems);
            RequireString(root, "unitPath", unitPath, problems);
            var allowedProperties = profile == "ce.models.v1"
                ? new HashSet<string>(["schema", "repositoryId", "commitSha", "unitPath", "sourceSha256",
                    "contentProfile", "coverage", "diagnostics", "elements", "relationships"], StringComparer.Ordinal)
                : new HashSet<string>(["schema", "recipe", "repositoryId", "commitSha", "unitPath",
                    "sourceSha256", "coverage", "diagnostics", "candidates"], StringComparer.Ordinal);
            if (root.EnumerateObject().Any(property => !allowedProperties.Contains(property.Name)))
            {
                problems.Add("profile contains an unknown top-level property");
            }

            if (!root.TryGetProperty("sourceSha256", out var sourceHash) ||
                sourceHash.ValueKind != JsonValueKind.String || !IsSha256(sourceHash.GetString()))
            {
                problems.Add("sourceSha256 must be a lowercase SHA-256 digest");
            }

            if (!root.TryGetProperty("coverage", out var coverage) || coverage.ValueKind != JsonValueKind.Object ||
                !coverage.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String ||
                status.GetString() is not ("full" or "partial"))
            {
                problems.Add("coverage.status must be full or partial");
            }

            if (profile == "ce.models.v1")
            {
                RequireString(root, "contentProfile", ["structural", "model-content"], problems);
                if (coverage.ValueKind == JsonValueKind.Object)
                {
                    if (coverage.EnumerateObject().Any(p => p.Name is not ("status" or "supported" or "limitations")))
                    {
                        problems.Add("coverage contains an unknown top-level property");
                    }

                    if (!coverage.TryGetProperty("supported", out var supported) || supported.ValueKind != JsonValueKind.Array ||
                        !coverage.TryGetProperty("limitations", out var limitations) || limitations.ValueKind != JsonValueKind.Array)
                    {
                        problems.Add("coverage requires supported and limitations arrays");
                    }
                }
            }
            else
            {
                RequireString(root, "recipe", "roslyn.scalar-guards.v1", problems);
                if (coverage.ValueKind == JsonValueKind.Object)
                {
                    if (!coverage.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.String ||
                        scope.GetString() != "single-file-intra-method" ||
                        !coverage.TryGetProperty("supportedMethods", out var supported) || supported.ValueKind != JsonValueKind.Array ||
                        !coverage.TryGetProperty("unsupportedMethods", out var unsupported) || unsupported.ValueKind != JsonValueKind.Array ||
                        coverage.EnumerateObject().Any(p => p.Name is not ("status" or "scope" or "supportedMethods" or "unsupportedMethods")))
                    {
                        problems.Add("candidate coverage does not match the bounded scalar-guard profile");
                    }
                }
            }

            if (!root.TryGetProperty("diagnostics", out var diagnostics) || diagnostics.ValueKind != JsonValueKind.Array)
            {
                problems.Add("diagnostics must be an array");
            }

            var payloadName = profile == "ce.models.v1" ? "elements" : "candidates";
            if (!root.TryGetProperty(payloadName, out var payload) || payload.ValueKind != JsonValueKind.Array)
            {
                problems.Add($"{payloadName} must be an array");
            }

            if (profile == "ce.models.v1" &&
                (!root.TryGetProperty("relationships", out var relationships) || relationships.ValueKind != JsonValueKind.Array))
            {
                problems.Add("relationships must be an array");
            }
        }
        catch (JsonException)
        {
            problems.Add("artifact is not valid JSON");
        }
    }

    private static void RequireString(JsonElement root, string name, string expected, List<string> problems)
    {
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String ||
            !string.Equals(property.GetString(), expected, StringComparison.Ordinal))
        {
            problems.Add($"{name} does not match the collection identity");
        }
    }

    private static void RequireString(JsonElement root, string name, IReadOnlyCollection<string> allowed, List<string> problems)
    {
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String ||
            property.GetString() is not { } value || !allowed.Contains(value, StringComparer.Ordinal))
        {
            problems.Add($"{name} is missing or unsupported");
        }
    }

    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex("^[a-f0-9]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9._:-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryIdentifierPattern();
}
