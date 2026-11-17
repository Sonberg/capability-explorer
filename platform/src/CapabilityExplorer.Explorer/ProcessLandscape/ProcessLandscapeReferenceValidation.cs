namespace CapabilityExplorer.Explorer;

public static partial class ProcessLandscapeValidation
{
    private const string BpmnModelNamespace = "http://www.omg.org/spec/BPMN/20100524/MODEL";

    private static void ValidateProcessRelationships(IReadOnlyList<ProcessLandscapeProcess> processes)
    {
        // All process/source/element records must pass validation before they can vouch for a target.
        // Shared DI ancestors may repeat globally, but a cross-process call target must be unambiguous.
        var declaredRoots = new Dictionary<string, ProcessLandscapeElement>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var repeated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var process in processes)
        {
            foreach (var element in process.Elements)
            {
                if (!seen.Add(element.Identity)) repeated.Add(element.Identity);
                if (element.Kind == "bpmn" && element.Namespace == BpmnModelNamespace &&
                    element.Type == "process" && element.Id == process.ProcessId)
                    declaredRoots.TryAdd(element.Identity, element);
            }
        }

        foreach (var process in processes)
        {
            var elements = process.Elements.ToDictionary(element => element.Identity, StringComparer.Ordinal);
            foreach (var relationship in process.Relationships)
                ValidateRelationship(RequireItem(relationship, "invalid-relationship"), elements, declaredRoots, repeated);
        }
    }

    private static bool IsDeclaredCalledProcess(
        ProcessLandscapeElement source, string target,
        Dictionary<string, ProcessLandscapeElement> declaredRoots, HashSet<string> repeatedIdentities)
    {
        if (source.Kind != "bpmn" || source.Namespace != BpmnModelNamespace || source.Type != "callActivity" ||
            !source.Attributes.TryGetValue("calledElement", out var called) || called is not { Length: > 0 } ||
            repeatedIdentities.Contains(target) || !declaredRoots.TryGetValue(target, out var root))
            return false;

        // Match the producer's local process-id / #process-id form, never a QName, URI or external reference.
        var id = called.StartsWith('#') ? called[1..] : called;
        return !id.Contains(':') && !id.Contains('/') && !id.Contains('#') && id == root.Id;
    }
}
