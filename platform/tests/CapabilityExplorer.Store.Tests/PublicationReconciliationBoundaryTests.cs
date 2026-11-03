extern alias analysiscli;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Store;
using Xunit;

namespace CapabilityExplorer.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class PublicationReconciliationBoundaryTests(PostgresFixture db)
{
    [Fact]
    public async Task Actual_stream_hash_must_match_original_receipt_before_import_commit()
    {
        var jsonl = Fixtures.Golden("web-ts/v1-base/_fixture_web.jsonl");
        var validation = ArtifactValidator.Validate(jsonl).Artifact!;
        var h = validation.Header;
        var run = Guid.NewGuid().ToString("N");
        var attempt = Guid.NewGuid().ToString("N");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(jsonl)));
        var expectation = new PublicationArtifactExpectation(0, h.RepositoryId, h.CommitSha, h.Unit.Path,
            h.Extractor.Name, h.Extractor.Version, h.ConfigHash, validation.ContentSha256, hash);
        var store = new SnapshotStore(db.DataSource);
        await store.BeginPublicationAttemptAsync(new(run, attempt, "stream-" + run, hash, hash, 0, [expectation]));
        var before = await db.CountAsync("artifact");
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(" " + jsonl));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ArtifactImporter(db.DataSource).ImportForPublicationAsync(stream, new(run, attempt, 0, hash)));

        Assert.Equal(before, await db.CountAsync("artifact"));
        var report = await store.ReconcilePublicationAttemptAsync(run, attempt);
        Assert.Equal("unresolved", report.Outcome);
        Assert.Equal("uncertain", Assert.Single(report.Artifacts).Outcome);
    }

    [Fact]
    public async Task Failed_terminal_receipt_recording_is_visible_without_masking_original_error()
    {
        var run = Guid.NewGuid().ToString("N");
        var attempt = Guid.NewGuid().ToString("N");
        var previous = Console.Error;
        using var error = new StringWriter();
        Console.SetError(error);
        try
        {
            var result = await analysiscli::CapabilityExplorer.Cli.ImportCommand.RunAnalysisAsync(
                db.DataSource, ["missing.jsonl"], new Dictionary<string,string> { ["scope"]="test", ["expected-generation"]="invalid" },
                run, attempt, new('a',64), new('b',64),
                [new(0,"repo",new('a',40),"unit","test","1",new('a',64),new('b',64),new('b',64))]);
            Assert.Equal(2, result);
        }
        finally { Console.SetError(previous); }
        Assert.Contains("terminal receipt recording failed (InvalidOperationException); publication outcome unresolved", error.ToString());
        Assert.Equal("unresolved", (await new SnapshotStore(db.DataSource).ReconcilePublicationAttemptAsync(run,attempt)).Outcome);
    }

    [Theory]
    [InlineData("null-hash")]
    [InlineData("null-effect")]
    [InlineData("null-effect-state")]
    public async Task Malformed_host_journal_is_conflicting_and_unchanged(string corruption)
    {
        var run = Guid.NewGuid().ToString("N");
        var attempt = Guid.NewGuid().ToString("N");
        var root = Path.Combine(Fixtures.RepositoryRoot, ".sandbox-cache", "issue61-report-" + run);
        var directory = Path.Combine(root, ".sandbox-cache", "requests", "runs", run);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory,"run.json");
        var record = new JsonObject {
            ["runId"]=run, ["attemptId"]=attempt, ["capabilityId"]="test", ["state"]="unknown",
            ["acceptedInputsHash"]=new string('a',64), ["manifestHash"]=new string('b',64)
        };
        if (corruption=="null-hash") record["acceptedInputsHash"]=null;
        else record["effects"]=corruption=="null-effect" ? new JsonArray((JsonNode?)null)
            : new JsonArray(new JsonObject { ["surface"]="postgres", ["destination"]="test", ["state"]=null });
        File.WriteAllText(path,record.ToJsonString());
        var before = File.ReadAllBytes(path);
        var databaseBefore = (await db.CountAsync("analysis_publication_attempt"),
            await db.CountAsync("analysis_publication_attempt_result"), await db.CountAsync("schema_migration"));
        try
        {
            var result = await Report(root,run,attempt);
            Assert.Equal(1,result.Exit);
            var json = JsonNode.Parse(result.Output)!;
            Assert.Equal("unresolved",(string?)json["outcome"]);
            Assert.Equal("unreadable-or-conflicting",(string?)json["hostJournal"]!["status"]);
            Assert.Equal(before,File.ReadAllBytes(path));
            Assert.Single(Directory.GetFiles(directory));
            Assert.Equal(databaseBefore, (await db.CountAsync("analysis_publication_attempt"),
                await db.CountAsync("analysis_publication_attempt_result"), await db.CountAsync("schema_migration")));
        }
        finally { Directory.Delete(root,true); }
    }

    [Theory]
    [InlineData("expected-count")]
    [InlineData("actual-count")]
    [InlineData("data-bytes")]
    [InlineData("encoded-bytes")]
    public async Task Excessive_report_selection_fails_explicitly_without_database_mutation(string excess)
    {
        var run = Guid.NewGuid().ToString("N"); var attempt = Guid.NewGuid().ToString("N");
        await using var insert = db.DataSource.CreateCommand("""
            insert into analysis_publication_attempt(run_id,attempt_id,capability_id,accepted_inputs_hash,
                manifest_hash,expected_generation,expected_artifact_count,selection_sha256)
            values($1,$2,'budget',repeat('a',64),repeat('b',64),0,$3,repeat('c',64))
            """);
        insert.Parameters.AddWithValue(run); insert.Parameters.AddWithValue(attempt);
        insert.Parameters.AddWithValue(excess=="expected-count" ? 1001 : excess=="encoded-bytes" ? 400 : 1);
        await insert.ExecuteNonQueryAsync();
        if (excess!="expected-count")
        {
            await using var artifacts = db.DataSource.CreateCommand("""
                insert into analysis_publication_attempt_artifact(run_id,attempt_id,artifact_index,
                    repository_id,commit_sha,unit_path,extractor_name,extractor_version,config_hash,
                    content_sha256,source_sha256,import_state)
                select $1,$2,n,'repo',repeat('a',40),repeat($5,$3),'test','1',
                    repeat('b',64),repeat('c',64),repeat('c',64),'pending'
                from generate_series(0,$4) n
                """);
            artifacts.Parameters.AddWithValue(run); artifacts.Parameters.AddWithValue(attempt);
            artifacts.Parameters.AddWithValue(excess=="data-bytes" ? 270000 : excess=="encoded-bytes" ? 360 : 1);
            artifacts.Parameters.AddWithValue(excess=="actual-count" ? 1000 : excess=="encoded-bytes" ? 399 : 0);
            artifacts.Parameters.AddWithValue(excess=="encoded-bytes" ? "\u0001" : "u");
            await artifacts.ExecuteNonQueryAsync();
        }
        var count = await db.CountAsync("analysis_publication_attempt_result");
        var report = await Report(Fixtures.RepositoryRoot,run,attempt);
        Assert.Equal(1,report.Exit);
        Assert.Contains("report-budget-exceeded",report.Output);
        Assert.True(Encoding.UTF8.GetByteCount(report.Output) <= 1024*1024);
        Assert.Equal(count,await db.CountAsync("analysis_publication_attempt_result"));
    }

    private async Task<(int Exit,string Output)> Report(string root,string run,string attempt)
    {
        var start = new ProcessStartInfo("dotnet") {
            WorkingDirectory=root,RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false
        };
        start.ArgumentList.Add(Path.Combine(Fixtures.RepositoryRoot,"platform/src/CapabilityExplorer.Cli/bin/Debug/net10.0/ce.dll"));
        start.ArgumentList.Add("reconcile-publication");
        start.ArgumentList.Add("--run"); start.ArgumentList.Add(run);
        start.ArgumentList.Add("--attempt"); start.ArgumentList.Add(attempt);
        start.Environment["CE_DATABASE"]=db.ConnectionString;
        using var process = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        await Task.WhenAll(output,error,process.WaitForExitAsync(deadline.Token));
        Assert.Empty(await error);
        return (process.ExitCode,await output);
    }
}
