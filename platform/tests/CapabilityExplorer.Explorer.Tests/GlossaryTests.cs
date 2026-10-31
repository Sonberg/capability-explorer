using System.Globalization;
using System.Text.Json;
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
}
