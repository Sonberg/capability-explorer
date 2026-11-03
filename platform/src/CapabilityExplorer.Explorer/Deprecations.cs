using CapabilityExplorer.Store;

namespace CapabilityExplorer.Explorer;

/// <param name="Key">"group|http|Klarna" for a whole integration, otherwise an endpoint, request, channel or table key.</param>
/// <param name="Status">"deprecated" or "obsolete".</param>
public sealed record MarkDto(
    string Key,
    string Status,
    string Note,
    DateTimeOffset MarkedAt,
    long Version,
    Removal? Removal);

/// <param name="RemovedIn">First revision in the pinned query scope without the key, after a revision that had it.</param>
public sealed record Removal(string RepositoryId, string LastSeen, string RemovedIn, string? Subject);

public sealed class DeprecationService(IDeprecationRepository repository)
{
    public static readonly string[] Statuses = ["deprecated", "obsolete"];

    public async Task<IReadOnlyList<MarkDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var marks = await repository.ListAsync(cancellationToken);
        return marks.Select(ToDto).ToList();
    }

    public async Task<MarkDto> UpsertAsync(
        string key,
        string status,
        string note,
        long? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        if (!DeprecationStatusValues.TryParse(status, out var parsed))
            throw new DeprecationValidationException("Status must be 'deprecated' or 'obsolete'.");
        var updated = await repository.UpsertAsync(new(key, parsed, note, expectedVersion), cancellationToken);
        return ToDto(updated);
    }

    public Task DeleteAsync(string key, long expectedVersion, CancellationToken cancellationToken = default) =>
        repository.DeleteAsync(key, expectedVersion, cancellationToken);

    public Task<DeprecationImportReport> ImportLegacyAsync(Stream json, CancellationToken cancellationToken = default) =>
        repository.ImportLegacyAsync(json, cancellationToken);

    public IReadOnlyList<MarkDto> WithRemovals(
        IReadOnlyList<MarkDto> marks,
        IReadOnlyDictionary<string, Removal> removals)
    {
        ArgumentNullException.ThrowIfNull(marks);
        ArgumentNullException.ThrowIfNull(removals);
        return marks.Select(mark => mark with
        {
            Removal = removals.GetValueOrDefault(mark.Key),
        }).ToList();
    }

    private static MarkDto ToDto(DeprecationMark mark) =>
        new(mark.Key, DeprecationStatusValues.ToWireValue(mark.Status), mark.Note, mark.MarkedAt, mark.Version, null);
}
