using CapabilityExplorer.Contracts;
using CapabilityExplorer.Store;
using Xunit;

namespace CapabilityExplorer.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class PublicationConfirmationBudgetTests(PostgresFixture db)
{
    [Theory]
    [InlineData(1001, 10)]
    [InlineData(80, 4000)]
    public async Task Core_confirmation_preserves_large_publications_while_operator_report_stays_bounded(
        int count, int pathLength)
    {
        var artifact = ArtifactValidator.Validate(Fixtures.Golden("web-ts/v1-base/_fixture_web.jsonl")).Artifact!;
        var original = await new ArtifactImporter(db.DataSource).ImportAsync(artifact);
        var suffix = Guid.NewGuid().ToString("N");
        var selected = new List<(long Id, string Path)>();
        await using (var seed = db.DataSource.CreateCommand("""
            insert into artifact(repository_id,commit_sha,unit_path,unit_name,language,extractor_name,
                extractor_version,config_hash,input_hash,status,content_sha256,header,complete)
            select repository_id,commit_sha,repeat('p',$3)||$4||n,unit_name,language,extractor_name,
                extractor_version,config_hash,input_hash,status,content_sha256,header,complete
            from artifact cross join generate_series(1,$2) n where id=$1
            returning id,unit_path
            """))
        {
            seed.Parameters.AddWithValue(original.ArtifactId); seed.Parameters.AddWithValue(count);
            seed.Parameters.AddWithValue(pathLength); seed.Parameters.AddWithValue(suffix);
            await using var reader = await seed.ExecuteReaderAsync();
            while (await reader.ReadAsync()) selected.Add((reader.GetInt64(0),reader.GetString(1)));
        }
        selected.Sort((a,b) => a.Id.CompareTo(b.Id));
        var h = artifact.Header;
        var expectations = selected.Select((a,index) => new PublicationArtifactExpectation(index,
            h.RepositoryId,h.CommitSha,a.Path,h.Extractor.Name,h.Extractor.Version,h.ConfigHash,
            artifact.ContentSha256,artifact.ContentSha256)).ToArray();
        var store = new SnapshotStore(db.DataSource);
        var run = Guid.NewGuid().ToString("N"); var attempt = Guid.NewGuid().ToString("N");
        await store.BeginPublicationAttemptAsync(new(run,attempt,"large-"+run,new('a',64),new('b',64),0,expectations));
        await using (var receipts = db.DataSource.CreateCommand("""
            update analysis_publication_attempt_artifact r
            set artifact_id=a.id,import_state='imported',observed_content_sha256=r.content_sha256
            from artifact a where r.run_id=$1 and r.attempt_id=$2 and r.repository_id=a.repository_id
                and r.commit_sha=a.commit_sha and r.unit_path=a.unit_path
                and r.extractor_name=a.extractor_name and r.extractor_version=a.extractor_version
                and r.config_hash=a.config_hash
            """))
        {
            receipts.Parameters.AddWithValue(run); receipts.Parameters.AddWithValue(attempt);
            Assert.Equal(count,await receipts.ExecuteNonQueryAsync());
        }
        var candidate = await store.CreateAsync("large-"+run,"identity-"+run,selected.Select(a => a.Id).ToArray());
        await store.BindPublicationCandidateAsync(run,attempt,candidate.Id,candidate.InputIdentity);
        Assert.True((await store.PublishAttemptAsync(run,attempt)).Published);

        var confirmation = await store.ConfirmPublicationAttemptAsync(run,attempt,expectations);
        Assert.Equal("confirmed-published",confirmation.Outcome);
        Assert.Equal(count,confirmation.Artifacts.Count);
        Assert.Equal(candidate.Id,confirmation.CandidateSnapshotId);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReconcilePublicationAttemptAsync(run,attempt));
        Assert.Equal(candidate.Id,(await store.GetPublishedAsync("large-"+run))!.Id);

        var mismatched = expectations.ToArray();
        mismatched[0] = mismatched[0] with { SourceSha256 = new('c',64) };
        Assert.Equal("unresolved",(await store.ConfirmPublicationAttemptAsync(run,attempt,mismatched)).Outcome);
    }
}
