using System.Text.Json;
using System.Text.Json.Nodes;
using CapabilityExplorer.Evaluation;
using Xunit;

namespace CapabilityExplorer.Tests;

/// <summary>
/// Each test starts from "actual = golden" (a perfect extractor), breaks one thing and checks that the
/// evaluator reports exactly that, with the right metric moving.
/// </summary>
public sealed class EvaluatorTests
{
    private const string Api = "api-cs/v1-base/Sales.Api.jsonl";
    private const string Broken = "worker-cs/v2-broken/Sales.Worker.jsonl";
    private const string Pricing = "pricing-node/v1-base/_fixture_pricing.jsonl";
    private const string ApiCalc = "cs|api-cs|Sales.Api|M:Sales.Api.Calculations.WeeklySalesCalculator.AveragePerCalendarWeek(System.Collections.Generic.IReadOnlyList{Sales.Domain.SaleRow},System.DateOnly,System.DateOnly)";

    private static readonly IReadOnlyList<ArtifactFile> Golden = ArtifactFile.LoadDirectory(Fixtures.ExpectedDirectory);
    private static readonly EvaluationConfig Config = EvaluationConfig.Parse(File.ReadAllText(Path.Combine(Fixtures.RepositoryRoot, "fixtures", "evaluation.json")));

    private static List<JsonObject> Records(string file) =>
        Golden.Single(g => g.Path == file).Content.TrimEnd('\n').Split('\n').Select(l => JsonNode.Parse(l)!.AsObject()).ToList();

    private static JsonObject First(List<JsonObject> records, string kind, Func<JsonObject, bool>? where = null) =>
        records.First(r => (string)r["kind"]! == kind && (where?.Invoke(r) ?? true));

    /// <summary>Applies a change and recomputes the counts so the artifact stays contract-valid.</summary>
    private static ArtifactFile Mutated(string file, Action<List<JsonObject>> change, string? newPath = null)
    {
        var records = Records(file);
        change(records);
        var body = records.Skip(1).SkipLast(1).ToList();
        var complete = records[^1];
        foreach (var kind in new[] { "node", "edge", "evidence", "calculation", "diagnostic" })
        {
            complete["counts"]![kind] = body.Count(r => (string)r["kind"]! == kind);
        }

        complete["coverage"]!["unresolvedEdges"] = body.Count(r => (string)r["kind"]! == "edge" && (string)r["resolution"]! == "unresolved");
        return new ArtifactFile(newPath ?? file, string.Join('\n', records.Select(r => r.ToJsonString())) + "\n");
    }

    private static List<ArtifactFile> Replace(params ArtifactFile[] replacements) =>
        Golden.Where(g => replacements.All(r => r.Path != g.Path)).Concat(replacements).ToList();

    private static Task<EvaluationReport> Evaluate(IReadOnlyList<ArtifactFile> actual, EvaluationConfig? config = null, string? repos = null) =>
        Evaluator.EvaluateAsync(Golden, actual, config ?? Config, repos);

    private static ArtifactEvaluation For(EvaluationReport report, string label) => report.Artifacts.Single(a => a.Label == label);

    [Fact]
    public async Task Golden_against_itself_is_perfect()
    {
        var report = await Evaluate(Golden);

        Assert.True(report.Passed, string.Join("\n", report.ThresholdFailures));
        Assert.All(report.Artifacts, a => Assert.Empty(a.Findings));
        var t = report.Totals;
        Assert.All(new[] { t.NodeRecall, t.EdgeRecall, t.EdgeAccuracy, t.EvidenceLocated, t.CalculationRecall, t.AttributeAccuracy, t.IrHashMatch, t.Precision, t.Coverage }, r => Assert.Equal(1.0, r.Value));
        Assert.True(t.Precision.Total > 20, "exhaustive categories must actually contain facts");
        Assert.Equal(Golden.Count, report.Artifacts.Count);
    }

    [Fact]
    public async Task Pairing_uses_header_identity_not_file_names()
    {
        var renamed = Golden.Select((g, i) => new ArtifactFile($"anything/{i}.jsonl", g.Content)).ToList();
        var report = await Evaluate(renamed);
        Assert.True(report.Passed);
        Assert.Empty(report.UnpairedActual);
    }

    [Fact]
    public async Task Missing_node_lowers_recall_and_is_named()
    {
        const string response = "cs|api-cs|Sales.Api|T:Sales.Api.Controllers.WeeklySalesResponse";
        var report = await Evaluate(Replace(Mutated(Api, r => r.Remove(First(r, "node", n => (string)n["symbolKey"]! == response)))));

        var e = For(report, Api);
        var finding = Assert.Single(e.Findings);
        Assert.Equal((FindingKind.MissingNode, response), (finding.Kind, finding.Subject));
        Assert.Equal(e.Metrics.NodeRecall.Total - 1, e.Metrics.NodeRecall.Hit);
        Assert.Contains(report.ThresholdFailures, f => f.StartsWith("nodeRecall", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Node_kind_mismatch_is_not_a_hit()
    {
        var report = await Evaluate(Replace(Mutated(Api, r => First(r, "node", n => (string)n["nodeKind"]! == "Service")["nodeKind"] = "Project")));
        var e = For(report, Api);
        Assert.Contains(e.Findings, f => f.Kind == FindingKind.NodeKindMismatch && f.Detail == "expected Service, got Project");
        Assert.Equal(e.Metrics.NodeRecall.Total - 1, e.Metrics.NodeRecall.Hit);
    }

    [Fact]
    public async Task Overclaimed_resolution_counts_against_accuracy_not_recall()
    {
        // An extractor that says "resolved" for an interface call claims a runtime target it cannot prove.
        var report = await Evaluate(Replace(Mutated(Api, r => First(r, "edge", e => (string)e["resolution"]! == "static-target")["resolution"] = "resolved")));
        var m = For(report, Api).Metrics;
        Assert.Equal(m.EdgeRecall.Total, m.EdgeRecall.Hit);
        Assert.Equal(m.EdgeAccuracy.Total - 1, m.EdgeAccuracy.Hit);
        Assert.Contains(For(report, Api).Findings, f => f.Kind == FindingKind.ResolutionMismatch && f.Detail == "expected static-target, got resolved");
    }

    [Fact]
    public async Task Missing_edge_reports_where_it_was_expected()
    {
        var report = await Evaluate(Replace(Mutated(Api, r => r.Remove(First(r, "edge", e => (string)e["relation"]! == "DEPENDS_ON")))));
        var finding = Assert.Single(For(report, Api).Findings);
        Assert.Equal(FindingKind.MissingEdge, finding.Kind);
        Assert.Equal("expected at src/Sales.Api/Sales.Api.csproj:14", finding.Detail);
    }

    [Fact]
    public async Task Unresolved_edge_with_wrong_reason_is_matched_but_inaccurate()
    {
        var report = await Evaluate(Replace(Mutated(Broken, r => First(r, "edge", e => e["to"] is null)["unresolvedReason"] = "reflection")));
        var e = For(report, Broken);
        Assert.Equal(e.Metrics.EdgeRecall.Total, e.Metrics.EdgeRecall.Hit);
        Assert.Contains(e.Findings, f => f.Kind == FindingKind.UnresolvedReasonMismatch && f.Detail == "expected missing-reference, got reflection");
        Assert.Equal(1.0, e.Metrics.Precision.Value);
    }

    [Fact]
    public async Task Fact_reported_somewhere_else_is_not_located()
    {
        var report = await Evaluate(Replace(Mutated(Api, r =>
        {
            var node = First(r, "node", n => (string)n["nodeKind"]! == "Project");
            var moved = First(r, "evidence", ev => (string)ev["path"]! == "src/Sales.Api/Program.cs").DeepClone().AsObject();
            moved["id"] = "ev:ffffffffffffffff";
            r.Insert(1, moved);
            node["evidence"] = new JsonArray("ev:ffffffffffffffff");
        })));
        var e = For(report, Api);
        Assert.Contains(e.Findings, f => f.Kind == FindingKind.EvidenceNotLocated && f.Subject == "proj|api-cs|src/Sales.Api/Sales.Api.csproj");
        Assert.Equal(e.Metrics.NodeRecall.Total, e.Metrics.NodeRecall.Hit);
    }

    [Fact]
    public async Task Duplicate_edges_prefer_the_one_at_the_golden_location()
    {
        var report = await Evaluate(Replace(Mutated(Api, r =>
        {
            var edge = First(r, "edge", e => (string)e["relation"]! == "HANDLES");
            var elsewhere = edge.DeepClone().AsObject();
            elsewhere["id"] = "e:ffffffffffffffff";
            elsewhere["evidence"] = new JsonArray((string)First(r, "evidence", ev => (string)ev["path"]! == "src/Sales.Api/Program.cs")["id"]!);
            elsewhere["resolution"] = "candidate";
            r.Insert(r.IndexOf(edge), elsewhere);
        })));
        Assert.Empty(For(report, Api).Findings);
    }

    [Theory]
    [InlineData("rounding", "wrong")]
    [InlineData("rounding", "missed")]
    [InlineData("currency", "fabricated")]
    public async Task Calculation_attributes_are_scored_one_by_one(string attribute, string mode)
    {
        var report = await Evaluate(Replace(Mutated(Api, r =>
        {
            var attrs = First(r, "calculation")["attributes"]!.AsObject();
            var evidence = attrs["rounding"]!["evidence"]!.DeepClone();
            attrs[attribute] = mode switch
            {
                "wrong" => new JsonObject { ["status"] = "known", ["value"] = "halfAwayFromZero", ["evidence"] = evidence },
                "missed" => new JsonObject { ["status"] = "unknown" },
                _ => new JsonObject { ["status"] = "known", ["value"] = "SEK", ["evidence"] = evidence },
            };
        })));
        var e = For(report, Api);
        var finding = Assert.Single(e.Findings);
        Assert.Equal($"{ApiCalc} :: {attribute}", finding.Subject);
        Assert.Equal(mode switch { "wrong" => FindingKind.AttributeWrong, "missed" => FindingKind.AttributeMissed, _ => FindingKind.AttributeFabricated }, finding.Kind);
        Assert.Equal(e.Metrics.AttributeAccuracy.Total - 1, e.Metrics.AttributeAccuracy.Hit);
    }

    [Fact]
    public async Task Different_normalised_expression_is_reported()
    {
        var report = await Evaluate(Replace(Mutated(Api, r => First(r, "calculation")["irHash"] = new string('0', 64))));
        Assert.Equal(FindingKind.IrHashMismatch, Assert.Single(For(report, Api).Findings).Kind);
    }

    [Fact]
    public async Task Missing_calculation_counts_all_its_attributes_as_misses()
    {
        var report = await Evaluate(Replace(Mutated(Api, r => r.Remove(First(r, "calculation")))));
        var m = For(report, Api).Metrics;
        Assert.Equal(new Ratio(0, 1), m.CalculationRecall);
        Assert.Equal(new Ratio(0, 10), m.AttributeAccuracy);
    }

    [Fact]
    public async Task Extra_fact_in_an_exhaustive_category_costs_precision()
    {
        var report = await Evaluate(Replace(Mutated(Pricing, r =>
        {
            var extra = First(r, "edge", e => (string)e["relation"]! == "READS").DeepClone().AsObject();
            extra["id"] = "e:ffffffffffffffff";
            extra["to"] = "sql|@unbound:pricing-node/@fixture/pricing:env=PRICING_DATABASE_URL|pricing.audit_log";
            r.Insert(r.Count - 1, extra);
        })));
        var e = For(report, Pricing);
        Assert.Contains(e.Findings, f => f.Kind == FindingKind.UnexpectedFact && f.Subject.EndsWith("pricing.audit_log", StringComparison.Ordinal));
        Assert.Equal(e.Metrics.Precision.Total - 1, e.Metrics.Precision.Hit);
    }

    [Fact]
    public async Task Extra_fact_outside_exhaustive_categories_is_free()
    {
        // Golden sets are subsets: an additional CALLS edge may well be true, so it must not count against precision.
        var report = await Evaluate(Replace(Mutated(Pricing, r =>
        {
            var extra = First(r, "edge", e => (string)e["relation"]! == "CALLS" && e["to"] is not null).DeepClone().AsObject();
            extra["id"] = "e:ffffffffffffffff";
            extra["to"] = "ts|@ext|typescript|lib.es5.d.ts|JSON.stringify#value";
            r.Insert(r.Count - 1, extra);
        })));
        Assert.Empty(For(report, Pricing).Findings);
        Assert.True(report.Passed);
    }

    [Fact]
    public async Task Extra_endpoint_is_unexpected()
    {
        var report = await Evaluate(Replace(Mutated(Pricing, r =>
        {
            var extra = First(r, "node", n => (string)n["nodeKind"]! == "Endpoint").DeepClone().AsObject();
            extra["id"] = "n:ffffffffffffffff";
            extra["symbolKey"] = "http|pricing-node/@fixture/pricing|DELETE /api/pricing/{}";
            r.Insert(r.Count - 1, extra);
        })));
        Assert.Contains(For(report, Pricing).Findings, f => f.Kind == FindingKind.UnexpectedFact && f.Subject.Contains("DELETE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Coverage_must_match_the_reference()
    {
        var withoutDiagnostic = Mutated(Broken, r => r.Remove(First(r, "diagnostic", d => (string)d["code"]! == "MISSING_PROJECT_REFERENCE")));
        var e = For(await Evaluate(Replace(withoutDiagnostic)), Broken);
        Assert.Contains(e.Findings, f => f.Kind == FindingKind.MissingDiagnostic && f.Subject.StartsWith("MISSING_PROJECT_REFERENCE", StringComparison.Ordinal));
        Assert.Equal(new Ratio(0, 1), e.Metrics.Coverage);

        var withoutPaths = Mutated(Broken, r =>
        {
            r[^1]["coverage"]!.AsObject().Remove("incompletePaths");
            r[^1]["coverage"]!["filesAnalyzed"] = r[^1]["coverage"]!["filesTotal"]!.GetValue<int>();
        });
        Assert.Contains(For(await Evaluate(Replace(withoutPaths)), Broken).Findings, f => f.Kind == FindingKind.CoverageMismatch && f.Subject == "coverage.incompletePaths");
    }

    [Fact]
    public async Task Missing_and_invalid_artifacts_score_zero()
    {
        var truncated = new ArtifactFile(Api, string.Join('\n', Golden.Single(g => g.Path == Api).Content.TrimEnd('\n').Split('\n').SkipLast(1)) + "\n");
        var actual = Golden.Where(g => g.Path != Api && g.Path != Pricing).Append(truncated).ToList();
        var report = await Evaluate(actual);

        var invalid = For(report, Api);
        Assert.Equal(PairingStatus.InvalidActual, invalid.Status);
        Assert.Contains(invalid.Problems, p => p.Contains("truncated", StringComparison.Ordinal));
        Assert.Equal(0, invalid.Metrics.NodeRecall.Hit);

        var missing = For(report, Pricing);
        Assert.Equal(PairingStatus.MissingActual, missing.Status);
        Assert.Equal(0, missing.Metrics.EdgeRecall.Hit);
        Assert.True(missing.Metrics.EdgeRecall.Total > 0);

        Assert.Contains(report.ThresholdFailures, f => f.Contains("2 golden artifact(s) have no valid actual artifact", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unknown_actual_artifacts_are_listed_but_not_scored()
    {
        var other = Mutated(Api, r => r[0]["unit"]!["path"] = "src/Other/Other.csproj", newPath: "extra.jsonl");
        var report = await Evaluate([.. Golden, other]);
        Assert.True(report.Passed);
        Assert.Single(report.UnpairedActual);
    }

    [Fact]
    public async Task Evaluation_can_be_limited_to_some_repositories()
    {
        var forecastOnly = Golden.Where(g => g.Path.StartsWith("forecast-cs/", StringComparison.Ordinal)).ToList();
        var report = await Evaluator.EvaluateAsync(Golden, forecastOnly, Config, onlyRepositories: ["forecast-cs"]);
        Assert.True(report.Passed);
        Assert.All(report.Artifacts, a => Assert.Equal("forecast-cs", a.RepositoryId));
        Assert.Equal(2, report.Artifacts.Count);
    }

    [Fact]
    public async Task Evaluation_can_be_limited_to_some_languages()
    {
        // An extractor for one language is gated only on the units it owns (no SQL units missing).
        var csharpOnly = Golden.Where(g => g.Content.Contains("\"language\":\"csharp\"", StringComparison.Ordinal)).ToList();
        var report = await Evaluator.EvaluateAsync(Golden, csharpOnly, Config, onlyLanguages: ["csharp"]);
        Assert.True(report.Passed);
        Assert.NotEmpty(report.Artifacts);
        Assert.Equal(csharpOnly.Count, report.Artifacts.Count);
    }

    [Fact]
    public async Task Thresholds_are_optional()
    {
        var actual = Replace(Mutated(Api, r => r.Remove(First(r, "edge", e => (string)e["relation"]! == "DEPENDS_ON"))));
        Assert.True((await Evaluate(actual, EvaluationConfig.Default)).Passed);
        Assert.False((await Evaluate(actual)).Passed);
    }

    [Fact]
    public async Task Evidence_is_verified_against_git_when_repositories_are_given()
    {
        var actual = Replace(Mutated(Api, r => First(r, "evidence")["snippetSha256"] = new string('a', 64)));
        var report = await Evaluate(actual, repos: Path.Combine(Fixtures.RepositoryRoot, "fixtures", ".repos"));
        Assert.Equal(1, report.Totals.EvidenceErrors);
        Assert.Contains(report.ThresholdFailures, f => f.StartsWith("evidenceErrors", StringComparison.Ordinal));

        var clean = await Evaluate(Golden, repos: Path.Combine(Fixtures.RepositoryRoot, "fixtures", ".repos"));
        Assert.Equal(0, clean.Totals.EvidenceErrors);
    }

    [Fact]
    public async Task Reports_render_as_text_and_json()
    {
        var report = await Evaluate(Replace(Mutated(Api, r => r.Remove(First(r, "edge", e => (string)e["relation"]! == "DEPENDS_ON")))));
        using var text = new StringWriter();
        ReportWriter.WriteText(report, text);
        Assert.Contains("DIFF     api-cs/v1-base/Sales.Api.jsonl", text.ToString(), StringComparison.Ordinal);
        Assert.Contains("thresholds: FAILED", text.ToString(), StringComparison.Ordinal);

        using var json = JsonDocument.Parse(ReportWriter.ToJson(report));
        Assert.False(json.RootElement.GetProperty("passed").GetBoolean());
        Assert.Equal(1, json.RootElement.GetProperty("totals").GetProperty("edgeRecall").GetProperty("total").GetInt32()
            - json.RootElement.GetProperty("totals").GetProperty("edgeRecall").GetProperty("hit").GetInt32());
        Assert.Equal("MissingEdge", json.RootElement.GetProperty("artifacts").EnumerateArray()
            .Single(a => a.GetProperty("label").GetString() == Api).GetProperty("findings")[0].GetProperty("kind").GetString());
    }
}
