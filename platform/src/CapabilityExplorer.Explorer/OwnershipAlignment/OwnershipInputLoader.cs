using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CapabilityExplorer.Explorer;

public sealed class OwnershipInputLoader
{
    private const int MaxBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };

    public static async Task<OwnershipInputs> LoadAsync(
        OwnershipAlignmentOptions options, IReadOnlyList<string> repositories, DateTimeOffset now, CancellationToken ct)
    {
        var snapshots = new Dictionary<string, OwnershipInput<OwnershipSnapshot>>(StringComparer.Ordinal);
        foreach (var system in OwnershipSystems.All)
        {
            options.Snapshots.TryGetValue(system, out var path);
            snapshots.Add(system, await ReadAsync<OwnershipSnapshot>(options.Root, path,
                value => OwnershipInputValidation.Snapshot(value, system, now), ct).ConfigureAwait(false));
        }
        var crosswalk = await ReadAsync<OwnershipCrosswalk>(options.Root, options.Crosswalk,
            OwnershipInputValidation.Crosswalk, ct).ConfigureAwait(false);
        var rules = await ReadAsync<OwnershipRules>(options.Root, options.Rules,
            OwnershipInputValidation.Rules, ct).ConfigureAwait(false);
        return new(repositories, snapshots, crosswalk, rules, options.Repositories);
    }

    private static async Task<OwnershipInput<T>> ReadAsync<T>(
        string root, string? relative, Action<T> validate, CancellationToken ct) where T : class
    {
        if (relative is null) return new("not-configured");
        string? digest = null;
        try
        {
            var path = Resolve(root, relative);
            var info = new FileInfo(path);
            if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new JsonException("Not a regular ownership input.");
            if (info.Length is < 1 or > MaxBytes) throw new JsonException("Ownership input outside byte limit.");
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var buffer = new MemoryStream();
            var bytes = new byte[16 * 1024];
            int read;
            while ((read = await file.ReadAsync(bytes, ct).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + read > MaxBytes) throw new JsonException("Ownership input outside byte limit.");
                buffer.Write(bytes, 0, read);
            }
            var raw = buffer.ToArray();
            digest = Convert.ToHexStringLower(SHA256.HashData(raw));
            using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 16 });
            RejectDuplicates(document.RootElement);
            var value = document.RootElement.Deserialize<T>(Json) ?? throw new JsonException("Null ownership input.");
            validate(value);
            return new("loaded", digest, value);
        }
        catch (UnauthorizedAccessException) { return new("denied", digest); }
        catch (IOException) { return new("unavailable", digest); }
        catch (JsonException) { return new("invalid", digest); }
        catch (ArgumentException) { return new("invalid", digest); }
        catch (NotSupportedException) { return new("invalid", digest); }
    }

    private static string Resolve(string root, string relative)
    {
        if (!Path.IsPathFullyQualified(root) || relative.Length is < 1 or > 1024 ||
            Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Contains(':') ||
            relative.Any(char.IsControl) || relative.Split('/').Any(part => part is "" or "." or ".."))
            throw new JsonException("Unsafe ownership input path.");
        var fullRoot = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new JsonException("Ownership input outside root.");
        for (var entry = full; entry is not null; entry = Path.GetDirectoryName(entry))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new JsonException("Linked ownership input path.");
        }
        return full;
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate ownership input member.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }
}
