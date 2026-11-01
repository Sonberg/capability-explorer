using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CapabilityExplorer.Store;
using Xunit;

namespace CapabilityExplorer.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class SnapshotStoreTests(PostgresFixture db)
{
    private readonly SnapshotStore _store = new(db.DataSource);
    private readonly ArtifactImporter _importer = new(db.DataSource);
    private static string Scope() => "test-"+Guid.NewGuid().ToString("N");

    private async Task<long> Artifact(bool changedAnalysis = false)
    {
        var input = Fixtures.Golden("web-ts/v1-base/_fixture_web.jsonl");
        if (changedAnalysis)
        {
            var rows = input.TrimEnd('\n').Split('\n').Select(s=>JsonNode.Parse(s)!.AsObject()).ToArray();
            rows[0]["configHash"] = Convert.ToHexStringLower(SHA256.HashData(Guid.NewGuid().ToByteArray()));
            input=string.Join('\n',rows.Select(r=>r.ToJsonString()))+"\n";
        }
        using var stream=new MemoryStream(Encoding.UTF8.GetBytes(input));
        return (await _importer.ImportAsync(stream)).ArtifactId;
    }

    [Fact]
    public async Task Candidate_creation_is_idempotent_and_preserves_exact_membership_and_revisions()
    {
        var scope=Scope();
        var artifact=await Artifact();
        var a=await _store.CreateAsync(scope,"inputs-1",[artifact]);
        var retry=await _store.CreateAsync(scope,"inputs-1",[artifact]);
        Assert.Equal(a.Id,retry.Id);
        Assert.Equal("candidate",a.Status);
        Assert.Null(a.Generation);
        Assert.Null(await _store.GetPublishedAsync(scope));
        await using var command=db.DataSource.CreateCommand("select count(*) from snapshot_revision where snapshot_id=$1");
        command.Parameters.AddWithValue(a.Id);
        Assert.Equal(1L,await command.ExecuteScalarAsync());
        await Assert.ThrowsAsync<SnapshotConflictException>(()=>_store.CreateAsync(scope,"inputs-1",[]));
    }

    [Fact]
    public async Task Last_good_selection_remains_readable_until_next_publication_and_historical_snapshot_remains_immutable()
    {
        var scope=Scope();
        var first=await _store.CreateAsync(scope,"first",[await Artifact()]);
        var published=await _store.PublishAsync(first.Id,0);
        Assert.Equal(1L,published.Generation);
        var next=await _store.CreateAsync(scope,"second",[await Artifact(true)]);
        Assert.Equal(first.Id,(await _store.GetPublishedAsync(scope))!.Id);
        var advanced=await _store.PublishAsync(next.Id,1);
        Assert.Equal(2L,advanced.Generation);
        Assert.Equal(next.Id,(await _store.GetPublishedAsync(scope))!.Id);
        Assert.Equal(published,await _store.GetAsync(first.Id));
        await Assert.ThrowsAsync<SnapshotConflictException>(()=>_store.PublishAsync(first.Id,2));
        Assert.Equal(advanced,await _store.PublishAsync(next.Id,1));
    }

    [Fact]
    public async Task Competing_publishers_have_exactly_one_winner_and_loser_does_not_mutate_pointer()
    {
        var scope=Scope();
        var ids=new[] {(await _store.CreateAsync(scope,"a",[])).Id,(await _store.CreateAsync(scope,"b",[])).Id};
        var attempts=await Task.WhenAll(ids.Select(async id=>
        {
            try { return (Snapshot:await _store.PublishAsync(id,0),Conflict:false); }
            catch(SnapshotConflictException) { return (Snapshot:(AnalysisSnapshot?)null,Conflict:true); }
        }));
        Assert.Single(attempts,r=>r.Conflict);
        var winner=Assert.Single(attempts,r=>!r.Conflict).Snapshot!;
        Assert.Equal(winner,await _store.GetPublishedAsync(scope));
        Assert.Equal("candidate",(await _store.GetAsync(ids.Single(id=>id!=winner.Id)))!.Status);
    }

    [Fact]
    public async Task Unknown_or_duplicate_or_competing_artifacts_roll_back_candidate()
    {
        var scope=Scope();
        var a=await Artifact();
        var b=await Artifact(true);
        await Assert.ThrowsAsync<ArgumentException>(()=>_store.CreateAsync(scope,"unknown",[long.MaxValue]));
        await Assert.ThrowsAsync<ArgumentException>(()=>_store.CreateAsync(scope,"duplicate",[a,a]));
        await Assert.ThrowsAsync<SnapshotConflictException>(()=>_store.CreateAsync(scope,"competing",[a,b]));
        await using var cmd=db.DataSource.CreateCommand("select count(*) from analysis_snapshot where capability_id=$1");
        cmd.Parameters.AddWithValue(scope);
        Assert.Equal(0L,await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Canceled_publication_preserves_last_good_and_candidate()
    {
        var scope=Scope();
        var first=await _store.CreateAsync(scope,"first",[]);
        await _store.PublishAsync(first.Id,0);
        var next=await _store.CreateAsync(scope,"next",[]);
        await using var hold=await db.DataSource.OpenConnectionAsync();
        await using var tx=await hold.BeginTransactionAsync();
        await using var lockCommand=new Npgsql.NpgsqlCommand("select pg_advisory_xact_lock(hashtextextended($1,284662))",hold,tx);
        lockCommand.Parameters.AddWithValue("publish:"+scope);
        await lockCommand.ExecuteNonQueryAsync();
        using var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>_store.PublishAsync(next.Id,1,cancel.Token));
        Assert.Equal(first.Id,(await _store.GetPublishedAsync(scope))!.Id);
        Assert.Equal("candidate",(await _store.GetAsync(next.Id))!.Status);
    }
    [Fact]
    public async Task Snapshot_operations_work_with_a_single_connection_budget()
    {
        var builder=new Npgsql.NpgsqlConnectionStringBuilder(db.ConnectionString){MaxPoolSize=1};
        await using var source=Npgsql.NpgsqlDataSource.Create(builder.ConnectionString);
        var store=new SnapshotStore(source);
        var scope=Scope();
        var snapshot=await store.CreateAsync(scope,"single",[]).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(snapshot,await store.CreateAsync(scope,"single",[]).WaitAsync(TimeSpan.FromSeconds(5)));
        var published=await store.PublishAsync(snapshot.Id,0).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(published,await store.PublishAsync(snapshot.Id,0).WaitAsync(TimeSpan.FromSeconds(5)));
    }

}
