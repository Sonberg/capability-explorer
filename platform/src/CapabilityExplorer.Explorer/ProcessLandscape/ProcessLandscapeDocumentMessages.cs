namespace CapabilityExplorer.Explorer;

public static partial class ProcessLandscapeValidation
{
    private static readonly HashSet<string> MessageFlowNodeTypes = new(StringComparer.Ordinal)
    {
        "task", "userTask", "serviceTask", "scriptTask", "manualTask", "businessRuleTask", "sendTask", "receiveTask",
        "subProcess", "transaction", "adHocSubProcess", "callActivity", "startEvent", "endEvent",
        "intermediateCatchEvent", "intermediateThrowEvent", "boundaryEvent",
    };

    private static bool IsBpmnType(ProcessLandscapeElement element, string type) =>
        element.Kind == "bpmn" && element.Namespace == BpmnModelNamespace && element.Type == type;

    private static void ValidateDocumentMessages(DocumentScope scope, IReadOnlyList<ProcessLandscapeDiagnostic> diagnostics)
    {
        var roots = new Dictionary<string, string>(StringComparer.Ordinal);
        var rootCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var process in scope.Processes)
        {
            var declared = process.Elements.Where(element => IsBpmnType(element, "process") &&
                element.Id == process.ProcessId).ToArray();
            if (declared.Length != 1) continue;
            roots[process.ProcessIdentity] = declared[0].Id;
            rootCounts[declared[0].Id] = rootCounts.GetValueOrDefault(declared[0].Id) + 1;
        }
        var pools = new Dictionary<(string Collaboration, string Process), List<string>>();
        foreach (var participant in scope.Canonical.Values.Where(element => IsBpmnType(element, "participant")))
        {
            if (participant.Parent is not { } parent || !scope.Canonical.TryGetValue(parent, out var collaboration) ||
                !IsBpmnType(collaboration, "collaboration") ||
                !participant.Attributes.TryGetValue("processRef", out var processRef)) continue;
            var key = (parent, processRef);
            if (!pools.TryGetValue(key, out var list)) pools[key] = list = [];
            list.Add(participant.Identity);
        }
        var identities = scope.Processes.SelectMany(process => process.Relationships)
            .Select(relationship => relationship.Identity).ToHashSet(StringComparer.Ordinal);
        var carriers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in scope.Document.Relationships)
        {
            var relationship = RequireItem(item, "invalid-document-relationship");
            if (!RelationshipIdentityPattern().IsMatch(relationship.Identity) || !identities.Add(relationship.Identity))
                Reject("invalid", "invalid-document-relationship-identity");
            if (relationship.Kind != "messageFlow" || relationship.Order < 0)
                Reject("invalid", "invalid-document-relationship-kind");
            if (!scope.Canonical.TryGetValue(relationship.ElementIdentity, out var carrier) ||
                !IsBpmnType(carrier, "messageFlow") || !carriers.Add(carrier.Identity))
                Reject("invalid", "invalid-message-flow-carrier");
            var parent = carrier!.Parent;
            if (parent is null || !scope.Canonical.TryGetValue(parent, out var collaboration) ||
                !IsBpmnType(collaboration, "collaboration"))
                Reject("invalid", "invalid-message-flow-scope");
            scope.Elements.TryGetValue(relationship.Source, out var source);
            scope.Elements.TryGetValue(relationship.Target, out var target);
            if (source is null || target is null ||
                !carrier.Attributes.TryGetValue("sourceRef", out var sourceRef) || sourceRef != source.Id ||
                !carrier.Attributes.TryGetValue("targetRef", out var targetRef) || targetRef != target.Id)
                Reject("invalid", "invalid-message-flow-endpoint");
            var sourcePool = DocumentEndpointPool(source!, parent!, scope, roots, rootCounts, pools);
            var targetPool = DocumentEndpointPool(target!, parent!, scope, roots, rootCounts, pools);
            if (sourcePool is null || targetPool is null || sourcePool == targetPool)
                Reject("invalid", "unresolved-message-flow-participation");
        }
        var unresolved = diagnostics.Where(diagnostic => diagnostic.Source?.Path == scope.Document.Path)
            .Select(diagnostic => diagnostic.Source!.Element).ToHashSet(StringComparer.Ordinal);
        foreach (var carrier in scope.Canonical.Values.Where(element => IsBpmnType(element, "messageFlow")))
            if (!carriers.Contains(carrier.Identity) && !unresolved.Contains(carrier.Id))
                Reject("invalid", "unreported-document-message-flow");
    }

    private static string? DocumentEndpointPool(
        ProcessLandscapeElement endpoint, string collaboration, DocumentScope scope,
        Dictionary<string, string> roots, Dictionary<string, int> rootCounts,
        Dictionary<(string Collaboration, string Process), List<string>> pools)
    {
        if (IsBpmnType(endpoint, "participant"))
            return endpoint.Parent == collaboration && scope.Canonical.ContainsKey(endpoint.Identity) ? endpoint.Identity : null;
        if (endpoint.Kind != "bpmn" || endpoint.Namespace != BpmnModelNamespace || !MessageFlowNodeTypes.Contains(endpoint.Type) ||
            !scope.Owners.TryGetValue(endpoint.Identity, out var owner) || !roots.TryGetValue(owner.ProcessIdentity, out var process) ||
            process.IndexOfAny([':', '/', '#']) >= 0 || rootCounts[process] != 1 ||
            !pools.TryGetValue((collaboration, process), out var matches) || matches.Count != 1)
            return null;
        return matches[0];
    }
}
