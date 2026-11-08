using System.Text;
using System.Xml;

namespace CapabilityExplorer.Explorer;

/// <summary>Direct C# port of the client's <c>decision-links.ts</c>: the same BPMN namespace, the same
/// <c>urn:capability-explorer:model-links:v1</c> decision-link attributes, the same bounds and the same
/// resolution statuses, so a server answer and the UI's own reading of the same XML never disagree.</summary>
public static class DecisionReferenceResolver
{
    private const string BpmnNamespace = "http://www.omg.org/spec/BPMN/20100524/MODEL";
    private const string LinkNamespace = "urn:capability-explorer:model-links:v1";
    private const int MaximumBytes = 1_048_576;
    private const int MaximumElements = 500;

    public sealed record Reference(string ElementId, string Label, IReadOnlyList<string> References);

    /// <summary>Never resolves a URI and never fetches anything outside the given XML text; throws
    /// <see cref="FormatException"/> for DTD/ENTITY declarations, oversize input, non-BPMN root or script elements.</summary>
    public static IReadOnlyList<Reference> Read(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        if (Encoding.UTF8.GetByteCount(xml) > MaximumBytes) throw new FormatException("decision reference source exceeds the one MiB bound");
        if (xml.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) || xml.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("decision reference source must not declare a DTD or entity");

        using var text = new StringReader(xml.TrimStart('\uFEFF'));
        using var reader = XmlReader.Create(text, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumBytes, MaxCharactersFromEntities = 1,
        });

        var results = new List<Reference>();
        var elements = 0;
        var root = false;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (++elements > MaximumElements || reader.Depth >= 128 || reader.AttributeCount > 128)
                throw new FormatException("decision reference source exceeds structural bounds");
            if (!root)
            {
                if (reader.LocalName != "definitions" || reader.NamespaceURI != BpmnNamespace)
                    throw new FormatException("decision references require a BPMN definitions document");
                root = true;
            }

            if (string.Equals(reader.LocalName, "script", StringComparison.OrdinalIgnoreCase))
                throw new FormatException("decision reference source must not contain executable script elements");
            if (reader.NamespaceURI != BpmnNamespace) continue;

            var id = reader.GetAttribute("id");
            if (id is null) continue;
            var raw = string.Join(' ', new[] { reader.GetAttribute("decisionRef", LinkNamespace), reader.GetAttribute("decisionRefs", LinkNamespace) }
                .Where(v => !string.IsNullOrEmpty(v)));
            if (raw.Length == 0) continue;
            var refs = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToList();
            results.Add(new Reference(id, reader.GetAttribute("name") is { Length: > 0 } name ? name : id, refs));
        }

        if (!root) throw new FormatException("decision reference source is missing a BPMN root");
        return results;
    }

    public sealed record Target(string ModelId, string DecisionId);

    /// <summary>Resolution is restricted to the same repository and the same exact commit as the source model:
    /// nothing is ever matched across revisions, and nothing outside <paramref name="candidates"/> is consulted.</summary>
    public static (string Status, Target? Target) Resolve(string reference, string sourceUnitPath, string repositoryId, string commitSha,
        IReadOnlyList<ModelSummaryLike> candidates)
    {
        var parts = reference.Split('#');
        var path = parts.ElementAtOrDefault(0) ?? "";
        var decisionId = parts.ElementAtOrDefault(1) ?? "";
        if (parts.Length != 2 || path.Length == 0 || decisionId.Length == 0 || reference.IndexOfAny(['\u003a', '\\', '?', '%']) >= 0
            || path.StartsWith('/') || path.Split('/').Any(p => p is "" or "." or "..") || !IsDecisionId(decisionId))
        {
            return ("external", null);
        }

        var slash = sourceUnitPath.LastIndexOf('/');
        var directory = slash < 0 ? "" : sourceUnitPath[..(slash + 1)];
        var targetPath = directory + path;
        var matches = candidates.Where(c => c.Authored && c.ModelKind == "dmn" && c.RepositoryId == repositoryId
            && c.CommitSha == commitSha && c.UnitPath == targetPath).ToList();
        if (matches.Count > 1) return ("ambiguous", null);
        if (matches.Count == 0) return ("missing", null);
        var target = matches[0];
        if (target.SourceStatus != "available") return (target.SourceStatus == "denied" ? "denied" : "missing", null);
        return ("available", new Target(target.ModelId, decisionId));
    }

    private static bool IsDecisionId(string value) =>
        value.Length > 0 && (char.IsAsciiLetter(value[0]) || value[0] == '_') && value.Skip(1).All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
}

/// <summary>The minimal shape <see cref="DecisionReferenceResolver.Resolve"/> needs from either evidence source's
/// model summaries, independent of whether they came from files or Postgres.</summary>
public sealed record ModelSummaryLike(string ModelId, bool Authored, string ModelKind, string RepositoryId, string CommitSha, string UnitPath, string SourceStatus);
