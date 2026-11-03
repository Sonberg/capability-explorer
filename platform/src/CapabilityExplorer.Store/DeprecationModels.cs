namespace CapabilityExplorer.Store;

public enum DeprecationStatus
{
    Deprecated,
    Obsolete,
}

public sealed record DeprecationMark(
    string Key,
    DeprecationStatus Status,
    string Note,
    DateTimeOffset MarkedAt,
    long Version);

public sealed record DeprecationUpsert(
    string Key,
    DeprecationStatus Status,
    string Note,
    long? ExpectedVersion = null);

public sealed record DeprecationImportIssue(int Entry, string? Key, string Reason);

public sealed record DeprecationImportReport(
    int Imported,
    int Unchanged,
    IReadOnlyList<DeprecationImportIssue> Issues);

public interface IDeprecationRepository
{
    Task<IReadOnlyList<DeprecationMark>> ListAsync(CancellationToken cancellationToken = default);
    Task<DeprecationMark> UpsertAsync(DeprecationUpsert value, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, long expectedVersion, CancellationToken cancellationToken = default);
    Task<DeprecationImportReport> ImportLegacyAsync(Stream json, CancellationToken cancellationToken = default);
}

public sealed class DeprecationValidationException(string message) : ArgumentException(message);

public sealed class DeprecationVersionConflictException(string key, long? expectedVersion)
    : Exception(expectedVersion is null
        ? $"Deprecation mark '{key}' already exists."
        : $"Deprecation mark '{key}' does not have expected version {expectedVersion}.")
{
    public string Key { get; } = key;
    public long? ExpectedVersion { get; } = expectedVersion;
}

public static class DeprecationStatusValues
{
    public static bool TryParse(string? value, out DeprecationStatus status)
    {
        status = value switch
        {
            "deprecated" => DeprecationStatus.Deprecated,
            "obsolete" => DeprecationStatus.Obsolete,
            _ => (DeprecationStatus)(-1),
        };
        return (int)status >= 0;
    }

    public static string ToWireValue(DeprecationStatus status) => status switch
    {
        DeprecationStatus.Deprecated => "deprecated",
        DeprecationStatus.Obsolete => "obsolete",
        _ => throw new DeprecationValidationException("Status must be 'deprecated' or 'obsolete'."),
    };

    internal static string ToDatabaseValue(DeprecationStatus status) => ToWireValue(status);

    internal static DeprecationStatus FromDatabaseValue(string value) =>
        TryParse(value, out var status) ? status : throw new InvalidOperationException("Database contains an invalid deprecation status.");
}

internal static class DeprecationValidation
{
    public static void Key(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Contains('\0', StringComparison.Ordinal))
            throw new DeprecationValidationException("Key must contain 1 to 4096 non-whitespace characters.");
    }

    public static void Note(string? value)
    {
        if (value is null || value.Length > 4096 || value.Contains('\0', StringComparison.Ordinal))
            throw new DeprecationValidationException("Note must be present and no longer than 4096 characters.");
    }

    public static void Upsert(DeprecationUpsert value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Key(value.Key);
        Note(value.Note);
        _ = DeprecationStatusValues.ToWireValue(value.Status);
        if (value.ExpectedVersion is <= 0)
            throw new DeprecationValidationException("Expected version must be positive.");
    }
}
