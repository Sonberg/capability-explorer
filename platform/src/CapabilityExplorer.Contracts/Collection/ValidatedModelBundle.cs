using System.Text;
using System.Text.Json;

namespace CapabilityExplorer.Contracts.Collection;

public sealed record ModelProduct(string Id, string Profile, string RepositoryId, string CommitSha,
    string UnitPath, string ArtifactSha256, byte[] EvidenceBytes, string CollectionIdentity,
    string SourceStatus, byte[]? SourceEnvelopeBytes)
{
    public JsonElement Evidence
    {
        get { using var document=JsonDocument.Parse(EvidenceBytes); return document.RootElement.Clone(); }
    }
    public JsonElement? Source
    {
        get
        {
            if(SourceEnvelopeBytes is null) return null;
            using var document=JsonDocument.Parse(SourceEnvelopeBytes);return document.RootElement.Clone();
        }
    }
}

public sealed record ValidatedModelBundle(string Identity, IReadOnlyList<ModelProduct> Products)
{
    public static async Task<ValidatedModelBundle> ReadAsync(string directory, CancellationToken ct = default)
    {
        var validation = await CollectionExporter.ValidateAsync(directory, ct);
        if (!validation.IsValid) throw new FormatException($"model collection invalid: {string.Join("; ", validation.Problems)}");
        var root = Path.GetFullPath(directory);
        var envelope = JsonSerializer.Deserialize<CollectionEnvelope>(
            await CollectionRules.ReadBoundedAsync(Path.Combine(root, "collection.json"), CollectionRules.MaxPlanBytes, ct),
            CollectionJson.Options)!;
        if (envelope.ContentSha256 != validation.ContentSha256) throw new FormatException("collection descriptor changed while loading");
        var products = new List<ModelProduct>();
        long remaining = CollectionRules.MaxTotalBytes;
        var bytesByInput = new Dictionary<CollectionInputDescriptor, byte[]>();
        foreach (var input in envelope.Content.Inputs.Where(i => i.Status == "included"))
        {
            var path = Path.Combine(root, input.ArtifactPath!);
            if (!CollectionExporter.HasSafeExistingPath(path, true)) throw new FormatException("unsafe collection path");
            var bytes = await CollectionRules.ReadBoundedAsync(path, Math.Min(CollectionRules.MaxInputBytes, remaining), ct);
            remaining -= bytes.Length;
            if (bytes.Length != input.ByteLength || ModelSource.Digest(bytes) != input.Sha256)
                throw new FormatException("collection changed while loading");
            bytesByInput.Add(input, bytes);
        }
        foreach (var (input, bytes) in bytesByInput)
        {
            if (input.Profile is not ("ce.models.v1" or "ce.candidates.v1")) continue;
            var repo = envelope.Content.Repositories.Single(r => r.RepositoryId == input.RepositoryId);
            var sourceInput = envelope.Content.Inputs.SingleOrDefault(i =>
                i.Profile == "ce.model-source.v1" && i.RepositoryId == input.RepositoryId && i.UnitPath == input.UnitPath);
            byte[]? attachment = sourceInput is not null && bytesByInput.TryGetValue(sourceInput, out var value) ? value : null;
            if (attachment is not null)
            {
                using var source = JsonDocument.Parse(attachment);
                _ = ModelSource.Decode(source.RootElement);
                ModelSource.CheckCompanion(source.RootElement, bytes);
            }
            var status = !repo.PermittedContentProfiles.Contains("model-content") ? "denied"
                : attachment is null ? "missing" : "available";
            products.Add(new ModelProduct(StableId(input.Profile, repo.RepositoryId, repo.CommitSha, input.UnitPath, input.Sha256!),
                input.Profile, repo.RepositoryId, repo.CommitSha, input.UnitPath, input.Sha256!, bytes,
                envelope.ContentSha256, status, attachment));
        }
        return new ValidatedModelBundle(envelope.ContentSha256, products);
    }

    public static string StableId(string profile, string repo, string commit, string path, string digest) =>
        "model:" + ModelSource.Digest(Encoding.UTF8.GetBytes("ce.model-view.v1\0" +
            JsonSerializer.Serialize(new[] { profile, repo, commit, path, digest })));
}
