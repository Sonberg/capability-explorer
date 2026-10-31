using System.Xml.Linq;

namespace CapabilityExplorer.Extractors.CSharp;

public sealed record PackageRef(string Id, string? Version, int Line);

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
    /// <summary>The packages this project resolves: its references plus the implicit hosting package of worker projects. Sorted, one per id.</summary>
    public IReadOnlyList<PackageRef> PackageRoots() =>
        Packages.Concat(IsWorker ? [new PackageRef("Microsoft.Extensions.Hosting", null, 0)] : [])
            .DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList();

    public bool IsWeb => Sdk.Contains("Web", StringComparison.OrdinalIgnoreCase);

    /// <summary>&lt;CheckForOverflowUnderflow&gt;true: integer arithmetic throws instead of wrapping.</summary>
    public bool CheckedArithmetic { get; init; }

    public bool IsWorker => Sdk.Contains("Worker", StringComparison.OrdinalIgnoreCase);

    /// <summary>Web, Worker and OutputType=Exe projects are executables (top-level statements need that).</summary>
    public bool IsExecutable { get; init; }

    public static IReadOnlyList<ProjectModel> Discover(string root)
    {
        var projects = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !Ignored(Path.GetRelativePath(root, p)))
            .Order(StringComparer.Ordinal)
            .ToList();
        var projectDirs = projects.Select(p => Path.GetDirectoryName(p)!).ToList();
        return projects.Select(p => Read(root, p, projectDirs)).ToList();
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

        var dir = Path.GetDirectoryName(path)!;
        var central = CentralVersions(root, dir);
        var packages = project.Descendants().Where(e => e.Name.LocalName == "PackageReference")
            .Select(e => new PackageRef(
                (string?)e.Attribute("Include") ?? (string?)e.Attribute("Update") ?? "",
                (string?)e.Attribute("Version") ?? e.Elements().FirstOrDefault(x => x.Name.LocalName == "Version")?.Value ?? central.GetValueOrDefault(((string?)e.Attribute("Include") ?? "").ToLowerInvariant()),
                Line(e)))
            .Where(p => p.Id.Length > 0)
            .ToList();
        var projectRefs = project.Descendants().Where(e => e.Name.LocalName == "ProjectReference")
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
        return new ProjectModel(
            path,
            Path.GetRelativePath(root, path).Replace('\\', '/'),
            Prop("AssemblyName") is { Length: > 0 } name ? name : Path.GetFileNameWithoutExtension(path),
            sdk,
            sdkAttr is null ? 1 : Line(project),
            !string.Equals(Prop("Nullable"), "disable", StringComparison.OrdinalIgnoreCase) && Prop("Nullable") is not null,
            string.Equals(Prop("ImplicitUsings"), "enable", StringComparison.OrdinalIgnoreCase) || string.Equals(Prop("ImplicitUsings"), "true", StringComparison.OrdinalIgnoreCase),
            packages,
            projectRefs,
            sources)
        {
            CheckedArithmetic = string.Equals(Prop("CheckForOverflowUnderflow"), "true", StringComparison.OrdinalIgnoreCase),
            IsExecutable = string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase) || string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase)
                || sdk.Contains("Web", StringComparison.OrdinalIgnoreCase) || sdk.Contains("Worker", StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>Central package management: Directory.Packages.props in the project directory or above (within root).</summary>
    private static Dictionary<string, string> CentralVersions(string root, string dir)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var d = dir; d is not null && d.StartsWith(Path.GetFullPath(root), StringComparison.Ordinal); d = Path.GetDirectoryName(d))
        {
            var props = Path.Combine(d, "Directory.Packages.props");
            if (!File.Exists(props))
            {
                continue;
            }

            foreach (var e in XDocument.Load(props).Descendants().Where(e => e.Name.LocalName == "PackageVersion"))
            {
                if ((string?)e.Attribute("Include") is { } id && (string?)e.Attribute("Version") is { } v)
                {
                    result.TryAdd(id.ToLowerInvariant(), v);
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
