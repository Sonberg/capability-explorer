using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CapabilityExplorer.Store;
using Npgsql;
using Xunit;

namespace CapabilityExplorer.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class SnapshotEmbeddingTests(PostgresFixture db, ITestOutputHelper output)
{
    private SnapshotEmbeddingStore Store => new(db.DataSource, db.ConnectionString);
    internal static SnapshotEmbeddingModel Model(string label = "fake") =>
        new(label, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(label))), "ce-embedding-text.v1");
    internal static Task<float[][]> Fake(IReadOnlyList<string> texts, CancellationToken _) =>
        Task.FromResult(texts.Select(t => new[] { (float)t.Length, 1f, 2f }).ToArray());

    internal static async Task<long> Import(PostgresFixture database, string revision = "a", bool zero = false, string? suffix = null, int extraNodes = 0)
    {
        var rows = Fixtures.Golden("web-ts/v1-base/_fixture_web.jsonl").TrimEnd('\n').Split('\n').Select(s => JsonNode.Parse(s)!.AsObject()).ToArray();
        rows[0]["configHash"] = Convert.ToHexStringLower(SHA256.HashData(Guid.NewGuid().ToByteArray()));
        rows[0]["commitSha"] = new string(revision[0], 40);
        foreach (var row in rows.Where(r => (string?)r["kind"] == "evidence"))
            row["commitSha"] = new string(revision[0], 40);
        if (suffix is not null)
        {
            rows[0]["unit"]!["path"] = "unit-" + suffix;
            foreach (var row in rows.Where(r => (string?)r["kind"] == "node"))
                row["name"] = (string?)row["name"] + suffix;
        }
        if (zero)
            foreach (var row in rows.Where(r => (string?)r["kind"] == "node")) row["generated"] = true;
        var additional = new List<JsonObject>();
        if (extraNodes > 0)
        {
            var template = rows.First(r => (string?)r["nodeKind"] == "Function");
            for (var index = 0; index < extraNodes; index++)
            {
                var node = template.DeepClone().AsObject();
                node["id"] = "n:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("extra-" + index)))[..16];
                node["symbolKey"] = (string?)template["symbolKey"] + "-extra-" + index;
                additional.Add(node);
            }
        }
        rows[^1]["counts"]!["node"] = rows.Count(r => (string?)r["kind"] == "node") + additional.Count;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', rows[..^1].Concat(additional).Append(rows[^1]).Select(r => r.ToJsonString())) + "\n"));
        return (await new ArtifactImporter(database.DataSource).ImportAsync(stream)).ArtifactId;
    }

    private async Task<AnalysisSnapshot> Publish(long[] artifacts, IReadOnlyDictionary<string, string>? defaults = null, string? scope = null, long generation = 0)
    {
        var snapshots = new SnapshotStore(db.DataSource);
        scope ??= "embedding-" + Guid.NewGuid().ToString("N");
        var candidate = defaults is null ? await snapshots.CreateAsync(scope, Guid.NewGuid().ToString("N"), artifacts)
            : await snapshots.CreateAsync(scope, Guid.NewGuid().ToString("N"), artifacts, defaults);
        return await snapshots.PublishAsync(candidate.Id, generation);
    }

    private static Dictionary<string, string> View(char revision = 'a') => new() { ["web-ts"] = new(revision, 40) };

    private async Task<long> Scalar(string sql, params object[] parameters)
    {
        await using var command = db.DataSource.CreateCommand(sql);
        foreach (var p in parameters) command.Parameters.AddWithValue(p);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Whole_snapshot_occurrences_are_immutable_idempotent_and_historical_not_a_symbol_cache()
    {
        var a = await Import(db);
        var b = await Import(db, "b", suffix: "changed-logic");
        var first = await Publish([a]);
        var model = Model();
        var embedded = await Store.EmbedAsync(first.Id, model, Fake);
        Assert.True(embedded > 0);
        var beforeReplay = await Store.StatusAsync(first.Id, View(), model);
        Assert.Equal(0, await Store.EmbedAsync(first.Id, model, (_, _) => throw new InvalidOperationException("must not request provider")));
        var ready = await Store.StatusAsync(first.Id, View(), model);
        Assert.Equal(beforeReplay.LastSuccessfulCompletion, ready.LastSuccessfulCompletion);
        Assert.Equal(1, await Scalar("select count(*) from snapshot_embedding_projection where snapshot_id=$1 and updated_at=last_success", first.Id));
        Assert.Equal("ready", ready.State);
        Assert.Equal(embedded, ready.Counts!.Ready);
        Assert.True(ready.CountsExact);
        Assert.NotNull(ready.LastSuccessfulCompletion);
        var next = await Publish([b], scope: first.CapabilityId, generation: 1);
        await Store.EmbedAsync(next.Id, model, Fake);
        Assert.Equal("ready", (await Store.StatusAsync(first.Id, View(), model)).State);
        Assert.Equal("ready", (await Store.StatusAsync(next.Id, View('b'), model)).State);
        Assert.True(await Scalar("""
            select count(*) from snapshot_embedding_item x join node a on a.artifact_id=x.artifact_id and a.local_id=x.node_local_id
              join node b on b.symbol_key=a.symbol_key and b.artifact_id=$2
              join snapshot_embedding_item y on y.artifact_id=b.artifact_id and y.node_local_id=b.local_id and y.snapshot_id=$3
            where x.snapshot_id=$1 and x.artifact_id<>y.artifact_id and x.vec<>y.vec
            """, first.Id, b, next.Id) > 0);
        Assert.Equal(1, (await new SnapshotStore(db.DataSource).GetAsync(first.Id))!.Generation);
        Assert.Equal(2, (await new SnapshotStore(db.DataSource).GetAsync(next.Id))!.Generation);
        await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = db.DataSource.CreateCommand("update snapshot_embedding_item set attempts=attempts+1 where snapshot_id=$1");
            command.Parameters.AddWithValue(first.Id);
            await command.ExecuteNonQueryAsync();
        });
    }

    [Fact]
    public async Task Failure_retry_counts_once_and_unrelated_revision_failure_does_not_taint_selected_ready_view()
    {
        var a = await Import(db);
        var b = await Import(db, "b");
        var snapshot = await Publish([a, b], View());
        var model = Model();
        var calls = 0;
        await Assert.ThrowsAsync<EmbeddingProjectionFailedException>(() => Store.EmbedAsync(snapshot.Id, model, (texts, ct) =>
            ++calls == 1 ? Fake(texts, ct) : throw new InvalidOperationException("secret must not be persisted")));
        Assert.Equal(2, calls);
        var selected = await Store.StatusAsync(snapshot.Id, View(), model);
        Assert.Equal("ready", selected.State);
        var failed = await Store.StatusAsync(snapshot.Id, View('b'), model);
        Assert.Equal("partial-failure", failed.State);
        Assert.True(failed.CountsExact);
        Assert.True(failed.Counts!.Failed > 0);
        Assert.Equal("pending", (await Store.StatusAsync(snapshot.Id, View(), Model("not-started"))).State);
        var retried = await Store.EmbedAsync(snapshot.Id, model, Fake);
        Assert.Equal(failed.Counts.Failed, retried);
        var complete = await Store.StatusAsync(snapshot.Id, View('b'), model);
        Assert.Equal("ready", complete.State);
        Assert.Equal(0, complete.Counts!.Failed);
        Assert.Equal(complete.Counts.Total, complete.Counts.Ready);
        Assert.Equal(failed.Counts.Total, complete.Counts.Total);
        Assert.Equal(0, await Store.EmbedAsync(snapshot.Id, model, (_, _) => throw new InvalidOperationException()));
        var changed = await Store.StatusAsync(snapshot.Id, View(), Model("changed"));
        Assert.Equal("stale", changed.State);
        Assert.Equal(model, changed.Model);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("empty")]
    [InlineData("nan")]
    [InlineData("inconsistent")]
    [InlineData("oversized")]
    public async Task Malformed_provider_batches_never_publish_partial_vectors(string shape)
    {
        var snapshot = await Publish([await Import(db)]);
        var model = Model(shape);
        await Assert.ThrowsAsync<EmbeddingProjectionFailedException>(() => Store.EmbedAsync(snapshot.Id, model, (texts, _) =>
        {
            var vectors = texts.Select(_ => new[] { 1f, 2f }).ToArray();
            switch (shape)
            {
                case "short": vectors = vectors[..^1]; break;
                case "long": vectors = [.. vectors, new[] { 1f, 2f }]; break;
                case "empty": vectors[0] = []; break;
                case "nan": vectors[0][0] = float.NaN; break;
                case "inconsistent": vectors[0] = [1f]; break;
                case "oversized": vectors[0] = new float[16001]; break;
            }
            return Task.FromResult(vectors);
        }));
        var failed = await Store.StatusAsync(snapshot.Id, View(), model);
        Assert.Equal("partial-failure", failed.State);
        Assert.Equal(0, failed.Counts!.Ready);
        Assert.Equal(failed.Counts.Total, failed.Counts.Failed);
        Assert.Equal(failed.Counts.Total, await Store.EmbedAsync(snapshot.Id, model, Fake));
    }

    [Fact]
    public async Task Zero_eligible_and_empty_membership_need_actual_completed_projection_and_artifact_seals()
    {
        var model = Model();
        var zero = await Publish([await Import(db, zero: true)]);
        var absent = await Store.StatusAsync(zero.Id, View(), model);
        Assert.Equal("pending", absent.State);
        Assert.False(absent.CountsExact);
        Assert.Null(absent.Counts);
        Assert.Equal("not-configured", (await Store.StatusAsync(zero.Id, View(), null)).State);
        Assert.Equal(0, await Store.EmbedAsync(zero.Id, model, (_, _) => throw new InvalidOperationException()));
        var ready = await Store.StatusAsync(zero.Id, View(), model);
        Assert.Equal("ready", ready.State);
        Assert.Equal(1, ready.CoveredArtifactCount);
        Assert.Equal(new EmbeddingCounts(0, 0, 0, 0), ready.Counts);
        var empty = await Publish([]);
        Assert.Equal("pending", (await Store.StatusAsync(empty.Id, new Dictionary<string, string>(), model)).State);
        await Store.EmbedAsync(empty.Id, model, (_, _) => throw new InvalidOperationException());
        Assert.Equal("ready", (await Store.StatusAsync(empty.Id, new Dictionary<string, string>(), model)).State);
    }

    [Fact]
    public async Task Cancelled_attempt_is_durable_and_a_concurrent_writer_is_explicitly_busy()
    {
        var snapshot = await Publish([await Import(db)]);
        var model = Model();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.EmbedAsync(snapshot.Id, model, async (_, ct) =>
        {
            await Assert.ThrowsAsync<EmbeddingProjectionBusyException>(() => Store.EmbedAsync(snapshot.Id, model, Fake));
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return [];
        }, cancellation.Token));
        var interrupted = await Store.StatusAsync(snapshot.Id, View(), model);
        Assert.Equal("partial-failure", interrupted.State);
        Assert.False(interrupted.CountsExact);
        Assert.Null(interrupted.Counts);
        await Store.EmbedAsync(snapshot.Id, model, Fake);
        Assert.Equal("ready", (await Store.StatusAsync(snapshot.Id, View(), model)).State);
    }

    [Fact]
    public async Task Connection_loss_during_inference_cannot_reconnect_and_commit_late_vectors()
    {
        var snapshot = await Publish([await Import(db)]);
        var model = Model();
        await Assert.ThrowsAnyAsync<NpgsqlException>(() => Store.EmbedAsync(snapshot.Id, model, async (texts, ct) =>
        {
            await using var kill = db.DataSource.CreateCommand("""
                select pg_terminate_backend(pid) from pg_locks where locktype='advisory'
                  and pid<>pg_backend_pid() and granted
                  and classid::bigint=((hashtextextended($1,590035)>>32)&4294967295)
                  and objid::bigint=(hashtextextended($1,590035)&4294967295) and objsubid=1
                """);
            kill.Parameters.AddWithValue($"ce.snapshot-embedding:{snapshot.Id:D}:{model.Fingerprint}");
            Assert.Equal(true, await kill.ExecuteScalarAsync(ct));
            return await Fake(texts, ct);
        }));
        Assert.Equal(0, await Scalar("select count(*) from snapshot_embedding_item where snapshot_id=$1 and state='ready'", snapshot.Id));
        await Store.EmbedAsync(snapshot.Id, model, Fake);
        Assert.Equal("ready", (await Store.StatusAsync(snapshot.Id, View(), model)).State);
    }

    [Fact]
    public async Task Status_plan_is_metadata_only_and_historical_fallback_is_bounded_to_one_completed_projection()
    {
        var snapshot = await Publish([await Import(db)]);
        await Store.EmbedAsync(snapshot.Id, Model("old"), Fake);
        await Store.EmbedAsync(snapshot.Id, Model("new"), Fake);
        var status = await Store.StatusAsync(snapshot.Id, View(), Model("not-started"));
        Assert.Equal("stale", status.State);
        Assert.Equal(Model("new"), status.Model);
        var sql = (string)typeof(SnapshotEmbeddingStore).GetField("StatusSql",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetRawConstantValue()!;
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using (var settings = new NpgsqlCommand("set enable_seqscan=off", connection)) await settings.ExecuteNonQueryAsync();
        await using var command = new NpgsqlCommand("explain (analyze,format json) " + sql, connection);
        command.Parameters.AddWithValue(snapshot.Id);
        command.Parameters.AddWithValue(new[] { "web-ts" });
        command.Parameters.AddWithValue(new[] { new string('a', 40) });
        command.Parameters.AddWithValue(Model("not-started").Fingerprint);
        var plan = (string)(await command.ExecuteScalarAsync())!;
        output.WriteLine(plan);
        Assert.DoesNotContain("\"Relation Name\": \"node\"", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Relation Name\": \"snapshot_embedding_item\"", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Relation Name\": \"embedding\"", plan, StringComparison.Ordinal);
        Assert.Contains("snapshot_embedding_completed_idx", plan, StringComparison.Ordinal);
        Assert.Contains("\"Node Type\": \"Limit\"", plan, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(plan);
        var metadata = Nodes(document.RootElement[0].GetProperty("Plan"))
            .Single(n => n.TryGetProperty("Relation Name", out var relation) && relation.GetString() == "snapshot_embedding_artifact");
        var condition = metadata.GetProperty("Index Cond").GetString()!;
        Assert.Contains("snapshot_id", condition, StringComparison.Ordinal);
        Assert.Contains("model_key", condition, StringComparison.Ordinal);
        Assert.Contains("artifact_id", condition, StringComparison.Ordinal);

        static IEnumerable<JsonElement> Nodes(JsonElement node)
        {
            yield return node;
            if (!node.TryGetProperty("Plans", out var children)) yield break;
            foreach (var child in children.EnumerateArray())
                foreach (var descendant in Nodes(child)) yield return descendant;
        }
    }

    [Fact]
    public async Task Retained_snapshots_keep_vectors_and_membership_first_pruning_cascades_derived_rows()
    {
        var old = await Publish([await Import(db)]);
        var model = Model();
        await Store.EmbedAsync(old.Id, model, Fake);
        await Publish([await Import(db, "b")], scope: old.CapabilityId, generation: 1);
        Assert.Equal("ready", (await Store.StatusAsync(old.Id, View(), model)).State);
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var sql in new[] { "delete from analysis_snapshot_artifact where snapshot_id=$1",
            "delete from snapshot_revision where snapshot_id=$1", "delete from analysis_snapshot where id=$1" })
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue(old.Id);
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
        foreach (var table in new[] { "snapshot_embedding_projection", "snapshot_embedding_artifact", "snapshot_embedding_item" })
            Assert.Equal(0, await Scalar($"select count(*) from {table} where snapshot_id=$1", old.Id));
        await Assert.ThrowsAsync<SnapshotConflictException>(() => Store.StatusAsync(old.Id, View(), model));
    }

    [Fact]
    public async Task Keyset_pages_validate_persisted_dimensions_and_retry_only_failed_items_without_double_counting()
    {
        var snapshot = await Publish([await Import(db, extraNodes: 140)]);
        var model = Model();
        var batches = new List<int>();
        await Assert.ThrowsAsync<EmbeddingProjectionFailedException>(() => Store.EmbedAsync(snapshot.Id, model, (texts, ct) =>
        {
            batches.Add(texts.Count);
            return batches.Count == 2 ? Task.FromResult(texts.Select(_ => new[] { 1f, 2f }).ToArray()) : Fake(texts, ct);
        }));
        Assert.True(batches.Count >= 3);
        Assert.All(batches, size => Assert.InRange(size, 1, 64));
        var partial = await Store.StatusAsync(snapshot.Id, View(), model);
        Assert.True(partial.CountsExact);
        Assert.Equal(64, partial.Counts!.Failed);
        Assert.Equal(partial.Counts.Total - 64, partial.Counts.Ready);
        Assert.Equal(64, await Store.EmbedAsync(snapshot.Id, model, Fake));
        var ready = await Store.StatusAsync(snapshot.Id, View(), model);
        Assert.Equal("ready", ready.State);
        Assert.Equal(partial.Counts.Total, ready.Counts!.Total);
        Assert.Equal(ready.Counts.Total, ready.Counts.Ready);
    }

    [Fact]
    public async Task Membership_keyset_pages_seal_every_zero_eligible_artifact()
    {
        var artifacts = new List<long>();
        for (var index = 0; index < 65; index++)
            artifacts.Add(await Import(db, zero: true, suffix: index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var snapshot = await Publish(artifacts.ToArray());
        var model = Model();
        Assert.Equal(0, await Store.EmbedAsync(snapshot.Id, model, (_, _) => throw new InvalidOperationException()));
        var ready = await Store.StatusAsync(snapshot.Id, View(), model);
        Assert.Equal("ready", ready.State);
        Assert.Equal(65, ready.SelectedArtifactCount);
        Assert.Equal(65, ready.CoveredArtifactCount);
        Assert.Equal(65, await Scalar("select count(*) from snapshot_embedding_artifact where snapshot_id=$1 and sealed", snapshot.Id));
    }

    [Fact]
    public async Task Pruning_during_inference_cannot_report_success_for_a_removed_snapshot()
    {
        var snapshot = await Publish([await Import(db)]);
        await Assert.ThrowsAsync<SnapshotConflictException>(() => Store.EmbedAsync(snapshot.Id, Model(), async (texts, ct) =>
        {
            await Publish([], scope: snapshot.CapabilityId, generation: 1);
            await using var connection = await db.DataSource.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            foreach (var sql in new[] { "delete from analysis_snapshot_artifact where snapshot_id=$1",
                "delete from snapshot_revision where snapshot_id=$1", "delete from analysis_snapshot where id=$1" })
            {
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                command.Parameters.AddWithValue(snapshot.Id);
                await command.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
            return await Fake(texts, ct);
        }));
        Assert.Equal(0, await Scalar("select count(*) from snapshot_embedding_projection where snapshot_id=$1", snapshot.Id));
    }
}
