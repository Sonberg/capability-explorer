using System.Xml.Linq;

namespace CapabilityExplorer.Extractors.CSharp;

public sealed record PackageRef(string Id, string? Version, int Line)
{
    public bool UnsupportedAssets { get; init; }
}

public sealed record ProjectRef(string Include, string? ResolvedPath, int Line);

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

    public static IReadOnlyList<ProjectModel> Discover(string root, bool includeTests = true)
    {
        var projects = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !Ignored(Path.GetRelativePath(root, p)))
            .Order(StringComparer.Ordinal)
            .ToList();
        var projectDirs = projects.Select(p => Path.GetDirectoryName(p)!).ToList();
        var models = projects.Select(p => Read(root, p, projectDirs));
        return (includeTests ? models : models.Where(p => !p.IsTestProject)).ToList();
    }

    private static bool Ignored(string relative) =>
        relative.Replace('\\', '/').Split('/').Any(s => s is "bin" or "obj" or "node_modules" or ".git");

    private static ProjectModel Read(string root, string path, IReadOnlyList<string> allProjectDirs)
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
                return new ProjectRef(include, File.Exists(full) ? full : null, Line(e));
            })
            .ToList();

        // Default compile items: every .cs under the project directory, minus bin/obj and nested projects.
        var nested = allProjectDirs.Where(d => d != dir && d.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.Ordinal)).ToList();
        var sources = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
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
