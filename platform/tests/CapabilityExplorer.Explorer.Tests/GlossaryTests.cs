using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Explorer;
using CapabilityExplorer.Interpretation;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class GlossaryTests
{
    [Theory]
    [InlineData("averagePerActiveWeek", "average per active week")]
    [InlineData("Average_Per_Active_Week", "average per active week")]
    [InlineData("average-per-active-week", "average per active week")]
    [InlineData("HTTPServer", "http server")]
    [InlineData("XMLHttpRequest", "xml http request")]
    [InlineData("InvoiceID", "invoice id")]
    [InlineData("InvoiceDto", "invoice")]
    [InlineData("InvoiceEntity", "invoice")]
    [InlineData("InvoiceRecord", "invoice")]
    [InlineData("InvoiceDtoFactory", "invoice dto factory")]
    [InlineData("Record", "record")]
    public void Normalizes_only_explicit_word_boundaries_and_terminal_container_suffixes(string name, string expected) =>
        Assert.Equal(expected, GlossaryNormalization.Normalize(name));

    [Theory]
    [InlineData("Invoice", "InvoiceId")]
    [InlineData("AveragePerCalendarWeek", "AveragePerActiveWeek")]
    [InlineData("CalendarWeekAverage", "AveragePerActiveWeek")]
    [InlineData("Qty", "Quantity")]
    [InlineData("InvoiceRecordCount", "InvoiceCount")]
    public void Qualifiers_and_abbreviations_are_not_business_equivalence(string left, string right) =>
        Assert.NotEqual(GlossaryNormalization.Normalize(left), GlossaryNormalization.Normalize(right));

    [Fact]
    public void Normalization_is_invariant_under_Turkish_case_rules()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal("invoice id", GlossaryNormalization.Normalize("InvoiceID"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Serialized_contract_preserves_unknowns_evidence_identity_and_comparison_completeness()
    {
        var evidence = new EvidenceDto("ev:source", "repo", "revision", "src/Invoice.cs", 3, 2, 3, 9);
        var occurrence = new GlossaryOccurrence("stable-id", "cs|repo|Unit|P:Invoice.Amount", "Amount", "property", "declaration",
            "repo", "revision", "Unit", "src/Unit.csproj", null, ["sales"], [evidence]);
        var difference = new Difference("d1", "rounding", DifferenceKind.KnownOnOneSide,
            new AttributeValue("explicit", "halfEven", null), null);
        var candidate = new GlossaryCandidate("amount", [occurrence], ["repo"], ["sales"], false, false, false,
            false, 66, [new GlossaryComparison("stable-id", "other-id", null, [difference])]);

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(candidate, options);
        var json = JsonSerializer.Deserialize<JsonElement>(bytes);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("occurrences")[0].GetProperty("type").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("comparisons")[0].GetProperty("sameExpression").ValueKind);
        Assert.False(json.GetProperty("comparisonsComplete").GetBoolean());
        Assert.Equal(66, json.GetProperty("possibleComparisons").GetInt64());
        var source = json.GetProperty("occurrences")[0].GetProperty("evidence")[0];
        Assert.Equal("repo", source.GetProperty("repositoryId").GetString());
        Assert.Equal("revision", source.GetProperty("commitSha").GetString());
        Assert.Equal("src/Invoice.cs", source.GetProperty("path").GetString());
        Assert.Equal(3, source.GetProperty("startLine").GetInt32());
        var roundTrip = JsonSerializer.Deserialize<GlossaryCandidate>(bytes, options)!;
        Assert.Equal(DifferenceKind.KnownOnOneSide, roundTrip.Comparisons[0].Differences[0].Kind);
        Assert.Equal(bytes, JsonSerializer.SerializeToUtf8Bytes(roundTrip, options));
    }
    [Fact]
    public void Generated_external_and_unnamed_route_parameters_do_not_become_candidates()
    {
        var records = Records(0);
        records.Add(Node("n:good", "cs|worker-cs|Sales.Worker|P:Domain.Invoice", "Invoice", "Property"));
        var generated = Node("n:generated", "cs|worker-cs|Sales.Worker|T:GeneratedInvoice", "GeneratedInvoice", "Type");
        generated["generated"] = true;
        records.Add(generated);
        var external = Node("n:external", "cs|worker-cs|Sales.Worker|T:ExternalInvoice", "ExternalInvoice", "Type");
        external["props"] = new JsonObject { ["external"] = true };
        records.Add(external);
        records.Add(Node("n:endpoint", "http|worker-cs/Sales.Worker|GET /invoices/{invoiceId}", "GET /invoices/{invoiceId}", "Endpoint"));
        var terms = Glossary.Build(Load(records).View(null)).Select(c => c.Term).ToList();
        Assert.Contains("invoice", terms);
        Assert.DoesNotContain("generated invoice", terms);
        Assert.DoesNotContain("external invoice", terms);
        Assert.DoesNotContain("invoice id", terms);
    }

    [Fact]
    public void Unknown_type_does_not_create_a_mismatch_but_two_known_different_types_do()
    {
        var records = Records(0);
        var amount = Node("n:amount", "cs|worker-cs|Sales.Worker|P:A.Amount", "Amount", "Property");
        amount["props"] = new JsonObject { ["type"] = "decimal" };
        records.Add(amount);
        records.Add(Node("n:unknown", "cs|worker-cs|Sales.Worker|P:B.Amount", "amount", "Property"));
        var candidate = Assert.Single(Glossary.Build(Load(records).View(null)));
        Assert.False(candidate.TypeDiffers);
        Assert.Contains(candidate.Occurrences, o => o.Type is null);
        var other = Node("n:other", "cs|worker-cs|Sales.Worker|P:C.Amount", "AMOUNT", "Property");
        other["props"] = new JsonObject { ["type"] = "string" };
        records.Add(other);
        Assert.True(Assert.Single(Glossary.Build(Load(records).View(null))).TypeDiffers);
    }

    [Fact]
    public void Equal_calculations_and_one_sided_knowledge_do_not_assert_divergent_derivations()
    {
        var records = Records(2);
        var candidate = Assert.Single(Glossary.Build(Load(records).View(null)));
        Assert.Equal(2, candidate.Occurrences.Count(o => o.Role == "calculation"));
        Assert.True(Assert.Single(candidate.Comparisons).SameExpression);
        Assert.False(candidate.DerivationDiffers);
        Assert.False(candidate.ExpressionDiffers);
        var calculations = records.Where(r => (string?)r["kind"] == "calculation").ToList();
        calculations[0]["attributes"]!["rounding"] = JsonNode.Parse("""{"status":"known","value":"halfEven"}""");
        calculations[1]["attributes"]!["rounding"] = JsonNode.Parse("""{"status":"unknown"}""");
        calculations[1].AsObject().Remove("irHash");
        candidate = Assert.Single(Glossary.Build(Load(records).View(null)));
        Assert.Equal(DifferenceKind.KnownOnOneSide, Assert.Single(Assert.Single(candidate.Comparisons).Differences).Kind);
        Assert.False(candidate.DerivationDiffers);
        Assert.False(candidate.ExpressionDiffers);
        Assert.Null(Assert.Single(candidate.Comparisons).SameExpression);
    }

    [Fact]
    public void Actual_attribute_and_hash_differences_are_reported_separately()
    {
        var records = Records(2);
        var calculations = records.Where(r => (string?)r["kind"] == "calculation").ToList();
        calculations[0]["attributes"]!["rounding"] = JsonNode.Parse("""{"status":"known","value":"halfEven"}""");
        calculations[1]["attributes"]!["rounding"] = JsonNode.Parse("""{"status":"known","value":"truncate"}""");
        calculations[1]["irHash"] = new string('b', 64);
        var candidate = Assert.Single(Glossary.Build(Load(records).View(null)));
        Assert.True(candidate.DerivationDiffers);
        Assert.True(candidate.ExpressionDiffers);
        Assert.Equal(DifferenceKind.ValueDiffers, Assert.Single(Assert.Single(candidate.Comparisons).Differences).Kind);
    }

    [Fact]
    public void Pair_budget_discloses_incomplete_comparisons_and_is_deterministic()
    {
        var records = Records(12);
        var candidate = Assert.Single(Glossary.Build(Load(records).View(null)));
        Assert.Equal(66, candidate.PossibleComparisons);
        Assert.Equal(64, candidate.Comparisons.Count);
        Assert.False(candidate.ComparisonsComplete);
        Assert.Equal(64, candidate.Comparisons.Select(c => (c.LeftId, c.RightId)).Distinct().Count());
        Assert.False(candidate.DerivationDiffers);
        Assert.False(candidate.ExpressionDiffers);
        Assert.Equal(Serialize([candidate]), Serialize(Glossary.Build(Load(records.AsEnumerable().Reverse().ToList()).View(null))));
    }

    [Fact]
    public void Input_order_repetition_culture_and_artifact_integer_ids_do_not_change_serialized_candidates()
    {
        var records = Records(3);
        var first = Load(records);
        var original = Serialize(Glossary.Build(first.View(null)));
        Assert.Equal(original, Serialize(Glossary.Build(first.View(null))));
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var unrelated = Records(0);
            var header = unrelated.Single(r => (string?)r["kind"] == "header");
            header["unit"]!["path"] = "aaa/Unrelated.csproj";
            header["unit"]!["name"] = "Unrelated";
            var shifted = Load(unrelated, records.AsEnumerable().Reverse().ToList());
            Assert.NotEqual(first.Artifacts.Single().Id, shifted.Artifacts.Single(a => a.Artifact.Header.Unit.Name == "Sales.Worker").Id);
            Assert.Equal(original, Serialize(Glossary.Build(shifted.View(null))));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Duplicate_artifacts_are_deduplicated_but_distinct_unit_paths_preserve_occurrence_identity()
    {
        var records = Records(1);
        var single = Assert.Single(Glossary.Build(Load(records).View(null)));
        Assert.Single(single.Occurrences.Where(o => o.Role == "calculation"));
        Assert.Equal(Serialize([single]), Serialize(Glossary.Build(Load(records, records).View(null))));
        var secondUnit = records.Select(r => (JsonObject)r.DeepClone()).ToList();
        secondUnit.Single(r => (string?)r["kind"] == "header")["unit"]!["path"] = "other/Worker.csproj";
        var combined = Assert.Single(Glossary.Build(Load(records, secondUnit).View(null)));
        var calculations = combined.Occurrences.Where(o => o.Role == "calculation").ToList();
        Assert.Equal(2, calculations.Count);
        Assert.Equal(2, calculations.Select(o => o.Id).Distinct().Count());
        Assert.Equal(2, calculations.Select(o => o.UnitPath).Distinct().Count());
    }

    [Fact]
    public void Calculation_inputs_require_resolved_named_declarations_and_output_types_are_not_names()
    {
        var records = Records(1);
        const string namedOrigin = "cs|worker-cs|Sales.Worker|P:Domain.Quantity";
        records.Add(Node("n:quantity", namedOrigin, "Quantity", "Property"));
        var calculation = records.Single(r => (string?)r["kind"] == "calculation");
        calculation["inputs"] = new JsonArray(
            new JsonObject { ["ref"] = "$0", ["type"] = "decimal", ["origin"] = namedOrigin },
            new JsonObject { ["ref"] = "$1", ["type"] = "decimal", ["origin"] = "cs|@ext|Other|P:Unknown.PhantomName" });
        var candidates = Glossary.Build(Load(records).View(null));
        var quantity = candidates.Single(c => c.Term == "quantity");
        Assert.Contains(quantity.Occurrences, o => o.Role == "input" && o.Key == namedOrigin && o.Name == "Quantity");
        Assert.DoesNotContain(candidates, c => c.Term is "decimal" or "phantom name");
        Assert.All(quantity.Occurrences, o => Assert.NotEmpty(o.Evidence));
    }

    [Fact]
    public void Conflicting_calculation_records_for_one_symbol_do_not_choose_an_arbitrary_output_or_comparison()
    {
        var records = Records(2);
        var original = records.First(r => (string?)r["kind"] == "calculation");
        var conflict = (JsonObject)original.DeepClone();
        conflict["id"] = "c:conflict";
        conflict["output"]!["type"] = "int32";
        conflict["irHash"] = new string('b', 64);
        records.Add(conflict);
        var forward = Glossary.Build(Load(records).View(null));
        var backward = Glossary.Build(Load(records.AsEnumerable().Reverse().ToList()).View(null));
        Assert.Equal(Serialize(forward), Serialize(backward));
        var candidate = Assert.Single(forward);
        var ambiguousKey = (string)original["symbolKey"]!;
        Assert.All(candidate.Occurrences.Where(o => o.Key == ambiguousKey), o => Assert.Null(o.Type));
        Assert.Empty(candidate.Comparisons);
        Assert.False(candidate.TypeDiffers);
        Assert.False(candidate.DerivationDiffers);
        Assert.False(candidate.ExpressionDiffers);
    }

    private static byte[] Serialize(IReadOnlyList<GlossaryCandidate> candidates) =>
        JsonSerializer.SerializeToUtf8Bytes(candidates, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static JsonObject Node(string id, string key, string name, string kind) => new()
    {
        ["schema"] = "ce.v1", ["kind"] = "node", ["id"] = id, ["symbolKey"] = key,
        ["nodeKind"] = kind, ["name"] = name, ["evidence"] = new JsonArray("ev:source"),
    };

    // Construct independent valid artifacts from the frozen schema framing, without editing fixture files.
    private static List<JsonObject> Records(int calculationCount)
    {
        var golden = Fixtures.Golden("worker-cs/v1-base/Sales.Worker.jsonl").TrimEnd().Split('\n')
            .Select(line => JsonNode.Parse(line)!.AsObject()).ToList();
        var header = golden.Single(r => (string?)r["kind"] == "header");
        var evidence = golden.First(r => (string?)r["kind"] == "evidence");
        evidence["id"] = "ev:source";
        var records = new List<JsonObject> { header, evidence };
        for (var i = 0; i < calculationCount; i++)
        {
            var key = $"cs|worker-cs|Sales.Worker|M:Domain.Calculator{i}.InvoiceTotal()";
            records.Add(Node($"n:calc{i}", key, i % 2 == 0 ? "InvoiceTotal" : "invoice_total", "Method"));
            records.Add(new JsonObject
            {
                ["schema"] = "ce.v1", ["kind"] = "calculation", ["id"] = $"c:calc{i}", ["symbolKey"] = key,
                ["output"] = JsonNode.Parse("""{"type":"decimal","unit":{"status":"unknown"}}"""),
                ["inputs"] = new JsonArray(), ["expr"] = JsonNode.Parse("""{"op":"lit","type":"decimal","value":1}"""),
                ["attributes"] = UnknownAttributes(), ["irHash"] = new string('a', 64), ["evidence"] = new JsonArray("ev:source"),
            });
        }
        return records;
    }

    private static JsonObject UnknownAttributes()
    {
        var attributes = new JsonObject();
        foreach (var name in new[] { "aggregation", "timeBucket", "denominatorSource", "rounding", "numericType", "nullHandling", "zeroDenominator", "currency" })
        {
            attributes[name] = new JsonObject { ["status"] = "unknown" };
        }
        return attributes;
    }

    private static ExplorerIndex Load(params List<JsonObject>[] artifacts)
    {
        var directory = Directory.CreateTempSubdirectory("ce-glossary").FullName;
        try
        {
            for (var i = 0; i < artifacts.Length; i++)
            {
                var records = artifacts[i];
                var complete = new JsonObject
                {
                    ["schema"] = "ce.v1", ["kind"] = "complete", ["status"] = "full",
                    ["counts"] = new JsonObject
                    {
                        ["node"] = records.Count(r => (string?)r["kind"] == "node"),
                        ["calculation"] = records.Count(r => (string?)r["kind"] == "calculation"),
                        ["evidence"] = 1, ["edge"] = 0, ["diagnostic"] = 0,
                    },
                    ["coverage"] = JsonNode.Parse("""{"filesTotal":1,"filesAnalyzed":1,"unresolvedEdges":0}"""),
                };
                var ordered = records.OrderBy(r => (string?)r["kind"] == "header" ? 0 : 1).Append(complete);
                var text = string.Join('\n', ordered.Select(r => r.ToJsonString())) + "\n";
                var validated = ArtifactValidator.Validate(text);
                Assert.True(validated.IsValid, string.Join("; ", validated.Problems));
                File.WriteAllText(Path.Combine(directory, $"{i:D3}.jsonl"), text);
            }
            var index = ExplorerIndex.Load([directory], null);
            Assert.Empty(index.Problems);
            return index;
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

}
