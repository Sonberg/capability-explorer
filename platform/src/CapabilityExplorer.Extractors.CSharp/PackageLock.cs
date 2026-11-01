using System.Text.Json;

namespace CapabilityExplorer.Extractors.CSharp;

public sealed record LockedPackage(string Id, string Version);

/// <summary>
/// The exact package versions one analysis uses: one set per project, written by the packages step next to the
/// export and read by the offline extract step. Extraction uses only these versions, so what the shared package
/// cache happens to hold can no longer change the result.
/// </summary>
public sealed class PackageLock
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly SortedDictionary<string, IReadOnlyList<LockedPackage>> _projects = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, IReadOnlyList<LockedPackage>> Projects => _projects;

    public void Set(string projectPath, IEnumerable<LockedPackage> packages)
    {
        var set = packages.ToList();
        if (set.GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase).Any(g => g.Select(p => p.Version).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
        {
            throw new InvalidDataException($"Conflicting package versions in lock for {projectPath}; a lock must select one exact version per package");
        }

        _projects[projectPath] = set
            .DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Version, StringComparer.Ordinal)
            .ToList();
    }

    public IReadOnlyList<LockedPackage> For(string projectPath) => _projects.GetValueOrDefault(projectPath) ?? [];

    /// <summary>Canonical JSON: sorted projects, sorted packages, fixed layout, so equal locks are equal bytes.</summary>
    public string ToJson()
    {
        var doc = new SortedDictionary<string, object>(StringComparer.Ordinal)
        {
            ["version"] = CurrentVersion,
            ["projects"] = _projects.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Select(p => new SortedDictionary<string, string>(StringComparer.Ordinal) { ["id"] = p.Id, ["version"] = p.Version }).ToList(),
                StringComparer.Ordinal),
        };
        return JsonSerializer.Serialize(doc, Indented) + "\n";
    }

    public static PackageLock Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.GetProperty("version").GetInt32() != CurrentVersion)
        {
            throw new InvalidDataException("unsupported package lock version");
        }

        var result = new PackageLock();
        foreach (var project in doc.RootElement.GetProperty("projects").EnumerateObject())
        {
            result.Set(project.Name, project.Value.EnumerateArray()
                .Select(p => new LockedPackage(p.GetProperty("id").GetString()!, p.GetProperty("version").GetString()!)));
        }

        return result;
    }

    public static PackageLock Load(string path) => Parse(File.ReadAllText(path));
}
