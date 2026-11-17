using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Contracts.Collection;

namespace CapabilityExplorer.Explorer.GeneratedModels;

internal sealed record GeneratedModelBuild(
    IReadOnlyList<GeneratedModelInput> Inputs,
    IReadOnlyList<GeneratedModelDiagram> Diagrams,
    IReadOnlyList<GeneratedModelProposal> DeterministicLinks,
    IReadOnlyList<string> Diagnostics,
    bool Complete);

internal static class GeneratedModelEngine
{
    internal const string Schema = "ce.generated-models.v1";
    internal const int MaximumProducts = 128;
    internal const int MaximumInputBytes = 16 * 1024 * 1024;
    internal const int MaximumCandidates = 256;
    internal const int MaximumElementsPerDiagram = 256;
    internal const int MaximumOutputBytes = 8 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly XNamespace Bpmn = "http://www.omg.org/spec/BPMN/20100524/MODEL";
    private static readonly XNamespace Dmn = "https://www.omg.org/spec/DMN/20191111/MODEL/";
    private static readonly XNamespace Extension = "urn:capability-explorer:generated-models:v1";

    private sealed record Candidate(
        string Id,
        string Symbol,
        string Kind,
        string Label,
        string ReviewStatus,
        bool Executable,
        string ModelId,
        string RepositoryId,
        string CommitSha,
        string UnitPath,
        string SourceSha256,
        string ArtifactSha256,
        JsonElement Body);

    internal static GeneratedModelBuild Build(IReadOnlyList<ModelProduct> products, bool truncated)
    {
        if (products.Count > MaximumProducts)
            throw new InvalidDataException("Generated model inputs exceed the product limit.");

        var totalBytes = products.Sum(product => (long)product.EvidenceBytes.Length + (product.SourceEnvelopeBytes?.Length ?? 0));
        if (totalBytes > MaximumInputBytes)
            throw new InvalidDataException("Generated model inputs exceed the aggregate byte limit.");

        var diagnostics = new List<string>();
        if (truncated) diagnostics.Add("input-limit: model listing was truncated; unobserved products are unknown");

        var inputs = new List<GeneratedModelInput>(products.Count);
        var candidates = new List<Candidate>();
        foreach (var product in products.OrderBy(p => p.RepositoryId, StringComparer.Ordinal)
                     .ThenBy(p => p.CommitSha, StringComparer.Ordinal).ThenBy(p => p.Id, StringComparer.Ordinal))
        {
            if (ModelSource.Digest(product.EvidenceBytes) != product.ArtifactSha256)
                throw new InvalidDataException("A selected model product failed its digest check.");
            var evidence = product.Evidence;
            if (EvidenceProfileSchema.Validate(product.Profile, evidence).Count != 0)
                throw new InvalidDataException("A selected model product failed its evidence-profile validation.");

            var coverage = evidence.GetProperty("coverage").GetProperty("status").GetString()
                ?? throw new InvalidDataException("A selected model product has no coverage status.");
            inputs.Add(new GeneratedModelInput(product.Id, product.Profile, product.RepositoryId, product.CommitSha,
                product.UnitPath, product.ArtifactSha256, coverage, product.SourceStatus));
            if (coverage != "full") diagnostics.Add($"partial-input:{product.Id}");
            if (product.Profile != "ce.candidates.v1") continue;

            var artifact = evidence;
            if (artifact.GetProperty("repositoryId").GetString() != product.RepositoryId
                || artifact.GetProperty("commitSha").GetString() != product.CommitSha
                || artifact.GetProperty("unitPath").GetString() != product.UnitPath)
                throw new InvalidDataException("Candidate identity does not match its published model membership.");
            foreach (var item in artifact.GetProperty("candidates").EnumerateArray())
            {
                if (candidates.Count >= MaximumCandidates)
                {
                    diagnostics.Add("candidate-limit: additional candidates are unknown");
                    break;
                }

                var source = item.GetProperty("source");
                var sourceSha = source.GetProperty("sourceSha256").GetString()!;
                if (source.GetProperty("unitPath").GetString() != product.UnitPath
                    || source.GetProperty("commitSha").GetString() != product.CommitSha
                    || sourceSha != artifact.GetProperty("sourceSha256").GetString())
                    throw new InvalidDataException("Candidate source identity does not match its published product.");
                var kind = item.GetProperty("kind").GetString();
                if (kind is not ("process" or "decision")) continue;
                if (item.GetProperty("executable").GetBoolean())
                    throw new InvalidDataException("Static candidates must not be marked executable.");
                var symbol = item.GetProperty("id").GetString()!;
                candidates.Add(new Candidate(
                    CandidateId(product.Id, symbol),
                    symbol,
                    kind,
                    symbol,
                    item.GetProperty("reviewStatus").GetString()!,
                    false,
                    product.Id,
                    product.RepositoryId,
                    product.CommitSha,
                    product.UnitPath,
                    sourceSha,
                    product.ArtifactSha256,
                    item.GetProperty("body").Clone()));
            }
        }

        var diagrams = new List<GeneratedModelDiagram>();
        foreach (var candidate in candidates)
        {
            var diagram = candidate.Kind == "process" ? ProcessDiagram(candidate) : DecisionDiagram(candidate);
            if (diagram is null)
            {
                diagnostics.Add($"unsupported-candidate:{candidate.Id}");
                continue;
            }
            diagrams.Add(diagram);
        }

        var deterministic = new List<GeneratedModelProposal>();
        foreach (var candidate in candidates.Where(candidate => candidate.Kind == "process"))
        {
            foreach (var call in Walk(candidate.Body).Where(node => node.TryGetProperty("kind", out var kind) && kind.GetString() == "call"))
            {
                if (!call.TryGetProperty("symbol", out var symbol) || symbol.GetString() is not { } target)
                    continue;
                var matched = candidates.FirstOrDefault(other => other.RepositoryId == candidate.RepositoryId
                    && other.CommitSha == candidate.CommitSha && other.Symbol == target);
                if (matched is null) continue;
                deterministic.Add(new GeneratedModelProposal(candidate.Id, matched.Id, "calls",
                    "The published static candidate contains this exact method symbol reference.",
                    [candidate.Id, matched.Id], "deterministic-static-call", "unreviewed"));
            }
        }

        var complete = diagnostics.Count == 0 && candidates.Count > 0;
        if (candidates.Count == 0) diagnostics.Add("no-candidates: selected published inputs contain no supported process or decision candidates");
        return new GeneratedModelBuild(inputs, diagrams, deterministic, diagnostics, complete);
    }

    internal static string InputDigest(
        string snapshotId,
        long generation,
        IReadOnlyDictionary<string, string> selection,
        IReadOnlyList<GeneratedModelInput> inputs,
        string provider,
        bool includeSourceSnippets)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = Schema,
            snapshotId,
            generation,
            selection = selection.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            inputs,
            provider,
            includeSourceSnippets,
        }, Json);
        return ModelSource.Digest(content);
    }

    internal static string ContentHash(GeneratedModelSnapshot snapshot) =>
        ModelSource.Digest(JsonSerializer.SerializeToUtf8Bytes(snapshot with { ContentHash = "" }, Json));

    private static GeneratedModelDiagram? ProcessDiagram(Candidate candidate)
    {
        var sourceNodes = Walk(candidate.Body)
            .Where(node => node.TryGetProperty("kind", out var kind) && kind.GetString() is "if" or "call" or "return" or "throw")
            .ToArray();
        if (sourceNodes.Length is 0 or > MaximumElementsPerDiagram) return null;
        if (sourceNodes.Any(node => node.GetProperty("kind").GetString() == "if")) return null;

        var elements = new List<GeneratedModelElement>();
        var flows = new List<GeneratedModelFlow>();
        var previous = "start";
        var index = 0;
        var startCitation = Citation(candidate, candidate.Body);
        elements.Add(new("start", "start-event", "Candidate entry", null, startCitation));
        foreach (var source in sourceNodes)
        {
            var kind = source.GetProperty("kind").GetString()!;
            var citation = Citation(candidate, source);
            var id = ElementId(candidate.Id, index++);
            var condition = kind == "if" && source.TryGetProperty("rawPredicate", out var predicate) ? predicate.GetString() : null;
            var label = kind switch
            {
                "if" => "Source guard",
                "call" => "Static call",
                "return" => "Return",
                "throw" => "Failure outcome",
                _ => throw new InvalidDataException("Unsupported static process node."),
            };
            elements.Add(new(id, kind switch
            {
                "if" => "exclusive-gateway",
                "call" => "task",
                "throw" => "error-end-event",
                _ => "end-event",
            }, label, condition, citation));
            flows.Add(new(FlowId(candidate.Id, flows.Count), previous, id, condition, citation));
            previous = id;
        }
        if (elements[^1].Kind is not ("end-event" or "error-end-event"))
        {
            var end = new GeneratedModelElement("end", "end-event", "Candidate end", null, startCitation);
            elements.Add(end);
            flows.Add(new(FlowId(candidate.Id, flows.Count), previous, end.Id, null, startCitation));
        }

        var xml = BuildBpmn(candidate, elements, flows);
        return Diagram(candidate, "bpmn", elements, flows, xml, null);
    }

    private static GeneratedModelDiagram? DecisionDiagram(Candidate candidate)
    {
        var children = candidate.Body.TryGetProperty("children", out var bodyChildren)
            ? bodyChildren.EnumerateArray().ToArray() : [];
        if (children.Length is 0 or > MaximumElementsPerDiagram) return null;

        var rules = new List<(string? Predicate, string Value, GeneratedModelCitation Citation)>();
        foreach (var child in children)
        {
            if (!child.TryGetProperty("kind", out var kind)) return null;
            if (kind.GetString() == "if")
            {
                if (!child.TryGetProperty("children", out var branches)) return null;
                var branchList = branches.EnumerateArray().ToArray();
                if (branchList.Length is < 2 or > 3
                    || !branchList[0].TryGetProperty("rawPredicate", out var predicate)
                    || predicate.GetString() is not { } expression) return null;
                if (!ReturnValue(branchList[1], out var value)
                    || !CanQuoteFeel(expression) || !CanQuoteFeel(value)) return null;
                rules.Add((expression, value, Citation(candidate, child)));
                if (branchList.Length == 3)
                {
                    if (!ReturnValue(branchList[2], out value)) return null;
                    rules.Add(($"else of {expression}", value, Citation(candidate, child)));
                }
            }
            else if (kind.GetString() == "return")
            {
                if (!ReturnValue(child, out var value) || !CanQuoteFeel(value)) return null;
                rules.Add((null, value, Citation(candidate, child)));
            }
            else return null;
        }
        if (rules.Count is 0 or > MaximumElementsPerDiagram) return null;

        var elements = rules.Select((rule, index) => new GeneratedModelElement(
            ElementId(candidate.Id, index),
            "decision-rule",
            $"Rule {index + 1}",
            rule.Predicate,
            rule.Citation)).ToArray();
        var xml = BuildDmn(candidate, rules);
        return Diagram(candidate, "dmn", elements, [], null, xml);
    }

    private static GeneratedModelDiagram Diagram(
        Candidate candidate,
        string kind,
        IReadOnlyList<GeneratedModelElement> elements,
        IReadOnlyList<GeneratedModelFlow> flows,
        string? bpmn,
        string? dmn)
    {
        var citation = Citation(candidate, candidate.Body);
        return new GeneratedModelDiagram(
            "generated:" + ModelSource.Digest(Encoding.UTF8.GetBytes($"{Schema}\0{candidate.ModelId}\0{candidate.Id}")),
            kind,
            candidate.Label,
            "unreviewed",
            candidate.RepositoryId,
            candidate.CommitSha,
            candidate.UnitPath,
            elements,
            flows,
            bpmn,
            dmn,
            [citation]);
    }

    private static string BuildBpmn(Candidate candidate, IReadOnlyList<GeneratedModelElement> elements, IReadOnlyList<GeneratedModelFlow> flows)
    {
        var processId = "process_" + ModelSource.Digest(Encoding.UTF8.GetBytes(candidate.Id))[..16];
        var process = new XElement(Bpmn + "process",
            new XAttribute("id", processId),
            new XAttribute("name", candidate.Label),
            new XAttribute("isExecutable", "false"),
            new XElement(Bpmn + "documentation", "Unreviewed static-code process candidate; not an authored or approved business process."));
        foreach (var element in elements)
        {
            var xmlKind = element.Kind switch
            {
                "start-event" => "startEvent",
                "task" => "task",
                "exclusive-gateway" => "exclusiveGateway",
                "error-end-event" => "endEvent",
                _ => "endEvent",
            };
            var item = new XElement(Bpmn + xmlKind, new XAttribute("id", element.Id), new XAttribute("name", element.Label),
                new XElement(Bpmn + "extensionElements",
                    new XElement(Extension + "evidence",
                        new XAttribute("modelId", element.Citation.ModelId),
                        new XAttribute("candidateId", element.Citation.CandidateId),
                        new XAttribute("repositoryId", element.Citation.RepositoryId),
                        new XAttribute("commitSha", element.Citation.CommitSha),
                        new XAttribute("path", element.Citation.UnitPath),
                        new XAttribute("startLine", element.Citation.StartLine),
                        new XAttribute("endLine", element.Citation.EndLine))));
            if (element.Condition is { Length: > 0 })
                item.Add(new XElement(Bpmn + "documentation", $"Source guard: {element.Condition}"));
            process.Add(item);
        }
        foreach (var flow in flows)
            process.Add(new XElement(Bpmn + "sequenceFlow",
                new XAttribute("id", flow.Id), new XAttribute("sourceRef", flow.From), new XAttribute("targetRef", flow.To)));

        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(Bpmn + "definitions",
                new XAttribute("id", "definitions_" + processId),
                new XAttribute("targetNamespace", "urn:capability-explorer:generated-models:v1"),
                new XAttribute(XNamespace.Xmlns + "ce", Extension.NamespaceName),
                new XAttribute("exporter", "Capability Explorer"),
                new XAttribute("exporterVersion", "1"),
                process)).ToString(SaveOptions.DisableFormatting);
    }

    private static string BuildDmn(Candidate candidate, IReadOnlyList<(string? Predicate, string Value, GeneratedModelCitation Citation)> rules)
    {
        var id = "decision_" + ModelSource.Digest(Encoding.UTF8.GetBytes(candidate.Id))[..16];
        var decisionTable = new XElement(Dmn + "decisionTable", new XAttribute("hitPolicy", "FIRST"),
            new XElement(Dmn + "input", new XAttribute("id", id + "_input"),
                new XElement(Dmn + "inputExpression", new XAttribute("typeRef", "string"), new XElement(Dmn + "text", "source condition text"))),
            new XElement(Dmn + "output", new XAttribute("id", id + "_output"), new XAttribute("name", "source return value"), new XAttribute("typeRef", "string")));
        foreach (var (predicate, value, citation) in rules)
        {
            decisionTable.Add(new XElement(Dmn + "rule",
                new XElement(Dmn + "inputEntry", new XElement(Dmn + "text", predicate is null ? "-" : FeelString(predicate))),
                new XElement(Dmn + "outputEntry", new XElement(Dmn + "text", FeelString(value))),
                new XElement(Dmn + "description",
                    $"unreviewed source-text map, not translated business logic; source {citation.RepositoryId}@{citation.CommitSha}:{citation.UnitPath}:{citation.StartLine}-{citation.EndLine}")));
        }
        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(Dmn + "definitions",
                new XAttribute("id", "definitions_" + id),
                new XAttribute("name", candidate.Label),
                new XAttribute("namespace", "urn:capability-explorer:generated-models:v1"),
                new XAttribute(XNamespace.Xmlns + "ce", Extension.NamespaceName),
                new XElement(Dmn + "decision",
                    new XAttribute("id", id),
                    new XAttribute("name", candidate.Label),
                    new XElement(Dmn + "description", "Unreviewed static-code decision candidate; not an approved business rule."),
                    decisionTable))).ToString(SaveOptions.DisableFormatting);
    }

    private static IEnumerable<JsonElement> Walk(JsonElement node)
    {
        yield return node;
        if (!node.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array) yield break;
        foreach (var child in children.EnumerateArray())
            foreach (var descendant in Walk(child))
                yield return descendant;
    }

    private static bool ReturnValue(JsonElement node, out string value)
    {
        value = "";
        if (!node.TryGetProperty("kind", out var kind) || kind.GetString() != "return"
            || !node.TryGetProperty("children", out var children) || children.GetArrayLength() != 1) return false;
        var result = children[0];
        if (!result.TryGetProperty("kind", out kind) || kind.GetString() != "constant"
            || !result.TryGetProperty("value", out var output) || output.ValueKind != JsonValueKind.String) return false;
        value = output.GetString()!;
        return value.Length <= 256;
    }

    private static bool CanQuoteFeel(string value) =>
        !value.Any(character => character is '"' or '\\' || char.IsControl(character));

    private static string FeelString(string value) => $"\"{value}\"";

    private static GeneratedModelCitation Citation(Candidate candidate, JsonElement sourceNode)
    {
        var source = sourceNode.TryGetProperty("source", out var nodeSource) ? nodeSource : default;
        var start = source.ValueKind == JsonValueKind.Object && source.TryGetProperty("startLine", out var startLine)
            ? startLine.GetInt32() : 1;
        var end = source.ValueKind == JsonValueKind.Object && source.TryGetProperty("endLine", out var endLine)
            ? endLine.GetInt32() : start;
        return new(candidate.ModelId, candidate.Id, candidate.RepositoryId, candidate.CommitSha, candidate.UnitPath,
            Math.Max(1, start), Math.Max(Math.Max(1, start), end), candidate.SourceSha256, candidate.ArtifactSha256);
    }

    private static string ElementId(string candidateId, int index) =>
        "element_" + ModelSource.Digest(Encoding.UTF8.GetBytes($"{candidateId}\0{index}"))[..16];

    internal static string CandidateId(string modelId, string symbol) =>
        "candidate:" + ModelSource.Digest(Encoding.UTF8.GetBytes($"{modelId}\0{symbol}"));

    private static string FlowId(string candidateId, int index) =>
        "flow_" + ModelSource.Digest(Encoding.UTF8.GetBytes($"{candidateId}\0flow\0{index}"))[..16];

    internal static IReadOnlyList<GeneratedModelProposal> ValidateProposals(
        string content,
        IReadOnlyDictionary<string, string> candidateKinds)
    {
        if (Encoding.UTF8.GetByteCount(content) > 262_144)
            throw new InvalidDataException("Generated model provider response exceeds its byte limit.");

        using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (!EvidenceProfileSchema.HasUnambiguousStructure(root)
            || root.ValueKind != JsonValueKind.Object
            || root.EnumerateObject().Count() != 1
            || !root.TryGetProperty("proposals", out var proposals)
            || proposals.ValueKind != JsonValueKind.Array
            || proposals.GetArrayLength() > 100)
            throw new InvalidDataException("Generated model provider response has an invalid shape.");

        var results = new List<GeneratedModelProposal>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in proposals.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || item.EnumerateObject().Count() != 5
                || !item.TryGetProperty("fromCandidateId", out var fromElement)
                || !item.TryGetProperty("toCandidateId", out var toElement)
                || !item.TryGetProperty("relation", out var relationElement)
                || !item.TryGetProperty("rationale", out var rationaleElement)
                || !item.TryGetProperty("evidenceCandidateIds", out var citationsElement)
                || fromElement.ValueKind != JsonValueKind.String
                || toElement.ValueKind != JsonValueKind.String
                || relationElement.ValueKind != JsonValueKind.String
                || rationaleElement.ValueKind != JsonValueKind.String
                || fromElement.GetString() is not { } from
                || toElement.GetString() is not { } to
                || relationElement.GetString() is not ("uses-decision" or "related-to")
                || rationaleElement.GetString() is not { Length: > 0 and <= 500 } rationale
                || citationsElement.ValueKind != JsonValueKind.Array
                || citationsElement.GetArrayLength() is < 2 or > 8)
                throw new InvalidDataException("Generated model provider returned an invalid proposal.");

            var citations = citationsElement.EnumerateArray().Select(citation =>
            {
                if (citation.ValueKind != JsonValueKind.String) throw new InvalidDataException("Generated model citation is not a string.");
                return citation.GetString()!;
            }).ToArray();
            if (from == to || !candidateKinds.ContainsKey(from) || !candidateKinds.ContainsKey(to)
                || citations.Any(citation => !candidateKinds.ContainsKey(citation))
                || citations.Distinct(StringComparer.Ordinal).Count() != citations.Length
                || !citations.Contains(from, StringComparer.Ordinal)
                || !citations.Contains(to, StringComparer.Ordinal))
                throw new InvalidDataException("Generated model proposal cites evidence outside the selected input.");

            var relation = relationElement.GetString()!;
            if (relation == "uses-decision" && (candidateKinds[from] != "process" || candidateKinds[to] != "decision"))
                throw new InvalidDataException("Generated model proposal uses an invalid process-to-decision relationship.");
            if (!unique.Add($"{from}\0{to}\0{relation}")) continue;
            results.Add(new GeneratedModelProposal(from, to, relation, rationale, citations, "ai-inferred", "unreviewed"));
        }
        return results;
    }
}
