using System.Text.RegularExpressions;

namespace CapabilityExplorer.Store;

internal static partial class GlossaryValidation
{
    public static void Term(GlossaryTermWrite value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Id(value.Id);
        Text(value.Name, 200, nameof(value.Name));
        Text(value.Definition, 10000, nameof(value.Definition));
        Optional(value.Group, 200, nameof(value.Group));
        Reviewer(value.Reviewer);
    }

    public static void Link(GlossaryLinkWrite value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Id(value.Id);
        Id(value.TermId);
        Text(value.RepositoryId, 100, nameof(value.RepositoryId));
        if (!RepositoryPattern().IsMatch(value.RepositoryId))
            throw new ArgumentException("Invalid repository identifier.", nameof(value));
        Path(value.UnitPath);
        Text(value.SymbolKey, 2000, nameof(value.SymbolKey));
        if (value.Role is not ("same" or "alias" or "derived_from"))
            throw new ArgumentException("Role must be same, alias or derived_from.", nameof(value));
        Optional(value.Note, 10000, nameof(value.Note));
        Reviewer(value.Reviewer);
        ArgumentNullException.ThrowIfNull(value.Observed);
        if (value.Observed.RepositoryId != value.RepositoryId || value.Observed.UnitPath != value.UnitPath || value.Observed.SymbolKey != value.SymbolKey)
            throw new ArgumentException("Observed occurrence must match the logical symbol reference.", nameof(value));
        if (!Sha40Pattern().IsMatch(value.Observed.CommitSha ?? "") || !Sha64Pattern().IsMatch(value.Observed.ArtifactContentSha256 ?? ""))
            throw new ArgumentException("Observed revision and artifact content hash must be lowercase SHA hashes.", nameof(value));
        Text(value.Observed.OccurrenceId, 2000, nameof(value.Observed.OccurrenceId));
    }

    public static void Issue(GlossaryIssueWrite value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Id(value.Id);
        Id(value.TermId);
        Text(value.Summary, 500, nameof(value.Summary));
        Optional(value.Note, 10000, nameof(value.Note));
        Reviewer(value.Reviewer);
        if (value.Kind is not ("type" or "derivation" or "naming" or "other"))
            throw new ArgumentException("Invalid issue kind.", nameof(value));
        if (value.Status is not ("open" or "resolved"))
            throw new ArgumentException("Issue status must be open or resolved.", nameof(value));
    }

    public static void Id(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("An identifier must not be empty.", nameof(id));
    }

    public static void Reviewer(string reviewer) => Text(reviewer, 200, nameof(reviewer));

    public static void Version(long version, bool createAllowed)
    {
        if (version < (createAllowed ? 0 : 1) || version == long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(version), "Expected version is outside the supported range.");
    }

    public static void Limit(int limit)
    {
        if (limit is < 1 or > 2000) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 2000.");
    }

    private static void Text(string value, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Contains('\0'))
            throw new ArgumentException($"{name} must contain 1–{maximum} characters and no NUL.", name);
    }

    private static void Optional(string? value, int maximum, string name)
    {
        if (value is not null && (value.Length > maximum || value.Contains('\0')))
            throw new ArgumentException($"{name} exceeds its limit or contains NUL.", name);
    }

    private static void Path(string value)
    {
        Text(value, 2000, nameof(value));
        if (value.StartsWith('/') || value.Contains('\\') || value.Split('/').Any(s => s is "" or "." or ".."))
            throw new ArgumentException("Unit path must be a repository-relative path without traversal.", nameof(value));
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPattern();
    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha40Pattern();
    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha64Pattern();
}
