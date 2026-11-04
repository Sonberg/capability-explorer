using System.Security.Cryptography;
using System.Text.Json;

namespace CapabilityExplorer.Capabilities;

public sealed record CollectionRepositoryInput(
    string RepositoryId, string CommitSha, string TreeSha, string InputsSha256, string DependenciesSha256);

public sealed record CollectionInputVector(
    IReadOnlyList<CollectionRepositoryInput> Repositories,
    string RecipeSha256, string ConfigurationSha256, string EnrollmentSha256,
    string RightsSha256, string ProfilesSha256, bool EnumerationComplete);

public sealed record CollectionFreshnessResult(string Status, IReadOnlyList<string> Reasons, string? InputVectorSha256);

public static class CollectionFreshness
{
    public static CollectionFreshnessResult Evaluate(CollectionInputVector previous, CollectionInputVector observed)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(observed);
        var reasons = Validate(previous, "previous").Concat(Validate(observed, "observed")).ToArray();
        if (reasons.Length > 0) return new("unknown", reasons, null);
        var before = Dimensions(previous);
        var after = Dimensions(observed);
        var changes = before.Keys.Union(after.Keys, StringComparer.Ordinal)
            .Where(key => before.GetValueOrDefault(key) != after.GetValueOrDefault(key))
            .Order(StringComparer.Ordinal).Select(key => $"Changed input: {key}").ToArray();
        var digest = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(after)));
        return new(changes.Length == 0 ? "current" : "stale", changes, digest);
    }

    private static SortedDictionary<string, string> Dimensions(CollectionInputVector vector)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["schema"] = "ce.collection.inputs.v1",
            ["recipe"] = vector.RecipeSha256,
            ["configuration"] = vector.ConfigurationSha256,
            ["enrollment"] = vector.EnrollmentSha256,
            ["rights"] = vector.RightsSha256,
            ["profiles"] = vector.ProfilesSha256
        };
        foreach (var repo in vector.Repositories.OrderBy(repo => repo.RepositoryId, StringComparer.Ordinal))
            result[$"repository:{repo.RepositoryId}"] = JsonSerializer.Serialize(new
            {
                repo.CommitSha, repo.TreeSha, repo.InputsSha256, repo.DependenciesSha256
            });
        return result;
    }

    private static IEnumerable<string> Validate(CollectionInputVector vector, string role)
    {
        if (!vector.EnumerationComplete) yield return $"{role}: input enumeration is incomplete";
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["recipe"] = vector.RecipeSha256,
            ["configuration"] = vector.ConfigurationSha256,
            ["enrollment"] = vector.EnrollmentSha256,
            ["rights"] = vector.RightsSha256,
            ["profiles"] = vector.ProfilesSha256
        };
        foreach (var (key, value) in hashes)
            if (!IsHash(value, 64)) yield return $"{role}: missing or invalid {key} SHA-256";
        if (vector.Repositories is not { Count: > 0 and <= 256 })
        {
            yield return $"{role}: expected 1..256 repository inputs";
            yield break;
        }
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var repo in vector.Repositories)
        {
            if (repo is null)
            {
                yield return $"{role}: null repository input";
                continue;
            }
            if (string.IsNullOrWhiteSpace(repo.RepositoryId) || repo.RepositoryId.Length > 256 ||
                repo.RepositoryId.Any(char.IsControl) || !identities.Add(repo.RepositoryId))
                yield return $"{role}: missing, invalid or duplicate repository identity";
            if (!IsGitHash(repo.CommitSha) || !IsGitHash(repo.TreeSha) ||
                !IsHash(repo.InputsSha256, 64) || !IsHash(repo.DependenciesSha256, 64))
                yield return $"{role}: incomplete repository revision/input/dependency hashes";
        }
    }

    private static bool IsGitHash(string? value) => IsHash(value, 40) || IsHash(value, 64);
    private static bool IsHash(string? value, int length) =>
        value?.Length == length && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
