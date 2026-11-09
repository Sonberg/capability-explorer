using System.Text.Json;
using System.Text.Json.Nodes;
using CapabilityExplorer.Binding;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Explorer;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class UnregisteredHttpTargetCandidateTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string Caller = "cs|consumer|Client.csproj|M:Synthetic.Client.Send";
    private const string Destination = "@unbound:consumer/Client.csproj:config=Orders";
    private static readonly HttpTargetMembership[] Membership =
        [new("shop", "Shop", "consumer"), new("portal", "Portal", "consumer")];

    // 102-B: Given repeated supported calls and two memberships, when calculated,
    // then details retain exact occurrences and selection-wide distinct operations.
    [Fact]
    public void Issue102_B_Given_repeated_calls_When_selected_Then_details_are_selection_wide()
    {
        var artifact = Source(calls: 3);
        var result = Build([artifact]);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("no-known-provider", candidate.Status);
        Assert.Equal("Orders", candidate.Label);
        Assert.Equal(2, candidate.Consumers.Count);
        Assert.Equal(["GET /orders"], candidate.Operations);
        Assert.Equal(3, candidate.Occurrences.Count);
        Assert.NotEmpty(candidate.Reasons);
        Assert.NotEmpty(candidate.MissingEvidence);
        Assert.Empty(candidate.Alternatives);
        Assert.All(candidate.Occurrences, occurrence =>
        {
            Assert.Equal(artifact.Artifact.ContentSha256, occurrence.Source.ArtifactHash);
            Assert.Equal(artifact.From.CommitSha, occurrence.Source.CommitSha);
            Assert.Equal("Synthetic.cs", Assert.Single(occurrence.Evidence).Path);
            Assert.Equal(new string('c', 64), occurrence.Evidence[0].SnippetSha256);
        });
    }

    // 102-C: Given equal host/config/prefix text, when grouped, then caller and unit
    // boundaries remain separate without ownership evidence.
    [Fact]
    public void Issue102_C_Given_shared_host_and_names_When_grouped_Then_scopes_do_not_merge()
    {
        var first = Source();
        var second = Source(unit: "Other.csproj", destination: "@unbound:consumer/Other.csproj:config=Orders");
        var third = Source(caller: Caller + "Other", destination: "@unbound:consumer/Client.csproj:url=https://api.example.test/api-one");
        var fourth = Source(caller: Caller + "Other2", destination: "@unbound:consumer/Client.csproj:url=https://api.example.test/api-two");
        var combined = Combine(first, third, fourth);
        var result = Build([combined, second]);
        Assert.Equal(4, result.Candidates.Count);
        Assert.Equal(4, result.Candidates.Select(candidate => candidate.Id).Distinct().Count());
        Assert.Contains(result.Candidates, candidate => candidate.Destination.EndsWith("/api-one", StringComparison.Ordinal));
        Assert.Contains(result.Candidates, candidate => candidate.Destination.EndsWith("/api-two", StringComparison.Ordinal));
    }

    // 102-D: Given ambiguous providers, a broken map, dynamic requests and partial
    // provider observations, when calculated, then absence is never inferred.
    [Fact]
    public void Issue102_D_Given_uncertain_targets_When_calculated_Then_outcomes_remain_distinct()
    {
        var request = Source();
        var provider = Source(repo: "provider", endpoint: true);
        var another = Source(repo: "another", endpoint: true);
        var membership = Membership.Concat([new HttpTargetMembership("p", "Provider", "provider"),
            new HttpTargetMembership("a", "Another", "another")]).ToArray();
        var ambiguous = Build([request, provider, another], membership);
        Assert.Empty(ambiguous.Candidates);
        Assert.Equal("ambiguous-known-providers", Assert.Single(ambiguous.Outcomes).Status);
        Assert.Equal(2, ambiguous.Outcomes[0].Alternatives.Count);
        var broken = Build([request], bindings: BindingFile.Parse(JsonSerializer.Serialize(new
        {
            bindings = new[] { new { hint = Destination, target = "provider/Client.csproj" } },
        })));
        Assert.Empty(broken.Candidates);
        Assert.Equal("broken-binding", Assert.Single(broken.Outcomes).Status);
        var dynamic = Build([Source(dynamic: true)]);
        Assert.Empty(dynamic.Candidates);
        Assert.Equal("dynamic/insufficient-evidence", Assert.Single(dynamic.Outcomes).Status);
        var partial = Build([request, Source(repo: "provider", calls: 0, partial: true)], membership);
        Assert.Empty(partial.Candidates);
        Assert.Equal("partial-provider-coverage", Assert.Single(partial.Outcomes).Status);
        Assert.Contains("another", partial.ProviderCoverage.MissingRepositories);
        var unsupported = Build([Source(operation: "TRACE /orders")]);
        Assert.Empty(unsupported.Candidates);
        Assert.Equal("dynamic/insufficient-evidence", Assert.Single(unsupported.Outcomes).Status);
    }

    [Fact]
    public void Issue102_D_Given_an_exact_provider_When_routes_overlap_Then_existing_exact_matching_wins()
    {
        var result = Build([Source(destination: "provider/Client.csproj"), Source(repo: "provider", endpoint: true),
            Source(repo: "another", endpoint: true)]);
        Assert.Empty(result.Candidates);
        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal("known-provider", outcome.Status);
        Assert.Equal("provider", Assert.Single(outcome.Alternatives).Source.RepositoryId);
    }

    // 102-E: Given equivalent order and changed exact inputs, when refreshed,
    // then identities are deterministic and result identity invalidates old details.
    [Fact]
    public void Issue102_E_Given_reordered_or_changed_inputs_When_refreshed_Then_identity_is_source_bound()
    {
        var source = Source();
        var other = Source(unit: "Other.csproj", destination: "@unbound:consumer/Other.csproj:config=Orders");
        var baseline = Build([source, other]);
        Assert.NotEmpty(baseline.ResultId);
        Assert.Equal(JsonSerializer.Serialize(baseline, Web),
            JsonSerializer.Serialize(Build([other, source], Membership.Reverse().ToArray()), Web));
        var revisedOther = Source(unit: "Other.csproj", destination: "@unbound:consumer/Other.csproj:config=Orders", revision: 'd');
        Assert.NotEqual(baseline.ResultId, Build([Source(revision: 'd'), revisedOther]).ResultId);
        Assert.NotEqual(baseline.ResultId, Build([Source(calls: 2), other]).ResultId);
        Assert.NotEqual(baseline.ResultId, Build([source, other], Membership[..1]).ResultId);
        Assert.NotEqual(baseline.ResultId, Build([source, other], bindings: BindingFile.Parse("{\"bindings\":[],\"version\":1}")).ResultId);
        Assert.NotEqual(baseline.ResultId, UnregisteredHttpTargetCandidates.Build([source, other], Membership, BindingFile.Empty, "next-rule").ResultId);
        Assert.Equal(baseline.Candidates.Select(candidate => candidate.Id),
            Build([Source(revision: 'd'), revisedOther]).Candidates.Select(candidate => candidate.Id));
    }

    // 102-F: Given more than 100 supported target groups, when projected/hidden,
    // then graph limits and totals are honest and supported raw hosts are suppressed.
    [Fact]
    public void Issue102_F_Given_many_candidates_When_bounded_or_hidden_Then_projection_is_honest()
    {
        var artifact = Combine(Enumerable.Range(0, 120).Select(i => Source(caller: Caller + i,
            destination: $"@unbound:consumer/Client.csproj:config=Orders{i}")).ToArray());
        WithView([artifact], view =>
        {
            var graph = Architecture.Build(view, "system", null, view, Capabilities(), null);
            Assert.Equal(100, graph.Nodes.Count);
            Assert.True(graph.Budget!.Truncated);
            Assert.Equal(121, graph.Budget.TotalNodes);
            Assert.Equal(120, graph.Budget.TotalEdges);
            Assert.DoesNotContain(graph.Nodes, node => node.Category is "host" or "unmatched");
            var metadata = JsonSerializer.SerializeToElement(graph, Web).GetProperty("unregisteredHttpTargets");
            Assert.Equal(120, metadata.GetProperty("totalCandidates").GetInt32());
            Assert.True(metadata.GetProperty("detailsTruncated").GetBoolean());
            Assert.True(metadata.GetProperty("details").GetArrayLength() <= 100);
            var hidden = Architecture.Build(view, "system", null, view, Capabilities(), null,
                hideKinds: ["unregistered-http-target"]);
            Assert.Single(hidden.Nodes);
            Assert.Empty(hidden.Edges);
            var discovery = JsonSerializer.SerializeToElement(hidden, Web).GetProperty("unregisteredHttpTargets");
            Assert.Equal(120, discovery.GetProperty("totalCandidates").GetInt32());
            Assert.Equal(0, discovery.GetProperty("shownCandidates").GetInt32());
            Assert.Contains(hidden.Facets!.Kinds, facet => facet.Value == "unregistered-http-target" && facet.Total == 120);
        });
    }

    // 102-G: Given a supported group plus dynamic and known-external calls, when
    // inspected through the existing requests drill, then input facts/maps stay unchanged.
    [Fact]
    public void Issue102_G_Given_candidate_and_existing_targets_When_inspected_Then_projection_is_read_only()
    {
        var source = Combine(Source(), Source(dynamic: true), Source(operation: "GET /known", caller: Caller + "Known"));
        var external = Source(repo: "external", endpoint: true, operation: "GET /known");
        var before = JsonSerializer.Serialize(source.Artifact, Web);
        WithView([source, external], view =>
        {
            var graph = Architecture.Build(view, "system", null, view, Capabilities(), null);
            var candidate = Assert.Single(graph.Nodes, node => node.Category == "unregistered-http-target");
            Assert.Null(candidate.Group);
            Assert.Null(candidate.RepositoryId);
            Assert.Contains("Unregistered target", candidate.Badges);
            Assert.Equal("requests", candidate.Drill!.Level);
            Assert.Contains(graph.Nodes, node => node.Category == "unmatched");
            Assert.Contains(graph.Edges, edge => edge.To == "repo:external" && edge.Certainty == "candidate");
            var detail = Architecture.Build(view, "requests", candidate.Id, view, Capabilities(), null);
            Assert.NotEmpty(detail.Edges.SelectMany(edge => edge.Facts).SelectMany(fact => fact.Evidence));
            Assert.Equal(before, JsonSerializer.Serialize(source.Artifact, Web));
            Assert.Empty(view.Index.Bindings.Entries);
        });
    }

    [Fact]
    public void Issue102_C_Given_extractor_unit_name_and_base_hint_When_grouped_Then_real_scopes_are_supported()
    {
        var original = Source();
        var artifact = Load(original.Artifact.Header with { Unit = new("src/Client.csproj", "Client") },
            original.Artifact.Nodes.ToArray(), original.Artifact.Edges.Select(edge => edge with
            {
                To = "http|@unbound:consumer/Client:base=https://api.example.test/orders|GET /orders",
            }).ToArray(), original.Artifact.Evidence, false);
        var result = Build([artifact]);
        Assert.Equal("api.example.test/orders", Assert.Single(result.Candidates).Label);
        Assert.Equal("src/Client.csproj", result.Candidates[0].UnitPath);
    }

    [Fact]
    public void Issue102_C_Given_host_only_evidence_When_operations_differ_Then_identity_undermerges()
    {
        var original = Combine(Source(destination: "@unbound:consumer/Client.csproj:url=https://api.example.test", operation: "GET /api-one"),
            Source(destination: "@unbound:consumer/Client.csproj:url=https://api.example.test", operation: "GET /api-two"));
        var artifact = Load(original.Artifact.Header, original.Artifact.Nodes.ToArray(),
            original.Artifact.Edges.Select(edge => edge with { Props = null }).ToArray(), original.Artifact.Evidence, false);
        Assert.Equal(2, Build([artifact]).Candidates.Count);
        var unknown = Load(original.Artifact.Header, original.Artifact.Nodes.ToArray(),
            original.Artifact.Edges.Take(1).Select(edge => edge with { To = "http|@unbound:consumer/Client.csproj:client=unknown|GET /api-one" }).ToArray(),
            original.Artifact.Evidence, false);
        Assert.Empty(Build([unknown]).Candidates);
    }

    [Fact]
    public void Issue102_C_Given_extracted_host_hints_When_projected_Then_operations_remain_scoped()
    {
        var source = Combine(Source(destination: "@unbound:consumer/Client.csproj:host=api.example.test", operation: "GET /api-one", calls: 3),
            Source(destination: "@unbound:consumer/Client.csproj:host=api.example.test", operation: "GET /api-two"));
        var candidates = Build([source]).Candidates;
        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, candidate => Assert.Equal("api.example.test", candidate.Label));
        Assert.Equal([1, 3], candidates.Select(candidate => candidate.Occurrences.Count).Order());
        Assert.All(candidates, candidate => Assert.Single(candidate.Operations));
        WithView([source], view =>
        {
            var graph = Architecture.Build(view, "system", null, view, Capabilities(), null);
            Assert.DoesNotContain(graph.Nodes, node => node.Category is "host" or "unmatched");
            Assert.Equal(2, graph.UnregisteredHttpTargets!.TotalCandidates);
        });
    }

    [Fact]
    public void Issue102_E_Given_a_changed_source_scope_When_refreshed_Then_result_identity_changes()
    {
        WithView([Source(), Source(repo: "other", destination: "@unbound:other/Client.csproj:config=Orders")], all =>
        {
            var scope = all.Index.View(null, new ViewScope("shop", new HashSet<string>(["consumer"], StringComparer.Ordinal),
                new Dictionary<string, string>()));
            var full = Architecture.Build(all, "system", null, all, Capabilities(), null);
            var scoped = Architecture.Build(scope, "system", null, all, Capabilities(), null);
            Assert.NotEqual(full.UnregisteredHttpTargets!.ResultId, scoped.UnregisteredHttpTargets!.ResultId);
            Assert.Equal(2, full.UnregisteredHttpTargets.TotalCandidates);
            Assert.Equal(1, scoped.UnregisteredHttpTargets.TotalCandidates);
        });
    }

    [Fact]
    public void Issue102_F_Given_overlay_edges_after_budgeting_When_finalized_Then_hard_caps_and_totals_survive()
    {
        var nodes = Enumerable.Range(0, 100).Select(i => new ArchNode($"n{i}", "module", $"n{i}", null, null, null, null, null, [])).ToArray();
        var source = new ArchitectureGraph("system", null, "Synthetic", [], nodes,
            Enumerable.Range(1, 99).Select(i => new ArchEdge($"edge{i}", "n0", $"n{i}", "http", "calls", 1, "candidate", [])).ToArray(), []);
        var graph = ArchitectureBudgeting.Apply(source);
        var overlaid = graph with
        {
            Nodes = [.. graph.Nodes, new("overlay", "repository", "Overlay", null, null, null, null, null, [])],
            Edges = [.. graph.Edges, new("overlay1", "n0", "overlay", "http", "manual", 1, "human", []),
                new("overlay2", "n1", "overlay", "http", "automatic", 1, "candidate", [])],
        };
        var directory = Path.Combine(AppContext.BaseDirectory, "issue102-final-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var final = new ArchitectureConnections(Path.Combine(directory, "capabilities.json")).Apply(overlaid, []);
            Assert.True(final.Nodes.Count <= 100);
            Assert.True(final.Edges.Count <= 100);
            Assert.Equal(101, final.Budget!.TotalNodes);
            Assert.Equal(101, final.Budget.TotalEdges);
            Assert.True(final.Budget.Truncated);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Issue102_B_Given_many_occurrences_When_detail_is_bounded_Then_queries_and_pins_recover_them()
    {
        WithView([Source(calls: 160)], view =>
        {
            var graph = Architecture.Build(view, "system", null, view, Capabilities(), null, edgeBudget: 1);
            var metadata = graph.UnregisteredHttpTargets!;
            var candidate = Assert.Single(metadata.Details);
            Assert.Equal(160, candidate.OccurrenceCount);
            Assert.Equal(40, candidate.Occurrences.Count);
            Assert.Equal(1, candidate.OperationCount);
            var drill = Architecture.Build(view, "requests", candidate.Id, view, Capabilities(), null);
            Assert.True(drill.Budget!.Truncated);
            Assert.Equal(162, drill.Budget.TotalNodes);
            var omitted = drill.Omitted!.Nodes.First(node => node.Category == "http-request");
            var recovered = Architecture.Build(view, "requests", candidate.Id, view, Capabilities(), null,
                nodeBudget: 2, include: [omitted.Id]);
            Assert.Contains(recovered.Nodes, node => node.Id == omitted.Id);
            Assert.Contains(Assert.Single(recovered.UnregisteredHttpTargets!.Details).Occurrences, occurrence => occurrence.Occurrence.Id == omitted.Id);
            Assert.Equal(160, recovered.UnregisteredHttpTargets.Details[0].OccurrenceCount);
        });
    }

    private static UnregisteredHttpTargetResult Build((LoadedFrom From, Artifact Artifact)[] sources,
        HttpTargetMembership[]? membership = null, BindingFile? bindings = null) =>
        UnregisteredHttpTargetCandidates.Build(sources, membership ?? Membership, bindings ?? BindingFile.Empty);

    private static CapabilityStatus[] Capabilities() => Membership.Select(member => new CapabilityStatus(
        member.CapabilityId, member.CapabilityName, null, null,
        [new RepositoryStatus("consumer", "", "git", "main", true, new string('a', 40), null, null, null, true, [])])).ToArray();

    private static void WithView((LoadedFrom From, Artifact Artifact)[] sources, Action<ViewIndex> action)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "issue102-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var source in sources)
                File.WriteAllText(Path.Combine(directory, $"{source.From.RepositoryId}-{source.From.UnitPath}.jsonl"), Text(source.Artifact));
            var index = ExplorerIndex.Load([directory], null);
            Assert.Empty(index.Problems);
            action(index.View(null));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static (LoadedFrom From, Artifact Artifact) Source(string repo = "consumer", string unit = "Client.csproj",
        string? destination = Destination, string caller = Caller, string operation = "GET /orders",
        int calls = 1, bool endpoint = false, bool dynamic = false, bool partial = false, bool noEvidence = false, char revision = 'a')
    {
        var commit = new string(revision, 40);
        var key = endpoint ? $"http|{repo}/{unit}|{operation}" : caller.Replace("consumer", repo, StringComparison.Ordinal);
        var evidence = new EvidenceRecord("ev:0000000000000001", repo, commit, "Synthetic.cs", new(1, 1, 1, 2),
            new string('c', 64), new("synthetic", "1"));
        var header = new HeaderRecord(repo, commit, new(unit, unit), "csharp", new("synthetic", "1"), [],
            new string('b', 64), new string('b', 64));
        var node = new NodeRecord("n:0000000000000001", key, endpoint ? "Endpoint" : "Method", "Send", null, null,
            endpoint ? JsonSerializer.SerializeToElement(new { method = operation.Split(' ')[0], route = operation.Split(' ')[1] }) : null,
            [evidence.Id]);
        var edges = endpoint ? Array.Empty<EdgeRecord>() : Enumerable.Range(0, calls).Select(i => new EdgeRecord(
            $"e:{i + 1:x16}", key, "REQUESTS", dynamic ? null : $"http|{destination}|{operation}", "rule",
            "unresolved", dynamic ? "dynamic-string" : "configuration", new("synthetic.http", "1"),
            dynamic ? null : JsonSerializer.SerializeToElement(new { client = "OrdersClient" }), noEvidence ? [] : [evidence.Id])).ToArray();
        return Load(header, [node], edges, [evidence], partial);
    }

    private static (LoadedFrom From, Artifact Artifact) Combine(params (LoadedFrom From, Artifact Artifact)[] sources)
    {
        var first = sources[0].Artifact;
        var edges = sources.SelectMany(source => source.Artifact.Edges).Select((edge, i) => edge with { Id = $"e:{i + 1:x16}" }).ToArray();
        var nodes = sources.SelectMany(source => source.Artifact.Nodes).DistinctBy(node => node.SymbolKey)
            .Select((node, i) => node with { Id = $"n:{i + 1:x16}" }).ToArray();
        return Load(first.Header, nodes, edges, first.Evidence, first.Complete.Status == "partial");
    }

    private static (LoadedFrom From, Artifact Artifact) Load(HeaderRecord header, NodeRecord[] nodes,
        EdgeRecord[] edges, IReadOnlyList<EvidenceRecord> evidence, bool partial)
    {
        var complete = new CompleteRecord(partial ? "partial" : "full", new(nodes.Length, edges.Length, evidence.Count, 0, 0),
            new(partial ? 2 : 1, 1, partial ? ["Missing.cs"] : [], edges.Length));
        var text = Framed(header, nodes, edges, evidence, complete);
        var report = ArtifactValidator.Validate(text);
        Assert.True(report.IsValid, string.Join("; ", report.Problems));
        return (new(header.RepositoryId, header.CommitSha, header.Unit.Path), report.Artifact!);
    }

    private static string Text(Artifact artifact) => Framed(artifact.Header, artifact.Nodes, artifact.Edges, artifact.Evidence, artifact.Complete);

    private static string Framed(HeaderRecord header, IReadOnlyList<NodeRecord> nodes, IReadOnlyList<EdgeRecord> edges,
        IReadOnlyList<EvidenceRecord> evidence, CompleteRecord complete)
    {
        JsonObject Record(object value, string kind)
        {
            var result = JsonSerializer.SerializeToNode(value, Web)!.AsObject();
            foreach (var property in result.Where(property => property.Value is null).Select(property => property.Key).ToArray())
                result.Remove(property);
            result["schema"] = "ce.v1";
            result["kind"] = kind;
            return result;
        }
        return string.Join('\n', new[] { Record(header, "header") }.Concat(evidence.Select(item => Record(item, "evidence")))
            .Concat(nodes.Select(item => Record(item, "node"))).Concat(edges.Select(item => Record(item, "edge")))
            .Append(Record(complete, "complete")).Select(item => item.ToJsonString())) + "\n";
    }
}
