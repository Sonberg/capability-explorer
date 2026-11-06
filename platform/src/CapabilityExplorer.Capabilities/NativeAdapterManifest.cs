using System.Text.Json;
using System.Text.Json.Serialization;

namespace CapabilityExplorer.Capabilities;

public sealed record NativeAdapterLimits(int MaxEntries, int MaxUnits, long MaxFileBytes, long MaxTotalBytes, int MaxDepth);

public sealed record NativeAdapterConfiguration(IReadOnlyList<string> ExcludedPathPrefixes);

public sealed record NativeAdapterManifest(
    string Schema,
    string AdapterId,
    string AdapterVersion,
    string RecipeId,
    string RecipeSha256,
    string Toolchain,
    IReadOnlyList<string> SupportedInputProfiles,
    NativeAdapterLimits Limits,
    NativeAdapterConfiguration Configuration);

public sealed record AdapterPlanCounts(int Total, int Eligible, int Excluded, int Unsupported, int Unknown);

public sealed record AdapterPlanUnit(
    string UnitPath,
    string UnitId,
    string? Sha256,
    long ByteLength,
    int SourceFileCount,
    string Status,
    IReadOnlyList<string> Reasons);

public sealed record AdapterPlanInput(
    string Path,
    string Profile,
    string Status,
    string? Sha256,
    long ByteLength,
    string? UnitPath);

public sealed record AdapterPlanDiagnostic(string Code, string Path, string Message);

public sealed record NativeAdapterPlan(
    string Schema,
    string RepositoryId,
    string CommitSha,
    string TreeSha,
    string AdapterId,
    string AdapterVersion,
    string RecipeSha256,
    string ConfigurationSha256,
    string SourceSnapshotSha256,
    bool EnumerationComplete,
    int UnknownCount,
    AdapterPlanCounts UnitCounts,
    AdapterPlanCounts InputCounts,
    IReadOnlyList<AdapterPlanUnit> Units,
    IReadOnlyList<AdapterPlanInput> Inputs,
    IReadOnlyList<string> ExcludedDirectories,
    IReadOnlyList<AdapterPlanDiagnostic> Diagnostics);

public static partial class NativeAdapterPlanner
{
    private const int MaxManifestBytes = 1_048_576;
    private const int HardMaxEntries = 100_000;
    private const int HardMaxUnits = 100;
    private const long HardMaxFileBytes = 16 * 1024 * 1024;
    private const long HardMaxTotalBytes = 128L * 1024 * 1024;
    private const int HardMaxDepth = 128;
    private const string ManifestSchema = "ce.adapter.manifest.v1";
    private const string AdapterId = "csharp.roslyn";
    private const string AdapterVersion = "0.2.0";
    private const string ProjectProfile = "csharp.project.v1";

    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };

    public static async Task<NativeAdapterManifest> ReadManifestAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        var fullPath = Path.GetFullPath(manifestPath);
        RejectLinks(fullPath);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length is < 1 or > MaxManifestBytes)
        {
            throw new IOException("adapter manifest is missing, empty, or exceeds the size limit");
        }

        var bytes = await ReadBoundedAsync(fullPath, MaxManifestBytes, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        RejectDuplicateProperties(document.RootElement);
        var manifest = document.RootElement.Deserialize<NativeAdapterManifest>(ManifestJson)
            ?? throw new JsonException("adapter manifest cannot be null");
        ValidateManifest(manifest);
        return manifest with
        {
            SupportedInputProfiles = manifest.SupportedInputProfiles.Order(StringComparer.Ordinal).ToArray(),
            Configuration = manifest.Configuration with
            {
                ExcludedPathPrefixes = NormalizeExclusions(manifest.Configuration.ExcludedPathPrefixes)
            }
        };
    }

    private static void ValidateManifest(NativeAdapterManifest manifest)
    {
        if (manifest.Schema != ManifestSchema || manifest.AdapterId != AdapterId ||
            manifest.AdapterVersion != AdapterVersion || manifest.RecipeId != "csharp-project-discovery-v1" ||
            manifest.Toolchain != "dotnet10.0" || !IsSha256(manifest.RecipeSha256) ||
            manifest.SupportedInputProfiles is null ||
            !manifest.SupportedInputProfiles.SequenceEqual([ProjectProfile], StringComparer.Ordinal))
        {
            throw new JsonException("unsupported or malformed C# adapter manifest identity/profile");
        }

        if (manifest.Limits is null || manifest.Limits.MaxEntries is < 1 or > HardMaxEntries ||
            manifest.Limits.MaxUnits is < 1 or > HardMaxUnits ||
            manifest.Limits.MaxFileBytes is < 1 or > HardMaxFileBytes ||
            manifest.Limits.MaxTotalBytes is < 1 or > HardMaxTotalBytes ||
            manifest.Limits.MaxDepth is < 1 or > HardMaxDepth ||
            manifest.Configuration?.ExcludedPathPrefixes is null)
        {
            throw new JsonException("adapter limits or configuration are outside the hard bounds");
        }

        _ = NormalizeExclusions(manifest.Configuration.ExcludedPathPrefixes);
    }

    private static string[] NormalizeExclusions(IReadOnlyList<string> exclusions)
    {
        if (exclusions.Count > 1024)
        {
            throw new JsonException("too many path exclusions");
        }

        var normalized = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in exclusions)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.StartsWith('/') ||
                path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl) ||
                path.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.Contains('*') || part.Contains('?')))
            {
                throw new JsonException("manifest exclusion must be a safe relative path prefix");
            }

            if (!normalized.Add(path))
            {
                throw new JsonException("duplicate manifest path exclusion");
            }
        }

        return normalized.ToArray();
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("adapter manifest has duplicate JSON members");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in element.EnumerateArray()) RejectDuplicateProperties(value);
        }
    }
}
