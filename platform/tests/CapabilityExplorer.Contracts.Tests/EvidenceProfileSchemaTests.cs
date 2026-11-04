using System.Text.Json;
using CapabilityExplorer.Contracts;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class EvidenceProfileSchemaTests
{
    [Theory]
    [InlineData("ce.models.v1")]
    [InlineData("ce.candidates.v1")]
    public void Given_the_declared_complete_profile_shape_when_validated_then_accepted(string profile)
    {
        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["schema"] = profile, ["repositoryId"] = "sales", ["commitSha"] = new string('a', 40),
            ["unitPath"] = "source.cs", ["sourceSha256"] = new string('b', 64), ["diagnostics"] = Array.Empty<object>()
        };
        if (profile == "ce.models.v1")
        {
            payload["contentProfile"] = "structural";
            payload["coverage"] = new { status = "full", supported = Array.Empty<string>(), limitations = Array.Empty<string>() };
            payload["elements"] = Array.Empty<object>();
            payload["relationships"] = Array.Empty<object>();
        }
        else
        {
            payload["recipe"] = "roslyn.scalar-guards.v1";
            payload["coverage"] = new { status = "full", scope = "single-file-intra-method", supportedMethods = 0, unsupportedMethods = 0 };
            payload["candidates"] = Array.Empty<object>();
        }
        Assert.Empty(EvidenceProfileSchema.Validate(profile, JsonSerializer.SerializeToElement(payload)));
    }

    [Theory]
    [InlineData("ce.models.v1")]
    [InlineData("ce.candidates.v1")]
    public void Given_common_identity_without_the_profile_shape_when_validated_then_refused(string profile)
    {
        using var incomplete = JsonDocument.Parse($$$"""
            {"schema":"{{{profile}}}","repositoryId":"sales","commitSha":"{{{new string('a', 40)}}}",
             "unitPath":"source","sourceSha256":"{{{new string('b', 64)}}}","coverage":{"status":"full"},"diagnostics":[]}
            """);
        Assert.NotEmpty(EvidenceProfileSchema.Validate(profile, incomplete.RootElement));
    }

    [Fact]
    public void Given_a_model_profile_with_opaque_negative_order_then_schema_refuses_invalid_evidence()
    {
        using var document = JsonDocument.Parse($$$"""
            {"schema":"ce.models.v1","repositoryId":"sales","commitSha":"{{{new string('a', 40)}}}",
             "unitPath":"source.bpmn","sourceSha256":"{{{new string('b', 64)}}}","contentProfile":"model-content",
             "coverage":{"status":"partial","supported":[],"limitations":["opaque-extension-content"]},
             "diagnostics":[],"elements":[{"identity":"model:{{{new string('c', 64)}}}","kind":"opaqueExtension",
              "namespace":"urn:vendor","id":"v","type":"policy","order":-1,"attributes":{},"source":{"artifact":"source.bpmn"}}],
             "relationships":[]}
            """);
        Assert.NotEmpty(EvidenceProfileSchema.Validate("ce.models.v1", document.RootElement));
    }
}
