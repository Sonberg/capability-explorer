using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace CapabilityExplorer.Contracts;

public static class ModelSource
{
    public const int MaximumBytes = 1_048_576;
    public static byte[] Decode(JsonElement source)
    {
        var schemaProblems = EvidenceProfileSchema.Validate("ce.model-source.v1", source);
        if (schemaProblems.Count != 0) throw new FormatException(string.Join("; ", schemaProblems));
        var encoded = source.GetProperty("sourceBytesBase64").GetString()!;
        var bytes = Convert.FromBase64String(encoded);
        if (bytes.Length is < 1 or > MaximumBytes || Convert.ToBase64String(bytes) != encoded)
            throw new FormatException("source base64 or byte limit is invalid");
        if (Digest(bytes) != source.GetProperty("sourceSha256").GetString()) throw new FormatException("source digest mismatch");
        var xml = new UTF8Encoding(false, true).GetString(bytes);
        if (xml.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) || xml.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("DTD and entities are prohibited");
        using var text = new StringReader(xml.TrimStart('\uFEFF'));
        using var reader = XmlReader.Create(text, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumBytes,
            MaxCharactersFromEntities = 1, IgnoreComments = false,
        });
        var elements = 0;
        var root = false;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (++elements > 25_000 || reader.Depth >= 128 || reader.AttributeCount > 128)
                throw new FormatException("source XML structure limit exceeded");
            if (!root)
            {
                var expected = source.GetProperty("sourceFormat").GetString() == "bpmn+xml"
                    ? "http://www.omg.org/spec/BPMN/20100524/MODEL" : "https://www.omg.org/spec/DMN/20191111/MODEL/";
                if (reader.LocalName != "definitions" || reader.NamespaceURI != expected)
                    throw new FormatException("source format and root disagree");
                root = true;
            }
            if (reader.LocalName == "script" && reader.NamespaceURI == "http://www.omg.org/spec/BPMN/20100524/MODEL")
                throw new FormatException("source scripts are prohibited");
        }
        if (!root) throw new FormatException("source XML root missing");
        return bytes;
    }

    public static void CheckCompanion(JsonElement source, ReadOnlySpan<byte> modelBytes)
    {
        if (Digest(modelBytes) != source.GetProperty("modelArtifactSha256").GetString()) throw new FormatException("companion digest mismatch");
        using var model = JsonDocument.Parse(modelBytes.ToArray());
        foreach (var key in new[] { "repositoryId", "commitSha", "unitPath", "sourceSha256" })
            if (model.RootElement.GetProperty(key).GetString() != source.GetProperty(key).GetString())
                throw new FormatException("companion source identity mismatch");
        if (model.RootElement.GetProperty("schema").GetString() != "ce.models.v1") throw new FormatException("invalid companion profile");
    }

    public static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
