using System.Text.Json.Nodes;
using CapabilityExplorer.Contracts;
using Xunit;

namespace CapabilityExplorer.Tests;

/// <summary>Each test breaks one rule in an otherwise valid golden artifact and expects that rule to fire.</summary>
public sealed class ValidatorRejectionTests
{
    private const string Sample = "api-cs/v1-base/Sales.Api.jsonl";

    private static List<JsonObject> Records(string file = Sample) =>
        Fixtures.Golden(file).TrimEnd('\n').Split('\n').Select(l => JsonNode.Parse(l)!.AsObject()).ToList();

    private static string Jsonl(IEnumerable<JsonObject> records) => string.Join('\n', records.Select(r => r.ToJsonString())) + "\n";

    private static JsonObject First(List<JsonObject> records, string kind, Func<JsonObject, bool>? where = null) =>
        records.First(r => (string)r["kind"]! == kind && (where?.Invoke(r) ?? true));

    private static void AssertRejected(IEnumerable<JsonObject> records, string expectedFragment)
    {
        var report = ArtifactValidator.Validate(Jsonl(records));
        Assert.False(report.IsValid);
        Assert.Contains(report.Problems, p => p.Message.Contains(expectedFragment, StringComparison.Ordinal));
    }

    [Fact]
    public void Truncated_artifact_is_rejected() =>
        AssertRejected(Records().SkipLast(1), "truncated");

    [Fact]
    public void Empty_artifact_is_rejected() =>
        Assert.False(ArtifactValidator.Validate("").IsValid);

    [Fact]
    public void Header_must_come_first()
    {
        var r = Records();
        (r[0], r[1]) = (r[1], r[0]);
        AssertRejected(r, "first record must be the header");
    }

    [Fact]
    public void Schema_violation_reports_the_line()
    {
        var r = Records();
        First(r, "edge")["derivation"] = "llm"; // AI interpretations never enter extractor output
        var report = ArtifactValidator.Validate(Jsonl(r));
        Assert.Contains(report.Problems, p => p.Line == r.IndexOf(First(r, "edge")) + 1 && p.Message.StartsWith("schema", StringComparison.Ordinal));
    }

    [Fact]
    public void Duplicate_ids_are_rejected()
    {
        var r = Records();
        var nodes = r.Where(x => (string)x["kind"]! == "node").Take(2).ToList();
        nodes[1]["id"] = (string)nodes[0]["id"]!;
        AssertRejected(r, "duplicate id");
    }

    [Fact]
    public void Id_prefix_must_match_kind()
    {
        var r = Records();
        var node = First(r, "node");
        node["id"] = "e:" + ((string)node["id"]!)[2..];
        AssertRejected(r, "node id must start with 'n:'");
    }

    [Fact]
    public void Dangling_evidence_reference_is_rejected()
    {
        var r = Records();
        First(r, "node")["evidence"] = new JsonArray("ev:0000000000000000");
        AssertRejected(r, "unknown evidence reference ev:0000000000000000");
    }

    [Fact]
    public void Evidence_from_another_commit_is_rejected()
    {
        var r = Records();
        First(r, "evidence")["commitSha"] = new string('0', 40);
        AssertRejected(r, "evidence must reference the header's repository and commitSha");
    }

    [Fact]
    public void Node_claiming_another_repository_is_rejected()
    {
        var r = Records();
        var node = First(r, "node", n => ((string)n["symbolKey"]!).StartsWith("cs|api-cs|", StringComparison.Ordinal));
        node["symbolKey"] = ((string)node["symbolKey"]!).Replace("cs|api-cs|", "cs|worker-cs|", StringComparison.Ordinal);
        AssertRejected(r, "its key names repository 'worker-cs'");
    }

    [Fact]
    public void Malformed_symbol_key_is_rejected()
    {
        var r = Records();
        First(r, "edge")["from"] = "cs|api-cs|Sales.Api";
        AssertRejected(r, "'cs' keys need at least 4 segments");
    }

    [Fact]
    public void Edge_from_an_undeclared_symbol_is_rejected()
    {
        var r = Records();
        First(r, "edge")["from"] = "cs|api-cs|Sales.Api|M:Nowhere.Method";
        AssertRejected(r, "is not declared in this artifact");
    }

    [Fact]
    public void Relation_catalogue_is_enforced()
    {
        var r = Records();
        var edge = First(r, "edge", e => (string)e["relation"]! == "HANDLES");
        edge["from"] = (string)First(r, "node", n => (string)n["nodeKind"]! == "Type")["symbolKey"]!;
        AssertRejected(r, "HANDLES may not start at a Type");
    }

    [Fact]
    public void Count_mismatch_is_rejected()
    {
        var r = Records();
        r.Remove(First(r, "edge"));
        AssertRejected(r, "complete.counts");
    }

    [Fact]
    public void Full_status_with_incomplete_files_is_rejected()
    {
        var r = Records();
        r[^1]["coverage"]!["incompletePaths"] = new JsonArray("src/Sales.Api/Program.cs");
        AssertRejected(r, "status 'full' requires every file to be analysed");
    }

    [Fact]
    public void Partial_status_must_say_what_is_missing()
    {
        var r = Records();
        r[^1]["status"] = "partial";
        AssertRejected(r, "status 'partial' must say what is incomplete");
    }

    [Fact]
    public void Calculation_placeholders_must_be_inputs_or_bound()
    {
        var r = Records();
        var calc = First(r, "calculation");
        calc["inputs"]!.AsArray().RemoveAt(2); // drop $2 while the expression still uses it
        AssertRejected(r, "expression uses $2");
    }

    [Fact]
    public void Known_attribute_needs_evidence()
    {
        var r = Records();
        First(r, "calculation")["attributes"]!["rounding"]!.AsObject().Remove("evidence");
        AssertRejected(r, "schema");
    }

    [Fact]
    public async Task Tampered_snippet_hash_is_caught_against_git()
    {
        var r = Records();
        First(r, "evidence")["snippetSha256"] = new string('a', 64);
        var report = ArtifactValidator.Validate(Jsonl(r));
        Assert.True(report.IsValid); // structurally fine; only the repository can tell
        var problems = await new EvidenceVerifier(Fixtures.RepoDirectory("api-cs")).VerifyAsync(report.Artifact!);
        Assert.Contains(problems, p => p.Message.Contains("snippet hash mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Evidence_for_a_file_missing_at_that_commit_is_caught()
    {
        // The v1 path no longer exists at v2: evidence must be bound to its exact revision.
        var r = Records();
        var v2 = Fixtures.Lock["api-cs"]["v2-rename"];
        foreach (var rec in r.Where(x => x["commitSha"] is not null))
        {
            rec["commitSha"] = v2;
        }

        var report = ArtifactValidator.Validate(Jsonl(r));
        Assert.True(report.IsValid, string.Join("\n", report.Problems));
        var problems = await new EvidenceVerifier(Fixtures.RepoDirectory("api-cs")).VerifyAsync(report.Artifact!);
        Assert.Contains(problems, p => p.Message.Contains("src/Sales.Api/Calculations/WeeklySalesCalculator.cs does not exist", StringComparison.Ordinal));
    }
}
