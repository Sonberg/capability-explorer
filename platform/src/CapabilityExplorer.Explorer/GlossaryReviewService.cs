using CapabilityExplorer.Store;
using Npgsql;

namespace CapabilityExplorer.Explorer;

public sealed record GlossaryLinkState(Guid Id, string State, string? OccurrenceId, string Reason);
public sealed record GlossaryReviewView(bool Configured, bool ReadOnly, GlossarySnapshot Snapshot, IReadOnlyList<GlossaryLinkState> LinkStates);

/// <summary>Optional local review storage. Never changes imported source artifacts.</summary>
public sealed class GlossaryReviewService(NpgsqlDataSource? dataSource) : IDisposable
{
    private readonly SemaphoreSlim _migration = new(1, 1);
    private bool _initialized;
    public bool Configured => dataSource is not null;

    public async Task<GlossaryStore> StoreAsync(CancellationToken ct)
    {
        if (dataSource is null) throw new InvalidOperationException("Glossary review requires CE_DATABASE.");
        await _migration.WaitAsync(ct);
        try
        {
            if (!_initialized)
            {
                await Migrator.MigrateAsync(dataSource, ct);
                _initialized = true;
            }
        }
        finally { _migration.Release(); }
        return new GlossaryStore(dataSource);
    }

    public async Task<GlossaryReviewView> ReadAsync(ViewIndex view, int limit, CancellationToken ct)
    {
        if (!Configured) return new(false, true, new([], [], [], limit, false, false, false), []);
        var snapshot = await (await StoreAsync(ct)).GetSnapshotAsync(limit, ct);
        return new(true, false, snapshot, Resolve(view, snapshot.Links));
    }

    public static IReadOnlyList<GlossaryLinkState> Resolve(ViewIndex view, IReadOnlyList<GlossaryLink> links)
    {
        var occurrences = Glossary.Build(view).SelectMany(c => c.Occurrences).ToList();
        return links.Select(link =>
        {
            if (!view.Selection.ContainsKey(link.RepositoryId))
                return new GlossaryLinkState(link.Id, "out-of-scope", null, "Repository is outside the selected scope.");
            var matches = occurrences.Where(o => o.RepositoryId == link.RepositoryId && o.UnitPath == link.UnitPath && o.Key == link.SymbolKey).ToList();
            if (matches.Count == 0) return new(link.Id, "orphaned", null, "The selected analysis has no matching glossary occurrence; no name rebinding was attempted.");
            var exact = matches.Where(o => o.Id == link.Observed.OccurrenceId).ToList();
            if (exact.Count == 1)
            {
                var occurrence = exact[0];
                var artifact = view.Artifacts.SingleOrDefault(a => a.RepositoryId == occurrence.RepositoryId && a.CommitSha == occurrence.CommitSha && a.Artifact.Header.Unit.Path == occurrence.UnitPath);
                if (occurrence.CommitSha == link.Observed.CommitSha && artifact?.Artifact.ContentSha256 == link.Observed.ArtifactContentSha256)
                    return new(link.Id, "current", occurrence.Id, "Exact reviewed occurrence and artifact content are selected.");
                return new(link.Id, "stale", occurrence.Id, "Analysis content or revision changed; explicit re-review is required.");
            }
            return matches.Count > 1
                ? new(link.Id, "ambiguous", null, "Multiple current occurrences match the logical reference; choose one explicitly.")
                : new(link.Id, "stale", matches[0].Id, "The reviewed occurrence is not current; explicit re-review is required.");
        }).ToList();
    }

    public static GlossaryObservedOccurrence Observe(ViewIndex view, string occurrenceId)
    {
        var matches = Glossary.Build(view).SelectMany(c => c.Occurrences).Where(o => o.Id == occurrenceId).ToList();
        if (matches.Count != 1) throw new ArgumentException("Select exactly one occurrence from the current capability and revision selection.", nameof(occurrenceId));
        var occurrence = matches[0];
        var artifact = view.Artifacts.Single(a => a.RepositoryId == occurrence.RepositoryId && a.CommitSha == occurrence.CommitSha && a.Artifact.Header.Unit.Path == occurrence.UnitPath);
        return new(occurrence.RepositoryId, occurrence.CommitSha, occurrence.UnitPath, occurrence.Key, artifact.Artifact.ContentSha256, occurrence.Id);
    }

    public void Dispose() { _migration.Dispose(); GC.SuppressFinalize(this); }
}
