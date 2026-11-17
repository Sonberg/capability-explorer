using System.Text;

namespace CapabilityExplorer.Explorer;

public static partial class ProcessLandscapeValidation
{
    private sealed record DocumentScope(
        ProcessLandscapeDocument Document, ProcessLandscapeProcess[] Processes,
        Dictionary<string, ProcessLandscapeElement> Canonical,
        Dictionary<string, ProcessLandscapeElement> Elements,
        Dictionary<string, ProcessLandscapeProcess> Owners);

    private static void ValidateDocuments(ProcessLandscapeSnapshot snapshot)
    {
        if (snapshot.Documents is not { } documents) return;
        if (documents.Count > ProcessLandscapeDocumentLimits.MaxDocuments) Reject("limit", "document-count-limit");
        var sources = snapshot.Sources.Select(source => source.Path).ToHashSet(StringComparer.Ordinal);
        var processes = snapshot.Processes.GroupBy(process => process.Path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in documents)
        {
            var document = RequireItem(item, "invalid-document");
            if (!IsSafeRelativePath(document.Path) || !sources.Contains(document.Path) ||
                !processes.ContainsKey(document.Path))
                Reject("invalid", "document-source-mismatch");
            if (!paths.Add(document.Path)) Reject("invalid", "duplicate-document-path");
            if (document.Elements.Count > ProcessLandscapeDocumentLimits.MaxElements ||
                document.Relationships.Count > ProcessLandscapeDocumentLimits.MaxRelationships)
                Reject("limit", "document-graph-count-limit");
            var scope = BuildDocumentScope(document, processes[document.Path]);
            ValidateDocumentBudgets(scope.Elements, snapshot.ContentProfile);
            long conditionBytes = 0;
            foreach (var relationship in document.Relationships)
            {
                var condition = RequireItem(relationship, "invalid-document-relationship").Condition;
                conditionBytes += Encoding.UTF8.GetByteCount(condition?.Text ?? "") +
                    Encoding.UTF8.GetByteCount(condition?.Language ?? "");
                if (conditionBytes > ProcessLandscapeDocumentLimits.MaxTextBytes) Reject("limit", "document-text-limit");
            }
            ValidateDocumentParents(scope.Elements);
            ValidateDocumentMessages(scope, snapshot.Diagnostics);
        }
    }

    private static DocumentScope BuildDocumentScope(ProcessLandscapeDocument document, ProcessLandscapeProcess[] processes)
    {
        var canonical = new Dictionary<string, ProcessLandscapeElement>(StringComparer.Ordinal);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in document.Elements)
        {
            var element = RequireItem(item, "invalid-document-element");
            ValidateElement(element, document.Path, identities);
            canonical.Add(element.Identity, element);
        }
        var elements = new Dictionary<string, ProcessLandscapeElement>(canonical, StringComparer.Ordinal);
        var owners = new Dictionary<string, ProcessLandscapeProcess>(StringComparer.Ordinal);
        foreach (var process in processes)
        {
            var local = process.Elements.Select(element => element.Identity).ToHashSet(StringComparer.Ordinal);
            foreach (var element in process.Elements)
            {
                if (IsDocumentLayout(element))
                {
                    if (!canonical.TryGetValue(element.Identity, out var original))
                        Reject("invalid", "missing-canonical-document-projection");
                    else if (!SameDocumentCore(original, element) ||
                        (element.Parent is not null && element.Parent != original.Parent) ||
                        (element.Parent is null && original.Parent is not null && local.Contains(original.Parent)))
                        Reject("invalid", "conflicting-document-projection");
                }
                else
                {
                    if (!elements.TryAdd(element.Identity, element))
                        Reject("invalid", "ambiguous-document-element");
                    owners.Add(element.Identity, process);
                }
            }
        }
        if (elements.Count > ProcessLandscapeDocumentLimits.MaxElements) Reject("limit", "document-graph-count-limit");
        var authoredIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in elements.Values)
            if (!authoredIds.Add(element.Id)) Reject("invalid", "duplicate-document-authored-id");
        return new(document, processes, canonical, elements, owners);
    }

    private static bool IsDocumentLayout(ProcessLandscapeElement element) => element.Namespace is
        "http://www.omg.org/spec/BPMN/20100524/DI" or "http://www.omg.org/spec/DD/20100524/DI" or
        "http://www.omg.org/spec/DD/20100524/DC";

    private static bool SameDocumentCore(ProcessLandscapeElement left, ProcessLandscapeElement right) =>
        left.Kind == right.Kind && left.Namespace == right.Namespace && left.Id == right.Id &&
        left.Type == right.Type && left.Order == right.Order && left.Source == right.Source && left.Content == right.Content &&
        left.Attributes.Count == right.Attributes.Count &&
        left.Attributes.All(pair => right.Attributes.TryGetValue(pair.Key, out var value) && pair.Value == value);

    private static void ValidateDocumentBudgets(Dictionary<string, ProcessLandscapeElement> elements, string profile)
    {
        long attributeBytes = 0, textBytes = 0;
        foreach (var element in elements.Values)
        {
            if (profile == "structural" && element.Content is not null) Reject("invalid", "structural-document-content");
            if (element.Attributes.Count > ProcessLandscapeDocumentLimits.MaxAttributes)
                Reject("limit", "document-attribute-limit");
            foreach (var pair in element.Attributes)
            {
                if (pair.Key.Length == 0 || pair.Value is null) Reject("invalid", "invalid-document-attribute");
                attributeBytes += Encoding.UTF8.GetByteCount(pair.Key) + Encoding.UTF8.GetByteCount(pair.Value!);
            }
            if (element.Content is { } content) textBytes += Encoding.UTF8.GetByteCount(content.Text);
            if (attributeBytes > ProcessLandscapeDocumentLimits.MaxAttributeBytes ||
                textBytes > ProcessLandscapeDocumentLimits.MaxTextBytes)
                Reject("limit", "document-text-limit");
        }
    }

    private static void ValidateDocumentParents(Dictionary<string, ProcessLandscapeElement> elements)
    {
        var depths = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var start in elements.Keys)
        {
            var path = new List<string>();
            var visiting = new HashSet<string>(StringComparer.Ordinal);
            string? current = start;
            while (current is not null && !depths.ContainsKey(current))
            {
                if (!elements.TryGetValue(current, out var element)) Reject("invalid", "dangling-document-parent");
                if (!visiting.Add(current)) Reject("invalid", "cyclic-document-parent");
                path.Add(current);
                if (path.Count > ProcessLandscapeDocumentLimits.MaxParentDepth) Reject("limit", "document-depth-limit");
                current = element!.Parent;
            }
            var depth = current is null ? 0 : depths[current];
            for (var index = path.Count - 1; index >= 0; index--)
            {
                if (++depth > ProcessLandscapeDocumentLimits.MaxParentDepth) Reject("limit", "document-depth-limit");
                depths[path[index]] = depth;
            }
        }
    }
}
