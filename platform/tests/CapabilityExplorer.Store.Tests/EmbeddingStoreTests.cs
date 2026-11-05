using CapabilityExplorer.Store;
using Xunit;

namespace CapabilityExplorer.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class EmbeddingStoreTests(PostgresFixture db)
{
    private const string Model = "test:fake";
    private static readonly string[] Kinds = ["Method", "Function", "Endpoint", "Routine"];

    /// <summary>Stands in for the model: the vector is a bag of the text's letters, so equal texts are identical vectors.</summary>
    private static float[] Fake(string text)
    {
        var v = new float[26];
        foreach (var c in text.ToLowerInvariant().Where(char.IsAsciiLetterLower))
        {
            v[c - 'a']++;
        }

        return v;
    }

    [Fact]
    public async Task Legacy_cache_rejects_incomplete_provider_batches_without_claiming_success()
    {
        await new ArtifactImporter(db.DataSource).ImportAsync(Fixtures.Golden("web-ts/v1-base/_fixture_web.jsonl"));
        var store = new EmbeddingStore(db.DataSource);
        var model = "malformed-" + Guid.NewGuid().ToString("N");
        var pending = await store.PendingAsync(model);
        Assert.NotEmpty(pending);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.EmbedPendingAsync(model,
            (texts, _) => Task.FromResult(texts.Skip(1).Select(Fake).ToArray())));
        Assert.Equal(pending.Count, (await store.PendingAsync(model)).Count);
    }

    [Fact]
    public async Task Embeds_functions_and_calculations_once_and_finds_the_nearest()
    {
        var importer = new ArtifactImporter(db.DataSource);
        await importer.ImportAsync(Fixtures.Golden("web-ts/v1-base/_fixture_web.jsonl"));
        var store = new EmbeddingStore(db.DataSource);

        var pending = await store.PendingAsync(Model);
        Assert.NotEmpty(pending);
        Assert.Contains(pending, p => p.SymbolKey.EndsWith("averagePerActiveWeek#value", StringComparison.Ordinal) && p.Text.Contains("formula", StringComparison.Ordinal));
        Assert.All(pending, p => Assert.True(Kinds.Contains(p.NodeKind) || p.Text.Contains("formula", StringComparison.Ordinal), p.NodeKind));
        Assert.DoesNotContain(pending, p => p.Text.Contains("web-ts", StringComparison.Ordinal)); // where it lives is not what it does

        var embedded = await store.EmbedPendingAsync(Model, (texts, _) => Task.FromResult(texts.Select(Fake).ToArray()), batchSize: 3);
        Assert.Equal(pending.Count, embedded);
        Assert.Empty(await store.PendingAsync(Model));
        Assert.Equal(0, await store.EmbedPendingAsync(Model, (_, _) => throw new InvalidOperationException("nothing should be embedded again")));

        var first = pending[0];
        var nearest = await store.NearestAsync(Fake(first.Text), Model, 3);
        Assert.Equal(first.SymbolKey, nearest[0].SymbolKey);
        Assert.Equal(1.0, nearest[0].Score, 6);

        var similar = await store.SimilarAsync(first.SymbolKey, Model, 5);
        Assert.DoesNotContain(similar, s => s.SymbolKey == first.SymbolKey);
        Assert.Equal(similar.OrderByDescending(s => s.Score).Select(s => s.SymbolKey), similar.Select(s => s.SymbolKey));
        Assert.Empty(await store.SimilarAsync(first.SymbolKey, "test:other-model", 5));
    }
}
