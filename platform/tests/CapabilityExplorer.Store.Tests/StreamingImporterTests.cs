using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Store;
using Xunit;

namespace CapabilityExplorer.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class StreamingImporterTests(PostgresFixture db)
{
    private readonly ArtifactImporter _importer = new(db.DataSource);

    private static string Changed(Action<List<JsonObject>> change, string file = "api-cs/v1-base/Sales.Domain.jsonl")
    {
        var rows = Fixtures.Golden(file).TrimEnd('\n').Split('\n')
            .Select(s => JsonNode.Parse(s)!.AsObject()).ToList();
        rows[0]["configHash"] = Convert.ToHexStringLower(SHA256.HashData(Guid.NewGuid().ToByteArray()));
        change(rows);
        return string.Join('\n', rows.Select(r => r.ToJsonString())) + "\n";
    }

    private Task<ImportResult> Import(string jsonl, CancellationToken ct = default) =>
        _importer.ImportAsync(new MemoryStream(Encoding.UTF8.GetBytes(jsonl)), ct);

    [Fact]
    public async Task Every_protected_fixture_streams_with_identical_hash_counts_and_status()
    {
        foreach (var file in Fixtures.GoldenFiles)
        {
            var jsonl = Fixtures.Golden(file);
            var expected = ArtifactValidator.Validate(jsonl).Artifact!;
            var result = await Import(jsonl);
            await using var command = db.DataSource.CreateCommand("""
                select content_sha256,status,(select count(*) from node where artifact_id=a.id),
                  (select count(*) from edge where artifact_id=a.id),(select count(*) from evidence where artifact_id=a.id),
                  (select count(*) from calculation where artifact_id=a.id),(select count(*) from diagnostic where artifact_id=a.id)
                from artifact a where id=$1
                """);
            command.Parameters.AddWithValue(result.ArtifactId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(expected.ContentSha256, reader.GetString(0));
            Assert.Equal(expected.Complete.Status, reader.GetString(1));
            Assert.Equal(new long[] {expected.Nodes.Count,expected.Edges.Count,expected.Evidence.Count,expected.Calculations.Count,expected.Diagnostics.Count},
                Enumerable.Range(2,5).Select(reader.GetInt64));
            await reader.DisposeAsync();
            var distinctConfig=Convert.ToHexStringLower(SHA256.HashData(Guid.NewGuid().ToByteArray()));
            var referenceInput=jsonl.Replace(expected.Header.ConfigHash,distinctConfig,StringComparison.Ordinal);
            var reference=(await _importer.ImportAsync(ArtifactValidator.Validate(referenceInput).Artifact!)).ArtifactId;
            foreach(var table in new[]{"node","edge","evidence","calculation","diagnostic"})
            {
                await using var parity=db.DataSource.CreateCommand($"""
                    with streamed as (select to_jsonb(r)-'artifact_id' as body from {table} r where artifact_id=$1),
                         materialized as (select to_jsonb(r)-'artifact_id' as body from {table} r where artifact_id=$2)
                    select not exists((select body from streamed except select body from materialized)
                        union all (select body from materialized except select body from streamed))
                    """);
                parity.Parameters.AddWithValue(result.ArtifactId);
                parity.Parameters.AddWithValue(reference);
                Assert.Equal(true,await parity.ExecuteScalarAsync());
            }
        }
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("duplicate-id")]
    [InlineData("duplicate-symbol")]
    [InlineData("unknown-evidence")]
    [InlineData("wrong-revision")]
    [InlineData("bad-span")]
    [InlineData("unknown-source")]
    [InlineData("bad-relation")]
    [InlineData("bad-key-owner")]
    [InlineData("count-mismatch")]
    [InlineData("full-coverage")]
    [InlineData("after-complete")]
    public async Task Stream_rejects_invalid_artifacts_and_rolls_back_every_table(string mutation)
    {
        var input = Changed(rows =>
        {
            var node = rows.First(r => (string?)r["kind"] == "node");
            var ev = rows.First(r => (string?)r["kind"] == "evidence");
            var edge = rows.First(r => (string?)r["kind"] == "edge");
            switch (mutation)
            {
                case "truncated": rows.RemoveAt(rows.Count-1); break;
                case "duplicate-id": node["id"] = edge["id"]!.DeepClone(); break;
                case "duplicate-symbol": rows.Insert(1,(JsonObject)node.DeepClone()); rows[1]["id"]="n:duplicate"; break;
                case "unknown-evidence": node["evidence"]=new JsonArray("ev:missing"); break;
                case "wrong-revision": ev["commitSha"]=new string('0',40); break;
                case "bad-span": ev["span"]!["endLine"]=0; break;
                case "unknown-source": edge["from"]="cs|api-cs|Sales.Domain|T:Missing"; break;
                case "bad-relation": edge["relation"]="CONTAINS"; edge["from"]=node["symbolKey"]!.DeepClone(); break;
                case "bad-key-owner": node["symbolKey"]="cs|another-repo|Sales.Domain|T:Other"; break;
                case "count-mismatch": rows[^1]["counts"]!["node"]=999; break;
                case "full-coverage": rows[^1]["coverage"]!["filesAnalyzed"]=0; break;
                case "after-complete": rows.Add((JsonObject)node.DeepClone()); break;
            }
        });
        Assert.False(ArtifactValidator.Validate(input).IsValid);
        var before = await db.CountAllAsync();
        await Assert.ThrowsAsync<InvalidArtifactException>(() => Import(input));
        Assert.Equal(before, await db.CountAllAsync());
    }

    [Fact]
    public async Task Concurrent_stream_retries_share_one_artifact_and_changed_bytes_conflict()
    {
        var input = Changed(_ => { });
        var results = await Task.WhenAll(Enumerable.Range(0,4).Select(_ => Import(input)));
        Assert.Single(results,r => r.Outcome == ImportOutcome.Imported);
        Assert.Single(results.Select(r => r.ArtifactId).Distinct());
        await Assert.ThrowsAsync<ArtifactConflictException>(() => Import(input.Replace("\n", "\r\n",StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Cancellation_after_staging_begins_leaves_no_artifact()
    {
        var before = await db.CountAllAsync();
        using var cancel = new CancellationTokenSource();
        var bytes = Encoding.UTF8.GetBytes(Changed(_=>{}));
        await using var stream = new CancelAfterReadStream(bytes,cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _importer.ImportAsync(stream,cancel.Token));
        Assert.Equal(before, await db.CountAllAsync());
    }

    [Fact]
    public async Task Oversized_record_is_rejected_without_unbounded_buffering()
    {
        var before = await db.CountAllAsync();
        await Assert.ThrowsAsync<InvalidArtifactException>(() => Import(new string(' ',ArtifactImporter.MaximumRecordBytes+1)));
        Assert.Equal(before, await db.CountAllAsync());
    }

    [Theory]
    [InlineData("undeclared-symbol")]
    [InlineData("duplicate-input")]
    [InlineData("unbound-variable")]
    [InlineData("attribute-evidence")]
    public async Task Calculation_validation_matches_the_materialized_contract(string mutation)
    {
        var input=Changed(rows=>
        {
            var calc=rows.First(r=>(string?)r["kind"]=="calculation");
            switch(mutation)
            {
                case "undeclared-symbol": calc["symbolKey"]="cs|api-cs|Sales.Api|M:Missing"; break;
                case "duplicate-input": calc["inputs"]!.AsArray().Add(calc["inputs"]![0]!.DeepClone()); break;
                case "unbound-variable": calc["expr"]=new JsonObject{["op"]="var",["ref"]="$999"}; break;
                case "attribute-evidence": calc["attributes"]!["aggregation"]!["evidence"]=new JsonArray("ev:missing"); break;
            }
        },"api-cs/v1-base/Sales.Api.jsonl");
        Assert.False(ArtifactValidator.Validate(input).IsValid);
        var before=await db.CountAllAsync();
        await Assert.ThrowsAsync<InvalidArtifactException>(()=>Import(input));
        Assert.Equal(before,await db.CountAllAsync());
    }

    [Fact]
    public async Task Failure_in_final_normalized_table_rolls_back_every_staged_fact()
    {
        await db.ExecuteAsync("""
            create function ce_stream_fail() returns trigger language plpgsql as
            $$ begin raise exception 'injected streaming failure'; end $$;
            create trigger ce_stream_fail before insert on diagnostic for each row execute function ce_stream_fail();
            """);
        try
        {
            var before=await db.CountAllAsync();
            var input=Changed(_=>{},"worker-cs/v2-broken/Sales.Worker.jsonl");
            await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(()=>Import(input));
            Assert.Equal(before,await db.CountAllAsync());
        }
        finally { await db.ExecuteAsync("drop trigger ce_stream_fail on diagnostic; drop function ce_stream_fail()"); }
    }

    private sealed class CancelAfterReadStream(byte[] bytes, CancellationTokenSource cancel) : MemoryStream(bytes)
    {
        private int _reads;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++_reads == 2) cancel.Cancel();
            return base.ReadAsync(buffer[..Math.Min(buffer.Length,2048)],cancellationToken);
        }
    }
}
