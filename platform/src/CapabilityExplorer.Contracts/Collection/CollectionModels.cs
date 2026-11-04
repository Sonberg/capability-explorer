using System.Text.Json.Serialization;

namespace CapabilityExplorer.Contracts.Collection;

public sealed record CollectionOperationResult(
    bool IsValid,
    string? ContentSha256,
    IReadOnlyList<string> Problems,
    bool Reused);

public sealed record CollectionPlan(
    string Schema,
    string RecipeSha256,
    string ConfigSha256,
    string PolicySha256,
    IReadOnlyList<CollectionRepositoryPlan> Repositories,
    IReadOnlyList<CollectionInputPlan> Inputs);

public sealed record CollectionRepositoryPlan(
    string RepositoryId,
    string ProviderId,
    string ProducerId,
    string TenantRef,
    string RightsRef,
    string CommitSha,
    string TreeSha,
    IReadOnlyList<string> PermittedContentProfiles);

public sealed record CollectionInputPlan(
    string RepositoryId,
    string Profile,
    string Path,
    string UnitPath,
    string Sha256,
    long ByteLength,
    bool Required,
    string? ContentProfile = null);

public sealed record CollectionContent(
    string Schema,
    string Canonicalization,
    string RecipeSha256,
    string ConfigSha256,
    string PolicySha256,
    IReadOnlyList<CollectionRepositoryDescriptor> Repositories,
    IReadOnlyList<CollectionInputDescriptor> Inputs);

public sealed record CollectionRepositoryDescriptor(
    string RepositoryId,
    string ProviderId,
    string ProducerId,
    string TenantRef,
    string RightsRef,
    string CommitSha,
    string TreeSha,
    IReadOnlyList<string> PermittedContentProfiles);

public sealed record CollectionInputDescriptor(
    string RepositoryId,
    string Profile,
    string UnitPath,
    bool Required,
    string Status,
    string? ArtifactPath,
    string? Sha256,
    long ByteLength,
    string? ContentProfile);

public sealed record CollectionEnvelope(string Schema, string ContentSha256, CollectionContent Content);

public sealed record CollectionAttempt(string AttemptId, DateTimeOffset ObservedAt);

internal static class CollectionJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new CollectionIntegerConverter() },
    };
}

internal sealed class CollectionIntegerConverter : JsonConverter<long>
{
    public override long Read(ref System.Text.Json.Utf8JsonReader reader, Type type,
        System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType != System.Text.Json.JsonTokenType.Number || !reader.TryGetDecimal(out var number) ||
            decimal.Truncate(number) != number || number is < -9007199254740991m or > 9007199254740991m)
            throw new System.Text.Json.JsonException("collection numbers must be safe integers");
        return decimal.ToInt64(number);
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, long value,
        System.Text.Json.JsonSerializerOptions options) => writer.WriteNumberValue(value);
}
