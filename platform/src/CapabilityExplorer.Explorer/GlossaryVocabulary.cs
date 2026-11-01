using System.Text.Json;
using System.Text.RegularExpressions;
using CapabilityExplorer.Contracts;

namespace CapabilityExplorer.Explorer;

/// <summary>
/// Reviewable naming conventions behind glossary concepts (MADR-0005). Every list is data, covered by tests, and used
/// only for the stem and for score features. None of it is applied to MADR-0004 term-candidate normalization.
/// </summary>
internal static class GlossaryVocabulary
{
    /// <summary>A type that is a shape of a concept (transport, storage, request): folded into the concept.</summary>
    public static readonly IReadOnlySet<string> Representation = new HashSet<string>(StringComparer.Ordinal)
    {
        "dto", "entity", "record", "row", "response", "request", "entry", "model", "payload", "view",
        "input", "body", "result", "res", "args", "summary",
    };

    /// <summary>A type that does work around a concept (operation, wiring, UI props, test): not vocabulary by itself.</summary>
    public static readonly IReadOnlySet<string> Role = new HashSet<string>(StringComparer.Ordinal)
    {
        "controller", "repository", "calculator", "client", "publisher", "consumer", "job", "loader", "endpoints",
        "context", "normalizer", "api", "service", "handler", "listener", "router", "config", "options", "settings",
        "validator", "mapper", "factory", "provider", "exception", "command", "query", "test", "tests", "formatter",
        "resolver", "builder", "base", "extensions", "props", "prop", "node", "nodes", "edge", "edges", "hook", "hub",
        "tracker",
    };

    /// <summary>A leading verb names an operation (a command or request model), not a thing.</summary>
    public static readonly IReadOnlySet<string> Verbs = new HashSet<string>(StringComparer.Ordinal)
    {
        "update", "create", "add", "get", "delete", "remove", "cancel", "set", "sync", "ensure", "schedule", "send",
        "fetch", "search", "list", "apply", "adjust", "reschedule", "initiate", "initate", "start", "place", "submit",
    };

    /// <summary>Words that look plural but are not.</summary>
    public static readonly IReadOnlySet<string> NotPlural = new HashSet<string>(StringComparer.Ordinal)
        { "stats", "series", "news", "status" };

    /// <summary>Folders that do not name an integration when they sit under a project root.</summary>
    public static readonly IReadOnlySet<string> StructuralFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "src", "lib", "services", "clients", "app" };

    public static readonly IReadOnlySet<string> GenericFeatureFolders = new HashSet<string>(StringComparer.Ordinal)
        { "models", "model", "src", "app", "lib", "glufs" };

    /// <summary>Folders that hold operations, not a feature's own model ("Queries/FindCustomer", "Jobs/SyncVariants").</summary>
    public static readonly IReadOnlySet<string> OperationFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "queries", "commands", "jobs", "migrations", "events", "handlers", "validators" };

    private static readonly Regex Version = new(@"^v\d+$", RegexOptions.CultureInvariant);
    private static readonly Regex InterfaceName = new(@"^I[A-Z][a-z]", RegexOptions.CultureInvariant);

    // Test project folders and file conventions. Tests are excluded from concepts and counted.
    private static readonly Regex TestPath = new(
        @"(^|/)(tests?|__tests__)/|\.Tests?/|\.(test|spec)\.[jt]sx?$", RegexOptions.CultureInvariant);

    public static bool IsTestPath(string unitPath, string evidencePath) => TestPath.IsMatch(unitPath + "/" + evidencePath);

    public static string Singular(string word)
    {
        if (NotPlural.Contains(word))
        {
            return word;
        }

        if (word.Length > 4 && word.EndsWith("ies", StringComparison.Ordinal))
        {
            return word[..^3] + "y";
        }

        return word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal) &&
            !word.EndsWith("us", StringComparison.Ordinal) ? word[..^1] : word;
    }

    public static string TypeKind(NodeRecord node) =>
        node.Props is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty("typeKind", out var k) &&
        k.ValueKind == JsonValueKind.String ? k.GetString()! : "";

    public static bool PropIsTrue(NodeRecord node, string name) =>
        node.Props is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>The words of the declared name after dropping the schema (tables) or the interface "I" prefix.</summary>
    public static IReadOnlyList<string> BaseWords(NodeRecord node)
    {
        var name = node.Name;
        if (node.NodeKind == "Table" && name.IndexOf('.') is var dot and >= 0)
        {
            name = name[(dot + 1)..];
        }

        if (node.NodeKind == "Type" && TypeKind(node) == "interface" && InterfaceName.IsMatch(name))
        {
            name = name[1..];
        }

        return GlossaryNormalization.Words(name);
    }

    /// <summary>Equal keys form one concept. Folding is visible: <see cref="Folded"/> and <see cref="Role"/> say what was removed.</summary>
    public readonly record struct Stem(string Key, string Display, bool Role, string RoleWord, bool Folded);

    public static Stem StemOf(NodeRecord node)
    {
        var words = BaseWords(node).ToList();
        var folded = false;
        while (words.Count > 1 && (Representation.Contains(words[^1]) || Version.IsMatch(words[^1])))
        {
            // "SubscriptionV2" is the model itself; "SubscriptionDto" is a shape of it.
            folded |= Representation.Contains(words[^1]);
            words.RemoveAt(words.Count - 1);
        }

        var role = words.Count >= 2 && Role.Contains(words[^1]);
        var display = string.Join(' ', words);
        if (words.Count > 0)
        {
            words[^1] = Singular(words[^1]);
        }

        return new Stem(string.Join(' ', words), display, role, role ? words[^1] : "", folded);
    }
}
