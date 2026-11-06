using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CapabilityExplorer.Capabilities;

public static partial class NativeAdapterPlanner
{
    private const string PlanSchema = "ce.adapter.plan.v1";

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.Ordinal)
        { ".git", "bin", "obj", "node_modules" };
    private static readonly HashSet<string> EmbeddedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".csx", ".razor", ".cshtml" };
    private static readonly HashSet<string> UnsupportedLanguageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".py", ".java", ".go", ".rs", ".sql" };
    public static async Task<NativeAdapterPlan> PlanAsync(
        string sourceRoot,
        string repositoryId,
        string commitSha,
        string treeSha,
        NativeAdapterManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentNullException.ThrowIfNull(manifest);
        ValidateManifest(manifest);
        ValidateIdentity(repositoryId, commitSha, treeSha);

        var root = Path.GetFullPath(sourceRoot);
        RejectLinks(root);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("adapter source root is missing or inaccessible");
        }

        var diagnostics = new List<AdapterPlanDiagnostic>();
        var excludedDirectories = new List<string>();
        var entries = Enumerate(root, manifest.Limits, excludedDirectories, diagnostics, cancellationToken,
            out var enumerationComplete);
        var traversalComplete = enumerationComplete;
        var inputFiles = entries.Where(entry => InputProfile(entry.Path) is not null)
            .OrderBy(entry => entry.Path, StringComparer.Ordinal).ToList();
        var projects = inputFiles.Where(entry => Path.GetExtension(entry.Path).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray();
        var projectsByDirectory = projects.GroupBy(project => Parent(project.Path), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var ownerDirectoryByPath = new Dictionary<string, string?>(StringComparer.Ordinal);
        var inputsByDirectory = new Dictionary<string, List<FileEntry>>(StringComparer.Ordinal);
        foreach (var entry in inputFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = NearestProjectDirectory(entry.Path, projectsByDirectory);
            ownerDirectoryByPath[entry.Path] = directory;
            if (directory is not null)
            {
                if (!inputsByDirectory.TryGetValue(directory, out var owned))
                {
                    owned = [];
                    inputsByDirectory[directory] = owned;
                }

                owned.Add(entry);
            }
        }

        var configurationSha = ConfigurationSha256(manifest);
        var truncatedUnitCount = Math.Max(0, projects.Length - manifest.Limits.MaxUnits);
        if (truncatedUnitCount > 0)
        {
            enumerationComplete = false;
            diagnostics.Add(new("unit-limit-exceeded", ".", "unit planning stopped at the declared project-unit limit"));
        }

        var hashes = new Dictionary<string, HashedInput>(StringComparer.Ordinal);
        long totalBytes = 0;
        var totalBudgetExhausted = false;
        foreach (var entry in inputFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsExcluded(entry.Path, manifest.Configuration.ExcludedPathPrefixes))
            {
                hashes[entry.Path] = new("excluded", null, 0, null);
                continue;
            }

            if (entry.IsLink)
            {
                hashes[entry.Path] = new("unknown", null, 0, "linked-input");
                continue;
            }

            if (totalBudgetExhausted)
            {
                hashes[entry.Path] = new("unknown", null, 0, "total-byte-limit");
                continue;
            }

            try
            {
                var hashed = await HashInputAsync(entry.FullPath, manifest.Limits.MaxFileBytes,
                    manifest.Limits.MaxTotalBytes - totalBytes, cancellationToken).ConfigureAwait(false);
                totalBytes += hashed.ByteLength;
                hashes[entry.Path] = new("read", hashed.Sha256, hashed.ByteLength, null);
            }
            catch (InputLimitException error)
            {
                hashes[entry.Path] = new("unknown", null, error.ByteLength, error.Code);
                diagnostics.Add(new(error.Code, entry.Path, error.Message));
                enumerationComplete = false;
                totalBudgetExhausted = error.Code == "total-byte-limit";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                hashes[entry.Path] = new("unknown", null, 0, "input-inaccessible");
                diagnostics.Add(new("input-inaccessible", entry.Path, "recognized input could not be read"));
                enumerationComplete = false;
            }
        }

        var sourceSnapshotSha = SourceSnapshotSha256(inputFiles, hashes);
        var units = new List<AdapterPlanUnit>(Math.Min(projects.Length, manifest.Limits.MaxUnits));
        var unitStatusByPath = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var project in projects.Skip(manifest.Limits.MaxUnits))
        {
            unitStatusByPath[project.Path] = "unknown";
        }

        foreach (var project in projects.Take(manifest.Limits.MaxUnits))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projectDir = Parent(project.Path);
            var directoryInputs = inputsByDirectory.GetValueOrDefault(projectDir) ?? [];
            var ownedSources = directoryInputs.Where(entry =>
                Path.GetExtension(entry.Path).Equals(".cs", StringComparison.OrdinalIgnoreCase)).ToArray();
            var embedded = directoryInputs.Where(entry =>
                EmbeddedExtensions.Contains(Path.GetExtension(entry.Path))).ToArray();
            var reasons = new SortedSet<string>(StringComparer.Ordinal);
            var projectHash = hashes[project.Path];
            string status;
            if (projectHash.Status == "excluded")
            {
                status = "excluded";
                reasons.Add("manifest-exclusion");
            }
            else if (!traversalComplete)
            {
                status = "unknown";
                reasons.Add("incomplete-source-traversal");
            }
            else if (projectHash.Status == "unknown")
            {
                status = "unknown";
                reasons.Add(projectHash.ErrorCode ?? "input-unknown");
            }
            else if (projectsByDirectory[projectDir].Length > 1)
            {
                status = "unsupported";
                reasons.Add("ambiguous-project-directory");
                diagnostics.Add(new("ambiguous-project-directory", project.Path,
                    "multiple projects share this directory; source ownership cannot be resolved by directory discovery"));
            }
            else if (embedded.Length > 0)
            {
                status = "unsupported";
                reasons.Add("unsupported-embedded-source");
                foreach (var source in embedded)
                {
                    diagnostics.Add(new("unsupported-embedded-source", source.Path,
                        "C# embedded source is outside the declared project input profile"));
                }
            }
            else if (ownedSources.Any(source => hashes[source.Path].Status == "excluded"))
            {
                status = "unsupported";
                reasons.Add("excluded-source-cannot-be-filtered-by-adapter");
                diagnostics.Add(new("exclusion-not-enforceable", project.Path,
                    "the native project adapter cannot apply a per-file exclusion inside this unit"));
            }
            else if (ownedSources.Any(source => hashes[source.Path].Status == "unknown"))
            {
                status = "unknown";
                reasons.Add("source-input-unknown");
            }
            else if (ownedSources.Length == 0)
            {
                status = "unsupported";
                reasons.Add("no-csharp-source");
            }
            else
            {
                status = "eligible";
            }

            var projectIdentity = string.Join('\n',
                "ce.adapter.unit.v1", repositoryId, commitSha, treeSha, project.Path,
                manifest.AdapterId, manifest.AdapterVersion, manifest.RecipeSha256,
                configurationSha, sourceSnapshotSha);
            var unitId = Digest(Encoding.UTF8.GetBytes(projectIdentity));
            var unit = new AdapterPlanUnit(project.Path, unitId, projectHash.Sha256,
                projectHash.ByteLength, ownedSources.Length, status, reasons.ToArray());
            units.Add(unit);
            unitStatusByPath[project.Path] = status;
        }

        var inputs = new List<AdapterPlanInput>(inputFiles.Count);
        foreach (var entry in inputFiles)
        {
            var extension = Path.GetExtension(entry.Path);
            var hash = hashes[entry.Path];
            var ownerDirectory = ownerDirectoryByPath[entry.Path];
            var ambiguousOwnership = ownerDirectory is not null && projectsByDirectory[ownerDirectory].Length > 1;
            var unitPath = extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                ? entry.Path
                : ownerDirectory is not null && !ambiguousOwnership
                    ? projectsByDirectory[ownerDirectory][0].Path : null;
            var profile = InputProfile(entry.Path)!;
            var status = hash.Status switch
            {
                "unknown" => "unknown",
                "excluded" => "excluded",
                _ when EmbeddedExtensions.Contains(extension) || UnsupportedLanguageExtensions.Contains(extension) => "unsupported",
                _ when extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) =>
                    unitStatusByPath.GetValueOrDefault(entry.Path, "unknown"),
                _ when !traversalComplete => "unknown",
                _ when unitPath is null => "unsupported",
                _ => unitStatusByPath.GetValueOrDefault(unitPath, "unknown")
            };
            if (ambiguousOwnership && unitPath is null)
            {
                diagnostics.Add(new("ambiguous-project-directory", entry.Path,
                    "source ownership is ambiguous between projects in the same directory"));
            }
            else if (status == "unsupported" && hash.Status == "read")
            {
                diagnostics.Add(new(
                    EmbeddedExtensions.Contains(extension) ? "unsupported-embedded-source" : "unsupported-input-profile",
                    entry.Path,
                    EmbeddedExtensions.Contains(extension)
                        ? "embedded or generated C# source is outside the declared adapter profile"
                        : "input language is not supported by the selected C# adapter"));
            }
            else if (unitPath is null && extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) && status == "unsupported")
            {
                diagnostics.Add(new("source-outside-project", entry.Path,
                    "the selected C# adapter discovers project units, not loose source files"));
            }

            inputs.Add(new(entry.Path, profile, status, hash.Sha256, hash.ByteLength, unitPath));
        }

        units.Sort((left, right) => StringComparer.Ordinal.Compare(left.UnitPath, right.UnitPath));
        inputs.Sort((left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));
        var knownUnitCounts = Counts(units.Select(unit => unit.Status));
        var unitCounts = knownUnitCounts with
        {
            Total = knownUnitCounts.Total + truncatedUnitCount,
            Unknown = knownUnitCounts.Unknown + truncatedUnitCount
        };
        var inputCounts = Counts(inputs.Select(input => input.Status));
        var unknownCount = unitCounts.Unknown + inputCounts.Unknown + (enumerationComplete ? 0 : 1);
        return new NativeAdapterPlan(PlanSchema, repositoryId, commitSha, treeSha, manifest.AdapterId,
            manifest.AdapterVersion, manifest.RecipeSha256, configurationSha, sourceSnapshotSha,
            enumerationComplete, unknownCount, unitCounts, inputCounts, units, inputs,
            excludedDirectories.Order(StringComparer.Ordinal).ToArray(),
            diagnostics.Distinct().OrderBy(item => item.Path, StringComparer.Ordinal)
                .ThenBy(item => item.Code, StringComparer.Ordinal).ToArray());
    }

    private static string ConfigurationSha256(NativeAdapterManifest manifest)
    {
        var normalized = NormalizeExclusions(manifest.Configuration.ExcludedPathPrefixes);
        var canonical = manifest with
        {
            SupportedInputProfiles = manifest.SupportedInputProfiles.Order(StringComparer.Ordinal).ToArray(),
            Configuration = new NativeAdapterConfiguration(normalized)
        };
        return Digest(JsonSerializer.SerializeToUtf8Bytes(canonical, ManifestJson));
    }

    private static string SourceSnapshotSha256(IReadOnlyList<FileEntry> files, IReadOnlyDictionary<string, HashedInput> hashes)
    {
        var canonical = string.Join('\n', files.OrderBy(item => item.Path, StringComparer.Ordinal)
            .Select(item =>
            {
                var hash = hashes[item.Path];
                return string.Join('\t', item.Path, hash.Status, hash.ByteLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    hash.Sha256 ?? string.Empty, hash.ErrorCode ?? string.Empty);
            }));
        return Digest(Encoding.UTF8.GetBytes(canonical));
    }

    private static AdapterPlanCounts Counts(IEnumerable<string> statuses)
    {
        var values = statuses.ToArray();
        return new(values.Length, values.Count(status => status == "eligible"), values.Count(status => status == "excluded"),
            values.Count(status => status == "unsupported"), values.Count(status => status == "unknown"));
    }

    private static string? InputProfile(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)) return ProjectProfile;
        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)) return "csharp.source.v1";
        if (EmbeddedExtensions.Contains(extension)) return "csharp.embedded.v1";
        if (UnsupportedLanguageExtensions.Contains(extension)) return "unsupported.source.v1";
        return null;
    }

    private static string? NearestProjectDirectory(string sourcePath, IReadOnlyDictionary<string, FileEntry[]> projects)
    {
        for (var directory = Parent(sourcePath); ; directory = Parent(directory))
        {
            if (projects.ContainsKey(directory)) return directory;
            if (directory.Length == 0) return null;
        }
    }

    private static bool IsExcluded(string path, IReadOnlyList<string> exclusions) =>
        exclusions.Any(prefix => path.Equals(prefix, StringComparison.Ordinal) ||
            path.StartsWith(prefix + "/", StringComparison.Ordinal));

    private static string Parent(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? string.Empty : path[..index];
    }

    private static string ToLogicalPath(string path) => path.Replace(Path.DirectorySeparatorChar, '/');

    private static void ValidateIdentity(string repositoryId, string commitSha, string treeSha)
    {
        if (repositoryId is null || repositoryId.Length is < 1 or > 100 ||
            !char.IsAsciiLetterOrDigit(repositoryId[0]) ||
            repositoryId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_') ||
            !IsGitHash(commitSha) || !IsGitHash(treeSha))
        {
            throw new ArgumentException("repository, commit, or tree identity is invalid");
        }
    }

    private static bool IsGitHash(string? value) =>
        value is { Length: 40 or 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static async Task<byte[]> ReadBoundedAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, maximumBytes + 1 - (int)buffer.Length)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maximumBytes) throw new IOException("adapter manifest exceeds the size limit");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                    new FileInfo(current).LinkTarget is not null || new DirectoryInfo(current).LinkTarget is not null)
                {
                    throw new IOException("adapter paths cannot contain symbolic links or reparse points");
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

}
