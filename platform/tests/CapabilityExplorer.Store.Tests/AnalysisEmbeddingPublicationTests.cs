extern alias analysiscli;
using analysiscli::CapabilityExplorer.Cli;
using CapabilityExplorer.Store;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed partial class AnalysisExecutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_invalid_optional_embedding_configuration_never_rolls_back_confirmed_core_publication(bool invalid)
    {
        using var environment = new DatabaseEnvironment(database.ConnectionString);
        var oldEndpoint = Environment.GetEnvironmentVariable("CE_MODEL_ENDPOINT");
        var oldModel = Environment.GetEnvironmentVariable("CE_EMBEDDING_MODEL");
        try
        {
            Environment.SetEnvironmentVariable("CE_MODEL_ENDPOINT", invalid ? "https://user:private-token@model.invalid" : "http://localhost:11434/v1");
            Environment.SetEnvironmentVariable("CE_EMBEDDING_MODEL", invalid ? "fake" : null);
            using var f = new ExecutionFixture();
            var prepared = f.Prepare();
            Guid? confirmed;
            using (var permit = f.Begin())
            {
                confirmed = await AnalysisPublication.Publish(f.Root, f.Run, prepared, permit);
                permit.Complete();
            }
            var enrichment = await AnalysisEnrichment.Run(confirmed);
            Assert.Equal(invalid ? "failed" : "not-configured", enrichment.Outcome);
            Assert.DoesNotContain("private-token", enrichment.Detail, StringComparison.Ordinal);
            var run = f.Store.RecordEnrichment(f.Owner, f.Identity, enrichment.Outcome, enrichment.Detail);
            Assert.Equal("done", run.State);
            Assert.Equal(confirmed, (await new SnapshotStore(database.DataSource).GetPublishedAsync(f.Run.CapabilityId))!.Id);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CE_MODEL_ENDPOINT", oldEndpoint);
            Environment.SetEnvironmentVariable("CE_EMBEDDING_MODEL", oldModel);
        }
    }

    [Fact]
    public async Task Confirmed_snapshot_returned_to_enrichment_does_not_follow_a_newer_publication()
    {
        using var environment = new DatabaseEnvironment(database.ConnectionString);
        using var f = new ExecutionFixture();
        var prepared = f.Prepare();
        Guid? original = null, newer = null;
        AnalysisPublication.BeforeHostConfirmationForTests = async ct =>
        {
            var snapshots = new SnapshotStore(database.DataSource);
            var current = (await snapshots.GetPublishedAsync(f.Run.CapabilityId, ct))!;
            original = current.Id;
            var empty = await snapshots.CreateAsync(f.Run.CapabilityId, "later-" + Guid.NewGuid().ToString("N"), [], ct);
            newer = (await snapshots.PublishAsync(empty.Id, current.Generation!.Value, ct)).Id;
        };
        try
        {
            using var permit = f.Begin();
            var confirmed = await AnalysisPublication.Publish(f.Root, f.Run, prepared, permit);
            Assert.Equal(original, confirmed);
            Assert.NotEqual(newer, confirmed);
            permit.Complete();
        }
        finally { AnalysisPublication.BeforeHostConfirmationForTests = null; }
    }
}
