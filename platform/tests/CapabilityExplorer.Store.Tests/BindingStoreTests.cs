using CapabilityExplorer.Binding;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Store;
using Xunit;

namespace CapabilityExplorer.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class BindingStoreTests(PostgresFixture db)
{
    private const string Map = """{"version":1,"bindings":[{"hint":"@unbound:pricing-node/@fixture/pricing:env=FORECAST_API_URL","target":"forecast-cs/Forecast.Api","note":"x"}]}""";

    private async Task<(long, Artifact)> Import(string file)
    {
        var text = Fixtures.Golden(file);
        var result = await new ArtifactImporter(db.DataSource).ImportAsync(text);
        return (result.ArtifactId, ArtifactValidator.Validate(text).Artifact!);
    }

    private async Task<long> Scalar(string sql)
    {
        await using var command = db.DataSource.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Saves_bound_and_broken_edges_without_touching_extractor_rows()
    {
        var pricing = await Import("pricing-node/v1-base/_fixture_pricing.jsonl");
        var v1 = await Import("forecast-cs/v1-base/Forecast.Api.jsonl");
        var v2 = await Import("forecast-cs/v2-contract/Forecast.Api.jsonl");
        var edgesBefore = await db.CountAsync("edge");
        var store = new BindingStore(db.DataSource);
        var file = BindingFile.Parse(Map);

        var good = await store.SaveAsync([pricing, v1], file);
        Assert.True(good.Created);
        Assert.Equal((1, 0), (good.BoundEdges, good.BrokenEdges));
        var broken = await store.SaveAsync([pricing, v2], file);
        Assert.Equal((0, 1), (broken.BoundEdges, broken.BrokenEdges));

        Assert.Equal(edgesBefore, await db.CountAsync("edge"));
        Assert.Equal(1, await Scalar($"select count(*) from bound_edge where snapshot_id = '{good.SnapshotId}' and status = 'bound' and derivation = 'human' and resolution = 'resolved' and to_key = 'http|forecast-cs/Forecast.Api|GET /api/forecasts/{{}}'"));
        Assert.Equal(1, await Scalar($"select count(*) from bound_edge where snapshot_id = '{broken.SnapshotId}' and status = 'broken' and unresolved_reason = 'missing-reference'"));
        // The extractor's edge is still unresolved/configuration.
        Assert.Equal(0, await Scalar("select count(*) from edge where derivation = 'human'"));
        Assert.Equal(0, await Scalar("select count(*) from edge where to_key like 'http|forecast-cs/%' and from_key like 'ts|pricing-node|%'"));
    }

    [Fact]
    public async Task Saving_the_same_inputs_twice_is_a_no_op_and_changed_bindings_make_a_new_snapshot()
    {
        var pricing = await Import("pricing-node/v1-base/_fixture_pricing.jsonl");
        var v1 = await Import("forecast-cs/v1-base/Forecast.Api.jsonl");
        var store = new BindingStore(db.DataSource);
        var first = await store.SaveAsync([pricing, v1], BindingFile.Parse(Map));
        var rows = await db.CountAsync("bound_edge");
        var again = await store.SaveAsync([v1, pricing], BindingFile.Parse(Map));
        Assert.Equal(first.SnapshotId, again.SnapshotId);
        Assert.False(again.Created);
        Assert.Equal(rows, await db.CountAsync("bound_edge"));

        var other = await store.SaveAsync([pricing, v1], BindingFile.Parse(Map.Replace("\"x\"", "\"y\"", StringComparison.Ordinal)));
        Assert.NotEqual(first.SnapshotId, other.SnapshotId);
    }
}
