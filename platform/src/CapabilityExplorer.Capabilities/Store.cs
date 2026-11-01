using System.Text.Json;
using System.Text.Json.Serialization;

namespace CapabilityExplorer.Capabilities;

public sealed class CapabilityConflictException(string message) : Exception(message);

/// <summary>
/// Capability definitions (configuration, fit to commit) and their latest snapshots (generated state, kept in the
/// workspace) as two JSON files. Writes are atomic (temp file + rename). Repository ids are global: artifacts are
/// keyed by them.
/// </summary>
public sealed class CapabilityStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _definitionsPath;
    private readonly string _snapshotsPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Document _document;

    private CapabilityStore(string definitionsPath, string snapshotsPath, Document document)
    {
        _definitionsPath = definitionsPath;
        _snapshotsPath = snapshotsPath;
        _document = document;
    }

    public static CapabilityStore Open(string definitionsPath, string snapshotsPath, bool loadSnapshots = true)
    {
        var definitions = File.Exists(definitionsPath) ? JsonSerializer.Deserialize<Definitions>(File.ReadAllText(definitionsPath), Json) ?? new Definitions() : new Definitions();
        var snapshots = loadSnapshots && File.Exists(snapshotsPath) ? JsonSerializer.Deserialize<Dictionary<string, CapabilitySnapshot>>(File.ReadAllText(snapshotsPath), Json) ?? [] : [];
        return new CapabilityStore(definitionsPath, snapshotsPath, new Document { Capabilities = definitions.Capabilities, Snapshots = snapshots });
    }

    public IReadOnlyList<CapabilityDefinition> Capabilities => _document.Capabilities;

    public void Dispose() => _gate.Dispose();

    public CapabilitySnapshot? Snapshot(string capabilityId) => _document.Snapshots.GetValueOrDefault(capabilityId);

    public CapabilityDefinition? Find(string id) => _document.Capabilities.FirstOrDefault(c => c.Id == id);

    /// <summary>Where a repository id lives, across all capabilities.</summary>
    public RepositorySource? FindRepository(string repositoryId) =>
        _document.Capabilities.SelectMany(c => c.Repositories).FirstOrDefault(r => r.Id == repositoryId);

    public async Task<CapabilityDefinition> AddAsync(string name, string? description, IReadOnlyList<RepositorySource> repositories, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(repositories);
        if (repositories.Count == 0)
        {
            throw new CapabilityConflictException("a capability needs at least one repository");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var r in repositories)
            {
                if (FindRepository(r.Id) is { } existing && existing.Location != r.Location)
                {
                    throw new CapabilityConflictException($"repository id '{r.Id}' already refers to {existing.Location}; give this one another id with id=location");
                }
            }

            var baseId = SourceParser.CapabilityId(name);
            var id = baseId;
            for (var i = 2; _document.Capabilities.Any(c => c.Id == id); i++)
            {
                id = $"{baseId}-{i}";
            }

            var definition = new CapabilityDefinition(id, name.Trim(), string.IsNullOrWhiteSpace(description) ? null : description.Trim(), repositories);
            await SaveAsync(_document with { Capabilities = [.. _document.Capabilities, definition] }, cancellationToken);
            return definition;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Find(id) is null)
            {
                return false;
            }

            var snapshots = new Dictionary<string, CapabilitySnapshot>(_document.Snapshots);
            snapshots.Remove(id);
            await SaveAsync(_document with { Capabilities = _document.Capabilities.Where(c => c.Id != id).ToList(), Snapshots = snapshots }, cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveSnapshotAsync(CapabilitySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var snapshots = new Dictionary<string, CapabilitySnapshot>(_document.Snapshots) { [snapshot.CapabilityId] = snapshot };
            await SaveAsync(_document with { Snapshots = snapshots }, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveAsync(Document document, CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(document.Capabilities, _document.Capabilities))
        {
            await WriteAtomicAsync(_definitionsPath, JsonSerializer.Serialize(new Definitions { Capabilities = document.Capabilities }, Json), cancellationToken);
        }

        if (!ReferenceEquals(document.Snapshots, _document.Snapshots))
        {
            await WriteAtomicAsync(_snapshotsPath, JsonSerializer.Serialize(document.Snapshots, Json), cancellationToken);
        }

        _document = document;
    }

    private static async Task WriteAtomicAsync(string path, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(temp, content + "\n", cancellationToken);
        File.Move(temp, path, overwrite: true);
    }

    private sealed record Definitions
    {
        public IReadOnlyList<CapabilityDefinition> Capabilities { get; init; } = [];
    }

    private sealed record Document
    {
        public IReadOnlyList<CapabilityDefinition> Capabilities { get; init; } = [];

        public IReadOnlyDictionary<string, CapabilitySnapshot> Snapshots { get; init; } = new Dictionary<string, CapabilitySnapshot>();
    }
}
