using System.Text.Json;
using System.Text.Json.Serialization;
using CapabilityExplorer.Contracts.Collection;

namespace CapabilityExplorer.Explorer;

public sealed record ModelSourceStatus(string Status, string? Sha256 = null);
public sealed record ModelPage(string Schema,IReadOnlyList<ModelSummary> Items,IReadOnlyList<string> Problems,
    string Mode,string Snapshot,long Generation,IReadOnlyDictionary<string,string> Selection,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.Never)] string? NextCursor);
public sealed record ModelSummary(string Id, string Kind, string RepositoryId, string CommitSha,
    string UnitPath, string ModelKind, string Coverage, int DiagnosticCount, ModelSourceStatus Source)
{
    public static ModelSummary From(ModelProduct product)
    {
        var evidence = product.Evidence;
        var authored = product.Profile == "ce.models.v1";
        var kind = authored && evidence.GetProperty("elements").EnumerateArray().Any(e =>
            e.GetProperty("kind").GetString() == "dmn") ? "dmn" : authored ? "bpmn" : "candidate";
        return new(product.Id, authored ? "authored" : "candidate", product.RepositoryId, product.CommitSha,
            product.UnitPath, kind, evidence.GetProperty("coverage").GetProperty("status").GetString()!,
            evidence.GetProperty("diagnostics").GetArrayLength(),
            new(product.SourceStatus, evidence.GetProperty("sourceSha256").GetString()));
    }
}

public sealed record ModelFileSnapshot(IReadOnlyList<ModelProduct> Products)
{
    public static ModelFileSnapshot Empty { get; } = new([]);
    public static ModelFileSnapshot Load(IReadOnlyList<string> directories)
    {
        if (directories.Count > 8) throw new InvalidOperationException("at most eight collection directories are supported");
        var products = directories.SelectMany(directory => ValidatedModelBundle.ReadAsync(directory).GetAwaiter().GetResult().Products).ToArray();
        if (products.Length > 128 || products.Select(m => m.Id).Distinct().Count() != products.Length)
            throw new InvalidOperationException("model snapshot exceeds product bounds or contains competing duplicates");
        if (products.Sum(p => (long)p.EvidenceBytes.Length + (p.SourceEnvelopeBytes?.Length ?? 0)) > 134_217_728)
            throw new InvalidOperationException("model snapshot exceeds aggregate byte bounds");
        return new(products);
    }
}
