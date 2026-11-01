using System.Xml.Linq;
using NuGet.Frameworks;

namespace CapabilityExplorer.Extractors.CSharp;

/// <summary>NuGet's compatibility reducer, shared by compile assets and nuspec dependency groups.</summary>
internal static class FrameworkSelection
{
    public static NuGetFramework? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('$', StringComparison.Ordinal) || value.Contains(';', StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var framework = NuGetFramework.Parse(value.Trim());
            return framework.IsUnsupported || framework.IsAny || framework.IsAgnostic ? null : framework;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static string? Nearest(string? target, IEnumerable<string> candidates)
    {
        if (Parse(target) is not { } framework)
        {
            return null;
        }

        var parsed = candidates.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(name => (Name: name, Framework: Parse(name))).Where(p => p.Framework is not null).ToList();
        var nearest = new FrameworkReducer().GetNearest(framework, parsed.Select(p => p.Framework!));
        return nearest is null ? null : parsed.First(p => p.Framework!.Equals(nearest)).Name;
    }

    public static IReadOnlyList<XElement> DependencyElements(string root, string? target)
    {
        var nuspec = Directory.EnumerateFiles(root, "*.nuspec").Order(StringComparer.Ordinal).FirstOrDefault();
        if (nuspec is null || Parse(target) is null)
        {
            return [];
        }

        var dependencies = XDocument.Load(nuspec).Descendants().FirstOrDefault(e => e.Name.LocalName == "dependencies");
        if (dependencies is null)
        {
            return [];
        }

        var groups = dependencies.Elements().Where(e => e.Name.LocalName == "group").ToList();
        if (groups.Count == 0)
        {
            return dependencies.Elements().Where(e => e.Name.LocalName == "dependency").ToList();
        }

        var name = Nearest(target, groups.Select(g => (string?)g.Attribute("targetFramework")).OfType<string>());
        var group = name is null
            ? groups.FirstOrDefault(g => string.IsNullOrWhiteSpace((string?)g.Attribute("targetFramework")))
            : groups.First(g => (string?)g.Attribute("targetFramework") == name);
        return group?.Elements().Where(e => e.Name.LocalName == "dependency").ToList() ?? [];
    }

    public static bool IncompatibleDependencyGroups(string root, string? target)
    {
        var nuspec = Directory.EnumerateFiles(root, "*.nuspec").Order(StringComparer.Ordinal).FirstOrDefault();
        if (nuspec is null) return false;
        var groups = XDocument.Load(nuspec).Descendants().Where(e => e.Name.LocalName == "dependencies")
            .Elements().Where(e => e.Name.LocalName == "group").ToList();
        return groups.Count > 0 && !groups.Any(g => string.IsNullOrWhiteSpace((string?)g.Attribute("targetFramework")))
            && Nearest(target, groups.Select(g => (string?)g.Attribute("targetFramework")).OfType<string>()) is null;
    }
}

public sealed record ResolutionIssue(string Code, string Message);
