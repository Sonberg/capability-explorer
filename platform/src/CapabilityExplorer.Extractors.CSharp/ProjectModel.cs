using System.Xml.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CapabilityExplorer.Extractors.CSharp;

public sealed record ProjectSelectionContract(IReadOnlyList<string> ProjectPaths, string SelectionSha256, string SourceSha256);
public sealed record ProjectSelectionReport(int CandidateCount, int TestExcludedCount, int SelectedCount, string SelectionSha256, string SourceSha256);
internal static class ProjectSelectionJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

public sealed record PackageRef(string Id, string? Version, int Line)
{
    public bool UnsupportedAssets { get; init; }
}

public sealed record ProjectRef(string Include, string? ResolvedPath, int Line)
{
    /// <summary>False keeps the declared dependency but omits its compile symbols, including unsupported metadata.</summary>
    public bool ReferenceOutputAssembly { get; init; } = true;
}

/// <summary>
/// A C# project read from its .csproj as plain XML. MSBuild is never evaluated: the analysed repository could
/// otherwise run arbitrary targets. Unusual project setups degrade to diagnostics, not to code execution.
/// </summary>
public sealed record ProjectModel(
    string FullPath,
    string RelativePath,
    string AssemblyName,
    string Sdk,
    int SdkLine,
    bool Nullable,
    bool ImplicitUsings,
    IReadOnlyList<PackageRef> Packages,
    IReadOnlyList<ProjectRef> ProjectReferences,
    IReadOnlyList<string> SourceFiles)
{
    private static readonly string[] TestProjectSuffixes = [".Test", ".Tests", ".UnitTests", ".IntegrationTests"];

    /// <summary>The packages this project resolves: its references plus the implicit hosting package of worker projects. Sorted, one per id.</summary>
    public IReadOnlyList<PackageRef> PackageRoots() =>
        Packages.Where(p => !p.UnsupportedAssets).Concat(IsWorker && !Packages.Any(p => p.Id.Equals("Microsoft.Extensions.Hosting", StringComparison.OrdinalIgnoreCase)) ? [new PackageRef("Microsoft.Extensions.Hosting", null, 0)] : [])
            .DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList();

    public bool IsWeb => Sdk.Contains("Web", StringComparison.OrdinalIgnoreCase);

    /// <summary>A single literal target. Conditional, inherited and multi-target builds are not evaluated.</summary>
    public string? TargetFramework { get; init; }

    public IReadOnlyList<ResolutionIssue> ResolutionIssues { get; init; } = [];

    public IReadOnlyList<string> FrameworkReferences { get; init; } = [];

    /// <summary>&lt;CheckForOverflowUnderflow&gt;true: integer arithmetic throws instead of wrapping.</summary>
    public bool CheckedArithmetic { get; init; }

    public bool IsWorker => Sdk.Contains("Worker", StringComparison.OrdinalIgnoreCase);

    /// <summary>Test projects are available for opt-in analysis but excluded from the default production graph.</summary>
    public bool IsTestProject { get; init; }

    /// <summary>Web, Worker and OutputType=Exe projects are executables (top-level statements need that).</summary>
    public bool IsExecutable { get; init; }

    public static IReadOnlyList<ProjectModel> Discover(string root, bool includeTests = true, ProjectSelectionContract? selection = null)
    {
        if (selection is not null) return LoadSelected(root, selection, includeTests).Projects;
        var projects = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !Ignored(Path.GetRelativePath(root, p)))
            .Order(StringComparer.Ordinal)
            .ToList();
        var projectDirs = projects.Select(p => Path.GetDirectoryName(p)!).ToList();
        var models = projects.Select(p => Read(root, p, projectDirs));
        return (includeTests ? models : models.Where(p => !p.IsTestProject)).ToList();
    }

    internal static ProjectSelectionReport ValidateSelection(string root, ProjectSelectionContract selection, bool includeTests)
    {
        var result = LoadSelected(root, selection, includeTests);
        return result.Report;
    }

    internal static ProjectSelectionContract ComputeSelectionContract(string root, IReadOnlyList<string> projectPaths)
    {
        ArgumentNullException.ThrowIfNull(projectPaths);
        var fullRoot = Path.GetFullPath(root);
        var allProjectPaths = EnumerateProjectPaths(fullRoot); ValidateProjectPaths(projectPaths);
        var allProjectDirs = allProjectPaths.Select(Path.GetDirectoryName).OfType<string>().ToArray();
        var selected = ResolveSelectedPaths(fullRoot, allProjectPaths, projectPaths);
        var models = selected.Select(path => Read(fullRoot, path, allProjectDirs, boundedSources: true)).ToArray();
        return new ProjectSelectionContract(projectPaths.Order(StringComparer.Ordinal).ToArray(),
            SelectionDigest(SelectionInputs(fullRoot, models)), SourceIdentity(fullRoot));
    }
    internal static string SourceIdentity(string root)
    {
        var fullRoot = Path.GetFullPath(root); var files = EnumerateSourceFiles(fullRoot);
        var identities = files.Select(path => LogicalPath(fullRoot, path) + ":" + HashFile(fullRoot, path, 64L * 1024 * 1024));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', identities))));
    }
    private static (IReadOnlyList<ProjectModel> Projects, ProjectSelectionReport Report) LoadSelected(
        string root, ProjectSelectionContract selection, bool includeTests)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var fullRoot = Path.GetFullPath(root); RejectLinks(fullRoot, fullRoot);
        if (!Directory.Exists(fullRoot)) throw new InvalidDataException("selected source root is missing");
        ValidateProjectPaths(selection.ProjectPaths);

        var sourceSha256 = SourceIdentity(fullRoot);
        if (!IsSha256(selection.SourceSha256) || sourceSha256 != selection.SourceSha256)
            throw new InvalidDataException("selected source identity does not match");

        var allProjectPaths = EnumerateProjectPaths(fullRoot);
        var allProjectDirs = allProjectPaths.Select(Path.GetDirectoryName).OfType<string>().ToArray();
        var selectedPaths = ResolveSelectedPaths(fullRoot, allProjectPaths, selection.ProjectPaths);
        var models = selectedPaths.Select(path => Read(fullRoot, path, allProjectDirs, boundedSources: true)).ToArray();
        var actualSelection = SelectionDigest(SelectionInputs(fullRoot, models));
        if (!IsSha256(selection.SelectionSha256) || actualSelection != selection.SelectionSha256)
            throw new InvalidDataException("selected project ownership or content does not match");

        ValidateSelectedReferences(fullRoot, models, allProjectPaths, selectedPaths);
        var selected = includeTests ? models : models.Where(model => !model.IsTestProject).ToArray();
        var report = new ProjectSelectionReport(models.Length, models.Length - selected.Length, selected.Length, actualSelection, sourceSha256);
        return (selected, report);
    }
    private static void ValidateSelectedReferences(
        string root, IReadOnlyList<ProjectModel> selectedModels, IReadOnlyList<string> allProjectPaths,
        IReadOnlyList<string> selectedPaths)
    {
        var selected = selectedPaths.ToHashSet(StringComparer.Ordinal); var known = allProjectPaths.ToHashSet(StringComparer.Ordinal);
        foreach (var model in selectedModels)
            foreach (var reference in model.ProjectReferences)
            {
                if (reference.Include.Any(char.IsControl))
                    throw new InvalidDataException("selected project reference contains a control character");
                var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(model.FullPath)!, reference.Include));
                if (!Within(root, target))
                    throw new InvalidDataException("selected project reference escapes the source root");
                if (reference.ResolvedPath is null || !File.Exists(target)) continue;
                var canonicalTarget = Path.GetFullPath(reference.ResolvedPath);
                if (!Within(root, canonicalTarget)) throw new InvalidDataException("selected project reference escapes the source root");
                if (known.Contains(canonicalTarget) && !selected.Contains(canonicalTarget))
                    throw new InvalidDataException("selected-project-reference-excluded");
            }
    }
    private static List<ProjectSelectionContractInput> SelectionInputs(string root, IReadOnlyList<ProjectModel> models)
    {
        var values = new List<ProjectSelectionContractInput>();
        foreach (var model in models)
        {
            values.Add(new(model.RelativePath, HashFile(root, model.FullPath, 8L * 1024 * 1024)));
            foreach (var source in model.SourceFiles)
                values.Add(new(LogicalPath(root, source), HashFile(root, source, 8L * 1024 * 1024)));
        }
        return values;
    }
    private static string SelectionDigest(IEnumerable<ProjectSelectionContractInput> inputs)
    {
        var canonical = inputs.OrderBy(input => input.Path, StringComparer.Ordinal).Select(input => new { path = input.Path, sha256 = input.Sha256 });
        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical, ProjectSelectionJson.Options);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
    private static void ValidateProjectPaths(IReadOnlyList<string> projectPaths)
    {
        if (projectPaths.Count is < 1 or > 100) throw new InvalidDataException("selected project count is outside its bounds");
        var exact = new HashSet<string>(StringComparer.Ordinal); var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in projectPaths)
        {
            if (string.IsNullOrEmpty(path) || Encoding.UTF8.GetByteCount(path) > 1024 || path.StartsWith('/') ||
                path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl) ||
                !Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
                path.Split('/').Any(segment => segment.Length == 0 || segment is "." or "..") || !exact.Add(path) || !folded.Add(path))
                throw new InvalidDataException("selected project path is unsafe, duplicated, or case-colliding");
        }
    }

    private static string[] EnumerateProjectPaths(string root)
    {
        var result = new List<string>(); var pending = new Stack<(string Directory, int Depth)>(); pending.Push((root, 0));
        var entries = 0;
        var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.TryPop(out var item))
        {
            RejectLinks(root, item.Directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(item.Directory).Order(StringComparer.Ordinal))
            {
                if (++entries > 100_000) throw new InvalidDataException("selected source entry count exceeds its bound");
                var name = Path.GetFileName(entry);
                if (Ignored(name)) continue;
                RejectLinks(root, entry);
                var relative = LogicalPath(root, entry);
                ValidateLogicalPath(relative);
                if (Directory.Exists(entry))
                {
                    if (item.Depth >= 128) throw new InvalidDataException("selected source depth exceeds its bound");
                    pending.Push((entry, item.Depth + 1));
                }
                else if (Path.GetExtension(entry).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    if (!folded.Add(relative)) throw new InvalidDataException("project paths contain a case collision");
                    result.Add(entry);
                }
            }
        }
        return result.OrderBy(path => LogicalPath(root, path), StringComparer.Ordinal).ToArray();
    }

    private static string[] ResolveSelectedPaths(string root, IReadOnlyList<string> allPaths, IReadOnlyList<string> requested)
    {
        var available = allPaths.ToDictionary(path => LogicalPath(root, path), StringComparer.Ordinal); var result = new string[requested.Count];
        for (var index = 0; index < requested.Count; index++)
        {
            if (!available.TryGetValue(requested[index], out var path))
                throw new InvalidDataException("selected project path is missing or does not match filesystem ownership");
            RejectLinks(root, path);
            result[index] = path;
        }
        return result;
    }

    private static string[] EnumerateSourceFiles(string root)
    {
        var result = new List<string>(); var pending = new Stack<(string Directory, int Depth)>(); pending.Push((root, 0));
        var entries = 0;
        var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.TryPop(out var item))
        {
            RejectLinks(root, item.Directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(item.Directory).Order(StringComparer.Ordinal))
            {
                if (++entries > 200_000) throw new InvalidDataException("source identity entry count exceeds its bound");
                if (Path.GetFileName(entry) == "node_modules") continue;
                RejectLinks(root, entry);
                var relative = LogicalPath(root, entry);
                ValidateLogicalPath(relative);
                if (Directory.Exists(entry))
                {
                    if (item.Depth >= 128) throw new InvalidDataException("source identity depth exceeds its bound");
                    pending.Push((entry, item.Depth + 1));
                }
                else
                {
                    if (!folded.Add(relative)) throw new InvalidDataException("source paths contain a case collision");
                    result.Add(entry);
                }
            }
        }
        return result.OrderBy(path => LogicalPath(root, path), StringComparer.Ordinal).ToArray();
    }

    private static string[] DiscoverSelectedSources(string root, string directory, IReadOnlyList<string> projectDirs)
    {
        var nested = projectDirs.Where(path => path != directory && path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal)).ToArray();
        var pending = new Stack<(string Directory, int Depth)>(); pending.Push((directory, 0));
        var result = new List<string>();
        var entries = 0;
        while (pending.TryPop(out var item))
        {
            RejectLinks(root, item.Directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(item.Directory).Order(StringComparer.Ordinal))
            {
                if (++entries > 100_000) throw new InvalidDataException("selected project source entry count exceeds its bound");
                if (Ignored(Path.GetRelativePath(directory, entry))) continue;
                RejectLinks(root, entry);
                if (Directory.Exists(entry))
                {
                    if (nested.Any(path => entry == path || entry.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                        continue;
                    if (item.Depth >= 128) throw new InvalidDataException("selected project source depth exceeds its bound");
                    pending.Push((entry, item.Depth + 1));
                }
                else if (Path.GetExtension(entry).Equals(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    _ = LogicalPath(root, entry);
                    result.Add(entry);
                }
            }
        }
        return result.OrderBy(path => LogicalPath(root, path), StringComparer.Ordinal).ToArray();
    }

    private static void ValidateLogicalPath(string path)
    {
        if (path.Length == 0 || path.StartsWith('/') || path.Contains('\\') ||
            path.Contains(':') || path.Any(char.IsControl) ||
            path.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."))
            throw new InvalidDataException("source path is unsafe");
    }

    private static string LogicalPath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
            throw new InvalidDataException("source path escapes its root");
        return relative;
    }

    private static bool Within(string root, string path) =>
        Path.GetRelativePath(root, path) is var relative && relative != ".." && !relative.StartsWith("../", StringComparison.Ordinal) && !Path.IsPathRooted(relative);

    private static string HashFile(string root, string path, long maximum)
    {
        RejectLinks(root, path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is < 0 || info.Length > maximum)
            throw new InvalidDataException("selected input exceeds its byte bound");
        using var stream = File.OpenRead(path); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long length = 0;
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            length += read;
            if (length > maximum) throw new InvalidDataException("selected input exceeds its byte bound");
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void RejectLinks(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root); var fullPath = Path.GetFullPath(path);
        if (!Within(fullRoot, fullPath))
            throw new InvalidDataException("selected source path escapes its root");
        for (var current = fullPath; current is not null; current = Path.GetDirectoryName(current))
        {
            var info = Directory.Exists(current) ? (FileSystemInfo)new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null || info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("selected source contains a link or reparse path");
            if (current == fullRoot) break;
        }
    }

    private static bool Ignored(string relative) =>
        relative.Replace('\\', '/').Split('/').Any(s => s is "bin" or "obj" or "node_modules" or ".git");

    private static ProjectModel Read(string root, string path, IReadOnlyList<string> allProjectDirs, bool boundedSources = false)
    {
        var text = File.ReadAllText(path);
        var doc = XDocument.Parse(text, LoadOptions.SetLineInfo);
        var project = doc.Root!;
        string? Prop(string name) => project.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
        int Line(XElement e) => ((System.Xml.IXmlLineInfo)e).LineNumber;
        static bool Conditional(XElement e) => e.AncestorsAndSelf().Any(x => x.Attribute("Condition") is not null);
        var issues = new List<ResolutionIssue>();
        var targets = project.Descendants().Where(e => e.Name.LocalName is "TargetFramework" or "TargetFrameworks").ToList();
        var target = targets.Count == 1 && targets[0].Name.LocalName == "TargetFramework" && !Conditional(targets[0])
            && FrameworkSelection.Parse(targets[0].Value.Trim()) is not null ? targets[0].Value.Trim() : null;
        if (target is null)
        {
            issues.Add(new("UNSUPPORTED_TARGET_FRAMEWORK", "A single literal, unconditional TargetFramework is required; MSBuild properties, imports and multi-targeting are not evaluated"));
        }
        var conditionalReferences = project.Descendants().Where(e => e.Name.LocalName is "PackageReference" or "ProjectReference" or "FrameworkReference").Where(Conditional).ToList();
        if (conditionalReferences.Count > 0)
        {
            issues.Add(new("UNSUPPORTED_CONDITIONAL_REFERENCES", "Conditional package/project/framework references are omitted because MSBuild conditions are not evaluated"));
        }
        var frameworks = project.Descendants().Where(e => e.Name.LocalName == "FrameworkReference" && !Conditional(e))
            .Select(e => (string?)e.Attribute("Include")).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        foreach (var framework in frameworks.Where(f => f is not "Microsoft.NETCore.App" and not "Microsoft.AspNetCore.App"))
        {
            issues.Add(new("UNSUPPORTED_FRAMEWORK_REFERENCE", $"FrameworkReference {framework} is not supported by this static extractor"));
        }
        var dir = Path.GetDirectoryName(path)!;
        var central = CentralVersions(root, dir, issues);
        static bool AssetControls(XElement e) => e.Attributes().Any(a => a.Name.LocalName is "IncludeAssets" or "ExcludeAssets" or "PrivateAssets")
            || e.Elements().Any(c => c.Name.LocalName is "IncludeAssets" or "ExcludeAssets" or "PrivateAssets");
        if (project.Descendants().Any(e => e.Name.LocalName == "PackageReference" && AssetControls(e)))
        {
            issues.Add(new("UNSUPPORTED_PACKAGE_ASSET_METADATA", "Package references with IncludeAssets, ExcludeAssets or PrivateAssets are omitted from compile resolution; their asset flow is not evaluated"));
        }
        var packages = project.Descendants().Where(e => e.Name.LocalName == "PackageReference" && !Conditional(e))
            .Select(e => new PackageRef(
                (string?)e.Attribute("Include") ?? (string?)e.Attribute("Update") ?? "",
                (string?)e.Attribute("Version") ?? e.Elements().FirstOrDefault(x => x.Name.LocalName == "Version")?.Value ?? central.GetValueOrDefault(((string?)e.Attribute("Include") ?? "").ToLowerInvariant()),
                Line(e)) { UnsupportedAssets = AssetControls(e) || issues.Any(i => i.Code == "UNSUPPORTED_CENTRAL_PACKAGE_CONDITIONS")
                    && e.Attribute("Version") is null && !e.Elements().Any(x => x.Name.LocalName == "Version")
                    && !central.ContainsKey(((string?)e.Attribute("Include") ?? "").ToLowerInvariant()) })
            .Where(p => p.Id.Length > 0)
            .ToList();
        var projectRefs = project.Descendants().Where(e => e.Name.LocalName == "ProjectReference" && !Conditional(e))
            .Select(e =>
            {
                var include = ((string?)e.Attribute("Include") ?? "").Replace('\\', '/');
                var full = Path.GetFullPath(Path.Combine(dir, include));
                var metadata = e.Elements().Where(c => c.Name.LocalName == "ReferenceOutputAssembly").ToArray();
                var values = e.Attributes().Where(a => a.Name.LocalName == "ReferenceOutputAssembly")
                    .Select(a => a.Value).Concat(metadata.Select(c => c.Value)).ToArray();
                var output = true;
                if (values.Length > 1 || metadata.Any(c => Conditional(c) || c.HasElements)
                    || values.Length == 1 && !bool.TryParse(values[0].Trim(), out output))
                {
                    output = false;
                    issues.Add(new("UNSUPPORTED_PROJECT_REFERENCE_METADATA", $"Project reference {include} requires a single literal, unconditional ReferenceOutputAssembly value; its compile symbols are omitted"));
                }
                return new ProjectRef(include, File.Exists(full) ? full : null, Line(e)) { ReferenceOutputAssembly = output };
            })
            .ToList();
        // Default compile items: every .cs under the project directory, minus bin/obj and nested projects.
        var nested = allProjectDirs.Where(d => d != dir && d.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.Ordinal)).ToList();
        var sources = boundedSources
            ? DiscoverSelectedSources(root, dir, allProjectDirs).ToList()
            : Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !Ignored(Path.GetRelativePath(dir, f)) && !nested.Any(n => f.StartsWith(n + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                .Order(StringComparer.Ordinal)
                .ToList();
        var sdkAttr = project.Attribute("Sdk");
        var outputType = Prop("OutputType");
        var sdk = sdkAttr?.Value ?? "Microsoft.NET.Sdk";
        var explicitTestProject = Prop("IsTestProject");
        var relativePath = Path.GetRelativePath(root, path).Replace('\\', '/');
        var projectName = Path.GetFileNameWithoutExtension(path);
        var testByConvention = relativePath.Split('/').Any(s => s.Equals("test", StringComparison.OrdinalIgnoreCase) || s.Equals("tests", StringComparison.OrdinalIgnoreCase) || s.Equals("__tests__", StringComparison.OrdinalIgnoreCase))
            || TestProjectSuffixes.Any(s => projectName.EndsWith(s, StringComparison.OrdinalIgnoreCase));
        var isTestProject = string.Equals(explicitTestProject, "true", StringComparison.OrdinalIgnoreCase)
            || explicitTestProject is null && (sdk.Contains("Test", StringComparison.OrdinalIgnoreCase)
                || packages.Any(p => p.Id.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase))
                || testByConvention);
        return new ProjectModel(
            path,
            relativePath,
            Prop("AssemblyName") is { Length: > 0 } name ? name : Path.GetFileNameWithoutExtension(path),
            sdk,
            sdkAttr is null ? 1 : Line(project),
            !string.Equals(Prop("Nullable"), "disable", StringComparison.OrdinalIgnoreCase) && Prop("Nullable") is not null,
            string.Equals(Prop("ImplicitUsings"), "enable", StringComparison.OrdinalIgnoreCase) || string.Equals(Prop("ImplicitUsings"), "true", StringComparison.OrdinalIgnoreCase),
            packages,
            projectRefs,
            sources)
        {
            TargetFramework = target,
            ResolutionIssues = issues,
            FrameworkReferences = frameworks,
            CheckedArithmetic = string.Equals(Prop("CheckForOverflowUnderflow"), "true", StringComparison.OrdinalIgnoreCase),
            IsTestProject = isTestProject,
            IsExecutable = string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase) || string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase)
                || sdk.Contains("Web", StringComparison.OrdinalIgnoreCase) || sdk.Contains("Worker", StringComparison.OrdinalIgnoreCase),
        };
    }
    private sealed record ProjectSelectionContractInput(string Path, string Sha256);

    /// <summary>Central package management: Directory.Packages.props in the project directory or above (within root).</summary>
    private static Dictionary<string, string> CentralVersions(string root, string dir, List<ResolutionIssue> issues)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var d = dir; d is not null && d.StartsWith(Path.GetFullPath(root), StringComparison.Ordinal); d = Path.GetDirectoryName(d))
        {
            var props = Path.Combine(d, "Directory.Packages.props");
            if (!File.Exists(props))
            {
                continue;
            }
            var versions = XDocument.Load(props).Descendants().Where(e => e.Name.LocalName == "PackageVersion").ToList();
            var conditionalIds = versions.Where(e => e.AncestorsAndSelf().Any(x => x.Attribute("Condition") is not null))
                .Select(e => (string?)e.Attribute("Include")).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (conditionalIds.Count > 0)
            {
                issues.Add(new("UNSUPPORTED_CENTRAL_PACKAGE_CONDITIONS", "Packages with conditional central PackageVersion declarations are omitted because MSBuild conditions are not evaluated"));
            }
            foreach (var e in versions)
            {
                if ((string?)e.Attribute("Include") is { } id && (string?)e.Attribute("Version") is { } v)
                {
                    if (!conditionalIds.Contains(id)) result.TryAdd(id.ToLowerInvariant(), v);
                }
            }
            break;
        }
        return result;
    }
    /// <summary>The global usings the SDK generates for ImplicitUsings=enable.</summary>
    public IReadOnlyList<string> ImplicitNamespaces()
    {
        if (!ImplicitUsings)
        {
            return [];
        }
        List<string> ns = ["System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Net.Http", "System.Threading", "System.Threading.Tasks"];
        if (IsWeb)
        {
            ns.AddRange(["System.Net.Http.Json", "Microsoft.AspNetCore.Builder", "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Http", "Microsoft.AspNetCore.Routing",
                "Microsoft.Extensions.Configuration", "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.Hosting", "Microsoft.Extensions.Logging"]);
        }
        else if (IsWorker)
        {
            ns.AddRange(["Microsoft.Extensions.Configuration", "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.Hosting", "Microsoft.Extensions.Logging"]);
        }
        return ns;
    }
}
