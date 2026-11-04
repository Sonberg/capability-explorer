using System.Text.Json;
using System.Text.Json.Nodes;
using CapabilityExplorer.Extractors.CSharp;
using Json.Schema;
using Xunit;

namespace CapabilityExplorer.Extractors.Tests;

public sealed class CandidateSchemaTests
{
    [Fact]
    public void Given_emitted_profile_then_closed_schema_accepts_it_and_refuses_executable_claims()
    {
        var (schema, artifact) = Load();
        Assert.True(Valid(schema, artifact));
        artifact["candidates"]![0]!["executable"] = true;
        Assert.False(Valid(schema, artifact));
    }

    [Theory]
    [InlineData("missing-value")]
    [InlineData("exception-as-scalar")]
    [InlineData("full-with-diagnostics")]
    public void Given_semantically_malformed_candidate_then_schema_refuses(string mutation)
    {
        var (schema, artifact) = Load();
        if (mutation == "full-with-diagnostics")
            artifact["diagnostics"]!.AsArray().Add(new JsonObject { ["code"] = "unsupported", ["message"] = "not full" });
        else
        {
            var constant = Walk(artifact["candidates"]![0]!["body"]!.AsObject()).First(n => n["kind"]!.GetValue<string>() == "constant" && n["type"]!.GetValue<string>() != "null");
            if (mutation == "missing-value") constant.Remove("value");
            else constant["type"] = "System.ArgumentException";
        }
        Assert.False(Valid(schema, artifact));
    }

    private static bool Valid(JsonSchema schema, JsonObject artifact)
    {
        using var document = JsonDocument.Parse(artifact.ToJsonString());
        return schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.Flag }).IsValid;
    }

    private static (JsonSchema Schema, JsonObject Artifact) Load()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "fixtures/candidates"))) root = root.Parent;
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(root!.FullName, "schema/candidates/artifact.schema.json")),
            new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        var result = CandidateExtractor.Extract(Path.Combine(root.FullName, "fixtures/candidates/reviewed.cs"), "synthetic",
            "0123456789abcdef0123456789abcdef01234567");
        return (schema, JsonNode.Parse(result.ToJson())!.AsObject());
    }

    private static IEnumerable<JsonObject> Walk(JsonObject node) => new[] { node }.Concat(
        node["children"]!.AsArray().SelectMany(child => Walk(child!.AsObject())));
}
