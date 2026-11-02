using CapabilityExplorer.Capabilities;

namespace CapabilityExplorer.Explorer;

/// <summary>Only small trusted configuration is loaded. Artifact paths are never enumerated.</summary>
public sealed class DatabaseExplorerConfiguration
{
    private readonly ExplorerOptions _options;
    public DatabaseExplorerConfiguration(ExplorerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        using var definitions = OpenDefinitions();
        Git = new GitSource(id =>
        {
            using var current = OpenDefinitions();
            return current.FindRepository(id) is { } repo ? SourcePathResolver.GitDirectory(repo, options.Workspace)
                : options.RepositoriesDirectory is not null && GitSource.IsSafeSegment(id) ? Path.Combine(options.RepositoriesDirectory, id) : null;
        });
    }

    public CapabilityStore OpenDefinitions()
    {
        if (new FileInfo(_options.CapabilitiesFile) is { Exists: true, Length: > 1048576 })
            throw new InvalidOperationException("capability configuration exceeds one MiB");
        var store = CapabilityStore.Open(_options.CapabilitiesFile, Path.Combine(_options.Workspace, "snapshots.json"), loadSnapshots: false);
        if (store.Capabilities.Count <= 100 && store.Capabilities.All(c => c.Repositories.Count <= 1000)) return store;
        store.Dispose();
        throw new InvalidOperationException("capability configuration exceeds supported count limits");
    }
    public GitSource Git { get; }
}
