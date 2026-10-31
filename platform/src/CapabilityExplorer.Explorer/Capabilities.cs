using CapabilityExplorer.Capabilities;

namespace CapabilityExplorer.Explorer;

public sealed record RepositoryStatus(
    string Id,
    string Location,
    string Kind,
    string Ref,
    bool Available,
    string? LockedSha,
    string? LockedSubject,
    DateTimeOffset? LockedAt,
    string? Error,
    bool Analysed,
    IReadOnlyList<string> AnalysedRevisions);

public sealed record CapabilityStatus(string Id, string Name, string? Description, DateTimeOffset? CheckedAt, IReadOnlyList<RepositoryStatus> Repositories)
{
    public int Analysed => Repositories.Count(r => r.Analysed);
}

public sealed record CreateCapabilityRequest(string Name, string? Description, string Repositories);

/// <summary>Capabilities as the explorer sees them: definitions, their last check, and which revisions have artifacts.</summary>
public sealed class CapabilityService(CapabilityStore store, SourceSync sync, ExplorerIndex index)
{
    public CapabilityStore Store => store;

    public SourceSync Sync => sync;

    public IReadOnlyList<CapabilityStatus> List() => store.Capabilities.Select(Status).ToList();

    public CapabilityStatus? Get(string id) => store.Find(id) is { } c ? Status(c) : null;

    public ViewScope? Scope(string? capabilityId)
    {
        if (string.IsNullOrEmpty(capabilityId) || store.Find(capabilityId) is not { } capability)
        {
            return null;
        }

        var preferred = store.Snapshot(capabilityId)?.Repositories
            .Where(r => r.CommitSha is not null && index.FindRevision(r.Id, r.CommitSha) is not null)
            .ToDictionary(r => r.Id, r => r.CommitSha!, StringComparer.Ordinal) ?? [];
        return new ViewScope(capability.Id, capability.Repositories.Select(r => r.Id).ToHashSet(StringComparer.Ordinal), preferred);
    }

    public async Task<CapabilityStatus> CheckAsync(string id, CancellationToken cancellationToken)
    {
        var capability = store.Find(id) ?? throw new KeyNotFoundException(id);
        await store.SaveSnapshotAsync(await sync.CheckAsync(capability, cancellationToken), cancellationToken);
        return Status(capability);
    }

    private CapabilityStatus Status(CapabilityDefinition c)
    {
        var snapshot = store.Snapshot(c.Id);
        return new CapabilityStatus(c.Id, c.Name, c.Description, snapshot?.CheckedAt, c.Repositories.Select(r =>
        {
            var locked = snapshot?.Repositories.FirstOrDefault(l => l.Id == r.Id);
            var revisions = index.Revisions.Where(x => x.RepositoryId == r.Id).ToList();
            var dir = sync.GitDirectory(r);
            return new RepositoryStatus(
                r.Id, r.Location, r.Kind == SourceKind.GitHub ? "github" : "local", r.Ref,
                Directory.Exists(dir),
                locked?.CommitSha, locked?.Subject, locked?.CommitTime, locked?.Error,
                locked?.CommitSha is { } sha && revisions.Any(x => x.CommitSha == sha),
                revisions.Select(x => x.Label).ToList());
        }).ToList());
    }
}
