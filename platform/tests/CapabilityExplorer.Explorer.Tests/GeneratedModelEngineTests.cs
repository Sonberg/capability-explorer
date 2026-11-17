using System.Text.Json;
using System.Xml.Linq;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Contracts.Collection;
using CapabilityExplorer.Explorer.GeneratedModels;
using CapabilityExplorer.Extractors.CSharp;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class GeneratedModelEngineTests
{
    [Fact]
    public void Given_valid_candidate_evidence_when_provider_proposes_a_relationship_then_it_is_unreviewed()
    {
        var candidates = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["candidate:a"] = "process",
            ["candidate:b"] = "decision",
        };

        var proposals = GeneratedModelEngine.ValidateProposals("""
            {"proposals":[{"fromCandidateId":"candidate:a","toCandidateId":"candidate:b","relation":"uses-decision","rationale":"Both candidates share cited static evidence.","evidenceCandidateIds":["candidate:a","candidate:b"]}]}
            """, candidates);

        var proposal = Assert.Single(proposals);
        Assert.Equal("ai-inferred", proposal.EvidenceClass);
        Assert.Equal("unreviewed", proposal.ReviewStatus);
        Assert.Equal(["candidate:a", "candidate:b"], proposal.EvidenceCandidateIds);
    }

    [Fact]
    public void Given_an_unknown_candidate_citation_when_provider_proposes_a_relationship_then_it_is_rejected()
    {
        var candidates = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["candidate:a"] = "process",
            ["candidate:b"] = "decision",
        };

        Assert.Throws<InvalidDataException>(() => GeneratedModelEngine.ValidateProposals("""
            {"proposals":[{"fromCandidateId":"candidate:a","toCandidateId":"candidate:b","relation":"related-to","rationale":"Unsupported candidate.","evidenceCandidateIds":["candidate:a","candidate:outside"]}]}
            """, candidates));
    }

    [Fact]
    public void Given_extra_provider_fields_when_provider_proposes_a_relationship_then_it_is_rejected()
    {
        var candidates = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["candidate:a"] = "process",
            ["candidate:b"] = "decision",
        };

        Assert.Throws<InvalidDataException>(() => GeneratedModelEngine.ValidateProposals("""
            {"proposals":[{"fromCandidateId":"candidate:a","toCandidateId":"candidate:b","relation":"related-to","rationale":"Cited.","evidenceCandidateIds":["candidate:a","candidate:b"],"approved":true}]}
            """, candidates));
    }

    [Fact]
    public void Given_a_relationship_with_incompatible_candidate_kinds_when_provider_proposes_it_then_it_is_rejected()
    {
        var candidates = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["candidate:a"] = "decision",
            ["candidate:b"] = "process",
        };

        Assert.Throws<InvalidDataException>(() => GeneratedModelEngine.ValidateProposals("""
            {"proposals":[{"fromCandidateId":"candidate:a","toCandidateId":"candidate:b","relation":"uses-decision","rationale":"Invalid direction.","evidenceCandidateIds":["candidate:a","candidate:b"]}]}
            """, candidates));
    }

    [Fact]
    public void Given_a_static_process_candidate_when_models_are_built_then_bpmn_is_non_executable_and_cited()
    {
        var product = ExtractProduct("""
            public static class Sample {
                public static extern void Emit(string value);
                public static string Run(string value) { Emit(value); return "done"; }
            }
            """);

        var build = GeneratedModelEngine.Build([product], false);

        Assert.True(build.Complete, string.Join("; ", build.Diagnostics));
        var diagram = Assert.Single(build.Diagrams);
        Assert.Equal("bpmn", diagram.Kind);
        Assert.Equal("unreviewed", diagram.ReviewStatus);
        var xml = XDocument.Parse(diagram.BpmnXml!);
        XNamespace bpmn = "http://www.omg.org/spec/BPMN/20100524/MODEL";
        Assert.Equal("false", xml.Root!.Element(bpmn + "process")!.Attribute("isExecutable")!.Value);
        Assert.Equal(2, xml.Descendants(bpmn + "sequenceFlow").Count());
        Assert.NotEmpty(diagram.Evidence);
    }

    [Fact]
    public void Given_a_static_decision_candidate_when_models_are_built_then_dmn_records_source_text_as_data()
    {
        var product = ExtractProduct("""
            public static class Sample {
                public static string Decide(bool ready) { if (ready) return "yes"; return "no"; }
            }
            """);

        var build = GeneratedModelEngine.Build([product], false);

        Assert.True(build.Complete, string.Join("; ", build.Diagnostics));
        var diagram = Assert.Single(build.Diagrams);
        Assert.Equal("dmn", diagram.Kind);
        var xml = XDocument.Parse(diagram.DmnXml!);
        XNamespace dmn = "https://www.omg.org/spec/DMN/20191111/MODEL/";
        Assert.Equal("source condition text", xml.Descendants(dmn + "inputExpression").Single().Element(dmn + "text")!.Value);
        Assert.Contains("\"ready\"", xml.Descendants(dmn + "inputEntry").First().Element(dmn + "text")!.Value);
        Assert.Contains(xml.Descendants(dmn + "description"),
            description => description.Value.Contains("not translated business logic", StringComparison.Ordinal));
    }

    private static ModelProduct ExtractProduct(string source)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "ce-candidate-" + Guid.NewGuid().ToString("N") + ".cs");
        try
        {
            File.WriteAllText(path, source);
            var artifact = CandidateExtractor.Extract(path, "repo", new string('a', 40));
            var bytes = System.Text.Encoding.UTF8.GetBytes(artifact.ToJson());
            using (var document = JsonDocument.Parse(bytes))
                Assert.True(EvidenceProfileSchema.Validate(artifact.Schema, document.RootElement).Count == 0,
                    string.Join("; ", EvidenceProfileSchema.Validate(artifact.Schema, document.RootElement)));
            var id = ValidatedModelBundle.StableId(artifact.Schema, artifact.RepositoryId, artifact.CommitSha, artifact.UnitPath, ModelSource.Digest(bytes));
            return new ModelProduct(id, artifact.Schema, artifact.RepositoryId, artifact.CommitSha, artifact.UnitPath,
                ModelSource.Digest(bytes), bytes, new string('b', 64), "missing", null);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
