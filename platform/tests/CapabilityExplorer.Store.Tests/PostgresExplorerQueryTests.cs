using System.Text.Json.Nodes;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Explorer;
using CapabilityExplorer.Store;
using Npgsql;
using Xunit;

namespace CapabilityExplorer.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class PostgresExplorerQueryTests(PostgresFixture db)
{
    private readonly PostgresExplorerQueries _queries = new(db.DataSource, new ExplorerQueryCursor(new byte[32]));

    private async Task<ExplorerQueryContext> Snapshot(string? capability = null, long generation = 1, Action<List<JsonObject>>? mutate = null, string? additionalJsonl = null)
    {
        var cap = capability ?? "queries-" + Guid.NewGuid().ToString("N");
        var snapshot = Guid.NewGuid();
        await using var insert = db.DataSource.CreateCommand("""
            insert into analysis_snapshot(id,capability_id,status,input_identity,generation,published_at)
            values(@id,@cap,'published',@identity,@generation,now())
            """);
        insert.Parameters.AddWithValue("id", snapshot); insert.Parameters.AddWithValue("cap", cap);
        insert.Parameters.AddWithValue("identity", snapshot.ToString()); insert.Parameters.AddWithValue("generation", generation);
        await insert.ExecuteNonQueryAsync();
        foreach (var (file, input) in Fixtures.GoldenFiles.Where(x => x.Contains("/v1-base/", StringComparison.Ordinal)).Select(file => (file, Fixtures.Golden(file)))
            .Concat(additionalJsonl is null ? [] : new[] { ("additional", additionalJsonl) }))
        {
            var jsonl = input;
            if (mutate is not null && file == "web-ts/v1-base/_fixture_web.jsonl")
            {
                var records = jsonl.TrimEnd('\n').Split('\n').Select(line => JsonNode.Parse(line)!.AsObject()).ToList();
                records[0]["configHash"] = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
                mutate(records);
                jsonl = string.Join('\n', records.Select(record => record.ToJsonString())) + "\n";
            }
            var validation = ArtifactValidator.Validate(jsonl);
            Assert.True(validation.IsValid, string.Join("; ", validation.Problems));
            var artifact = validation.Artifact!;
            var imported = await new ArtifactImporter(db.DataSource).ImportAsync(artifact);
            await using var member = db.DataSource.CreateCommand("""
                insert into analysis_snapshot_artifact(snapshot_id,artifact_id,repository_id,commit_sha,unit_path)
                select @snapshot,id,repository_id,commit_sha,unit_path from artifact where id=@artifact;
                insert into snapshot_revision(snapshot_id,repository_id,commit_sha,label,is_default)
                select @snapshot,repository_id,commit_sha,'base',true from artifact where id=@artifact on conflict do nothing;
                """);
            member.Parameters.AddWithValue("snapshot", snapshot); member.Parameters.AddWithValue("artifact", imported.ArtifactId);
            await member.ExecuteNonQueryAsync();
        }
        await using var publish = db.DataSource.CreateCommand("""
            insert into published_snapshot(capability_id,snapshot_id,generation) values(@cap,@snapshot,@generation)
            on conflict(capability_id) do update set snapshot_id=excluded.snapshot_id,generation=excluded.generation
            """);
        publish.Parameters.AddWithValue("cap", cap); publish.Parameters.AddWithValue("snapshot", snapshot); publish.Parameters.AddWithValue("generation", generation);
        await publish.ExecuteNonQueryAsync();
        return (await _queries.ResolveAsync(cap))!;
    }

    [Fact]
    public async Task SearchPagesAreStableUniqueBoundedAndRetainRank()
    {
        var context = await Snapshot();
        var all = new List<SearchHit>(); string? cursor = null;
        do
        {
            var page = await _queries.SearchAsync(context, null, null, null, new(3, cursor));
            Assert.InRange(page.Items.Count, 1, 3); Assert.Equal(context.SnapshotId, page.SnapshotId);
            all.AddRange(page.Items); cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.True(all.Count > 10); Assert.Equal(all.Count, all.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count());
        var name = all.First(x => x.Name.Length > 3).Name;
        var matches = await _queries.SearchAsync(context, name, null, null, new(100));
        Assert.NotEmpty(matches.Items); Assert.Equal(name, matches.Items[0].Name, StringComparer.OrdinalIgnoreCase);
        var prefixed = await _queries.SearchAsync(context, name[..3], null, null, new(100));
        Assert.Contains(prefixed.Items, x => x.Key == matches.Items[0].Key);
        var typed = await _queries.SearchAsync(context, null, all[0].Kind, all[0].RepositoryId, new(100));
        Assert.All(typed.Items, x => { Assert.Equal(all[0].Kind, x.Kind); Assert.Equal(all[0].RepositoryId, x.RepositoryId); });
    }

    [Fact]
    public async Task SearchMatchesExistingGoldenViewForExactPrefixSubstringAndKeyQueries()
    {
        var context = await Snapshot();
        var index = ExplorerIndex.Load([Fixtures.ExpectedDirectory], null);
        var view = index.View(string.Join(',', context.Selection.Select(x => x.Key + ":" + x.Value)),
            new ViewScope(context.Capability, context.Selection.Keys.ToHashSet(StringComparer.Ordinal), context.Selection));
        foreach (var query in new[] { "", "Order", "ord", "total", "cs|", "ts|", "http|", "not-present" })
        {
            var expected = Queries.Search(view, query, null, null, int.MaxValue);
            var actual = new List<SearchHit>(); string? cursor = null;
            do
            {
                var page = await _queries.SearchAsync(context, query, null, null, new(7, cursor));
                actual.AddRange(page.Items); cursor = page.NextCursor;
            } while (cursor is not null);
            Assert.Equal(expected.OrderBy(x => x.Key, StringComparer.Ordinal), actual.OrderBy(x => x.Key, StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task LiteralSearchDoesNotTreatSqlWildcardsAsPatterns()
    {
        var context = await Snapshot();
        Assert.Empty((await _queries.SearchAsync(context, "%_does-not-exist", null, null, new(10))).Items);
        Assert.Empty((await _queries.SearchAsync(context, "' OR true --", null, null, new(10))).Items);
    }

    [Fact]
    public async Task PublishedPointerAdvanceDoesNotChangeOldPagesAndCursorScopeCannotChange()
    {
        var old = await Snapshot();
        var first = await _queries.SearchAsync(old, null, null, null, new(2));
        Assert.NotNull(first.NextCursor);
        var newer = await Snapshot(old.Capability, 2);
        Assert.NotEqual(old.SnapshotId, newer.SnapshotId);
        var resolvedOld = await _queries.ResolveAsync(old.Capability, snapshotId: old.SnapshotId);
        Assert.Equal(old.SnapshotId, resolvedOld!.SnapshotId);
        Assert.NotEmpty((await _queries.SearchAsync(resolvedOld, null, null, null, new(2, first.NextCursor))).Items);
        await Assert.ThrowsAsync<ArgumentException>(() => _queries.SearchAsync(newer, null, null, null, new(2, first.NextCursor)));
        await Assert.ThrowsAsync<ArgumentException>(() => _queries.SearchAsync(old, "other", null, null, new(2, first.NextCursor)));
        Assert.Null(await _queries.ResolveAsync("another-capability", snapshotId: old.SnapshotId));
    }

    [Fact]
    public async Task MetadataRevisionsArtifactsAndSymbolEvidenceAreDatabaseOnly()
    {
        var context = await Snapshot();
        Assert.Equal("published", (await _queries.MetadataAsync(context)).Status);
        Assert.NotEmpty((await _queries.RevisionsAsync(context, new(1))).Items);
        var artifacts = await _queries.ArtifactsAsync(context, new(1));
        Assert.Single(artifacts.Items); Assert.NotNull(artifacts.NextCursor);
        var hit = (await _queries.SearchAsync(context, null, null, null, new(1))).Items[0];
        var node = await _queries.SymbolAsync(context, hit.Key); Assert.NotNull(node);
        var evidence = await _queries.EvidenceAsync(context, hit.Key, new(1)); Assert.Single(evidence.Items);
        var e = evidence.Items[0]; Assert.True(await _queries.IsCitedAsync(context, e.RepositoryId, e.CommitSha, e.Path));
        Assert.False(await _queries.IsCitedAsync(context, e.RepositoryId, e.CommitSha, "not-cited.txt"));
        Assert.Null(await _queries.SymbolAsync(context, "missing"));
        foreach (var direction in new[] { "incoming", "outgoing" })
            Assert.InRange((await _queries.EdgesAsync(context, hit.Key, direction, new(1))).Items.Count, 0, 1);
        await Assert.ThrowsAsync<ArgumentException>(() => _queries.EdgesAsync(context, hit.Key, "bad", new(1)));
    }

    [Fact]
    public async Task CalculationsAreMetadataPagesAndBodyIsExplicitDetail()
    {
        var context = await Snapshot();
        var page = await _queries.CalculationsAsync(context, new(1)); Assert.Single(page.Items);
        var detail = await _queries.CalculationAsync(context, page.Items[0].Key); Assert.NotNull(detail);
        Assert.Equal(page.Items[0].Key, detail.Metadata.Key); Assert.True(detail.Body.TryGetProperty("expr", out _));
        Assert.Null(await _queries.CalculationAsync(context, "missing"));
    }

    [Fact]
    public async Task UnknownSelectionAndCancellationRejectWithoutFileFallback()
    {
        var context = await Snapshot();
        await Assert.ThrowsAsync<ArgumentException>(() => _queries.ResolveAsync(context.Capability, new Dictionary<string, string> { ["outside"] = new('a', 40) }));
        var repo = context.Selection.Keys.First();
        await Assert.ThrowsAsync<ArgumentException>(() => _queries.ResolveAsync(context.Capability, new Dictionary<string, string> { [repo] = new('f', 40) }));
        await Assert.ThrowsAsync<ArgumentException>(() => _queries.SearchAsync(context, new string('a', 501), null, null, new(50)));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _queries.SearchAsync(context, null, null, null, new(50), cancelled.Token));
    }

    [Fact]
    public async Task CandidateSnapshotCannotBeResolvedForExplorerReads()
    {
        var capability = "candidate-" + Guid.NewGuid().ToString("N");
        var validation = ArtifactValidator.Validate(Fixtures.Golden("web-ts/v1-base/_fixture_web.jsonl"));
        Assert.True(validation.IsValid);
        var artifact = await new ArtifactImporter(db.DataSource).ImportAsync(validation.Artifact!);
        var candidate = await new SnapshotStore(db.DataSource).CreateAsync(
            capability, "unconfirmed-" + Guid.NewGuid().ToString("N"), [artifact.ArtifactId]);

        Assert.Equal("candidate", candidate.Status);
        Assert.Null(await _queries.ResolveAsync(capability));
        Assert.Null(await _queries.ResolveAsync(capability, snapshotId: candidate.Id));
    }

    [Fact]
    public async Task OversizedUtf8NodeProjectionFailsBeforeMaterializingRows()
    {
        string key = "";
        var context = await Snapshot(mutate: records =>
        {
            key = records.First(record => record["kind"]!.GetValue<string>() == "calculation")["symbolKey"]!.GetValue<string>();
            records.First(record => record["kind"]!.GetValue<string>() == "node" && record["symbolKey"]!.GetValue<string>() == key)["name"] = new string('界', 4000);
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _queries.SearchAsync(context, null, null, "web-ts", new(100)));
        Assert.Contains("projection exceeds", error.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _queries.SymbolAsync(context, key));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _queries.CalculationsAsync(context, new(100)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _queries.CalculationAsync(context, key));
    }

    [Fact]
    public async Task OversizedUnitMetadataFailsArtifactAndSearchProjection()
    {
        var context = await Snapshot(mutate: records => records[0]["unit"]!["name"] = new string('界', 4000));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _queries.ArtifactsAsync(context, new(100)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _queries.SearchAsync(context, null, null, "web-ts", new(100)));
    }

    [Fact]
    public async Task OversizedEdgeProjectionFailsInsteadOfSilentlySkipping()
    {
        string key = "";
        var context = await Snapshot(mutate: records =>
        {
            var edge = records.First(record => record["kind"]!.GetValue<string>() == "edge");
            var original = edge["from"]!.GetValue<string>();
            var node = records.First(record => record["kind"]!.GetValue<string>() == "node" && record["symbolKey"]!.GetValue<string>() == original).DeepClone().AsObject();
            key = "ts|web-ts|@fixture/web|large.ts|" + new string('界', 1900) + "#value";
            node["symbolKey"] = key; node["id"] = "n:sizeguard";
            records.Insert(records.Count - 1, node);
            records[^1]["counts"]!["node"] = records[^1]["counts"]!["node"]!.GetValue<int>() + 1;
            edge["from"] = key;
            edge["to"] = "ts|@ext|benchmark|large.ts|" + new string('界', 1900) + "#value";
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => _queries.EdgesAsync(context, key, "outgoing", new(100)));
    }

    [Fact]
    public async Task OversizedEvidencePathProjectionFailsBeforeTransferringPath()
    {
        string key = "";
        var context = await Snapshot(mutate: records =>
        {
            var node = records.First(record => record["kind"]!.GetValue<string>() == "node");
            key = node["symbolKey"]!.GetValue<string>();
            var evidenceId = node["evidence"]![0]!.GetValue<string>();
            records.First(record => record["kind"]!.GetValue<string>() == "evidence" && record["id"]!.GetValue<string>() == evidenceId)["path"] = new string('界', 4000) + ".ts";
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => _queries.EvidenceAsync(context, key, new(100)));
    }

    [Fact]
    public async Task MissingImmutableDefaultCannotFallBackToLexicographicRevision()
    {
        var context = await Snapshot();
        await using var command = db.DataSource.CreateCommand("update snapshot_revision set is_default=false where snapshot_id=@snapshot");
        command.Parameters.AddWithValue("snapshot", context.SnapshotId);
        await command.ExecuteNonQueryAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _queries.ResolveAsync(context.Capability));
        Assert.Contains("default revision", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnicodeSearchMatchesOrdinalCaseOracle()
    {
        var observations = new List<string>(); var mismatches = new List<string>();
        foreach (var (name, query) in new (string Name, string Query)[]
        {
            ("Σ", "ς"), ("ς", "σ"), ("İ", "i"), ("İ", "I"), ("ı", "I"),
            ("K", "k"), ("K", "K"), ("ſ", "S"), ("ß", "ẞ"), ("σ", "Σ"),
        })
        {
            string key = "";
            var context = await Snapshot(mutate: records =>
            {
                var node = records.First(record => record["kind"]!.GetValue<string>() == "node");
                key = node["symbolKey"]!.GetValue<string>(); node["name"] = name;
            });
            var expected = name.Equals(query, StringComparison.OrdinalIgnoreCase);
            var expectedRank = expected ? 4 : name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 3
                : name.Contains(query, StringComparison.OrdinalIgnoreCase) ? 2 : key.Contains(query, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            var actual = (await _queries.SearchAsync(context, query, null, "web-ts", new(100))).Items.Any(hit => hit.Key == key);
            await using var command = db.DataSource.CreateCommand("""
                select lower(n.name)=lower(@query),upper(n.name)=upper(@query),
                  case when s.name_fold=@fold then 4 when starts_with(s.name_fold,@fold) then 3
                  when strpos(s.name_fold,@fold)>0 then 2 when strpos(s.symbol_fold,@fold)>0 then 1 else 0 end
                from analysis_snapshot_artifact a join node n on n.artifact_id=a.artifact_id
                join node_search s on s.artifact_id=n.artifact_id and s.local_id=n.local_id
                where a.snapshot_id=@snapshot and n.symbol_key=@key and s.normalizer_id=@normalizer
                """);
            command.Parameters.AddWithValue("query", query); command.Parameters.AddWithValue("key", key);
            command.Parameters.AddWithValue("snapshot", context.SnapshotId);
            command.Parameters.AddWithValue("normalizer", NodeSearchNormalization.NormalizerId);
            command.Parameters.AddWithValue("fold", NodeSearchNormalization.Fold(query));
            await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
            var observation = $"name={name},query={query}: OrdinalIgnoreCase={expected}, queryHit={actual}, expectedRank={expectedRank}, actualRank={reader.GetInt32(2)}, PostgreSQL lower={reader.GetBoolean(0)}, upper={reader.GetBoolean(1)}, .NET upper={name.ToUpperInvariant().Equals(query.ToUpperInvariant(), StringComparison.Ordinal)}";
            observations.Add(observation); if (reader.GetInt32(2) != expectedRank || actual != (expectedRank > 0)) mismatches.Add(observation);
        }
        Assert.True(mismatches.Count == 0, "Unicode search differs from the existing OrdinalIgnoreCase contract:\n" + string.Join("\n", observations));
    }

    [Fact]
    public async Task SearchUsesOrdinalIgnoreCaseSortAcrossSupplementaryAndBmpNames()
    {
        var keys = new List<string>();
        var names = new[] { "\uE000", "\U00010000" };
        var context = await Snapshot(mutate: records =>
        {
            var nodes = records.Where(record => record["kind"]!.GetValue<string>() == "node").Take(2).ToList();
            for (var i = 0; i < 2; i++) { keys.Add(nodes[i]["symbolKey"]!.GetValue<string>()); nodes[i]["name"] = names[i]; }
        });
        var actual = new List<SearchHit>(); string? cursor = null;
        do
        {
            var page = await _queries.SearchAsync(context, "", null, "web-ts", new(1, cursor));
            actual.AddRange(page.Items); cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(names.Order(StringComparer.OrdinalIgnoreCase), actual.Where(hit => keys.Contains(hit.Key, StringComparer.Ordinal)).Select(hit => hit.Name));
    }

    [Fact]
    public async Task CanonicalWinnerIsChosenBeforeFilteringAcrossDuplicateUnitDeclarations()
    {
        var records = Fixtures.Golden("web-ts/v1-base/_fixture_web.jsonl").TrimEnd('\n').Split('\n').Select(line => JsonNode.Parse(line)!.AsObject()).ToList();
        var node = records.First(record => record["kind"]!.GetValue<string>() == "node");
        var key = node["symbolKey"]!.GetValue<string>(); var originalName = node["name"]!.GetValue<string>();
        var originalKind = node["nodeKind"]!.GetValue<string>();
        records[0]["unit"]!["path"] = "000-first-tsconfig.json";
        records[0]["configHash"] = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        node["name"] = "CanonicalWinner"; node["nodeKind"] = originalKind == "Function" ? "Method" : "Function";
        var context = await Snapshot(additionalJsonl: string.Join('\n', records.Select(record => record.ToJsonString())) + "\n");
        Assert.Equal("CanonicalWinner", (await _queries.SymbolAsync(context, key))!.Name);
        var winner = await _queries.SearchAsync(context, "CanonicalWinner", null, "web-ts", new(100));
        Assert.Single(winner.Items, hit => hit.Key == key);
        var shadowed = await _queries.SearchAsync(context, originalName, null, "web-ts", new(100));
        Assert.All(shadowed.Items.Where(hit => hit.Key == key), hit => Assert.Equal("CanonicalWinner", hit.Name));
        var shadowedKind = await _queries.SearchAsync(context, "", originalKind, "web-ts", new(100));
        Assert.DoesNotContain(shadowedKind.Items, hit => hit.Key == key);
    }

    [Fact]
    public async Task MissingArtifactProjectionMarkerFailsClosedWithoutLoadingNodes()
    {
        var context = await Snapshot();
        await using var command = db.DataSource.CreateCommand("delete from artifact_search_projection where artifact_id in(select artifact_id from analysis_snapshot_artifact where snapshot_id=@snapshot)");
        command.Parameters.AddWithValue("snapshot", context.SnapshotId); await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<ExplorerProjectionUnavailableException>(() => _queries.ResolveAsync(context.Capability));
    }

    [Fact]
    public async Task MissingNodeProjectionDespiteMarkerFailsExplicitlyWhenQueried()
    {
        string key = "";
        var context = await Snapshot(mutate: records =>
        {
            var node = records.First(record => record["kind"]!.GetValue<string>() == "node");
            key = node["symbolKey"]!.GetValue<string>(); node["name"] = "UniqueProjectionTest";
        });
        var symbol = await _queries.SymbolAsync(context, key);
        await using var command = db.DataSource.CreateCommand("delete from node_search where artifact_id=@artifact and local_id=(select local_id from node where artifact_id=@artifact and symbol_key=@key)");
        command.Parameters.AddWithValue("artifact", symbol!.ArtifactId); command.Parameters.AddWithValue("key", key);
        await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<ExplorerProjectionUnavailableException>(() => _queries.SearchAsync(context, "absent-query", null, "web-ts", new(1)));
    }

    [Fact]
    public async Task MixedUnicodeNamesHaveTheSameOrderingAsOrdinalIgnoreCaseAcrossPages()
    {
        var keys = new List<string>();
        var names = new[] { "a", "Σ", "ς", "İ", "K", "ſ", "ß", "\uE000", "\U00010000" };
        var context = await Snapshot(mutate: records =>
        {
            var nodes = records.Where(record => record["kind"]!.GetValue<string>() == "node").ToList();
            Assert.Equal(names.Length, nodes.Count);
            for (var i = 0; i < names.Length; i++) { keys.Add(nodes[i]["symbolKey"]!.GetValue<string>()); nodes[i]["name"] = names[i]; }
        });
        var actual = new List<SearchHit>(); string? cursor = null;
        do
        {
            var page = await _queries.SearchAsync(context, "", null, "web-ts", new(1, cursor));
            actual.AddRange(page.Items); cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(names.Length, actual.Count);
        Assert.Equal(names.Length, actual.Select(hit => hit.Key).Distinct(StringComparer.Ordinal).Count());
        for (var i = 1; i < actual.Count; i++)
            Assert.True(StringComparer.OrdinalIgnoreCase.Compare(actual[i - 1].Name, actual[i].Name) <= 0, $"Incorrect order: {actual[i - 1].Name} before {actual[i].Name}");
    }

}
