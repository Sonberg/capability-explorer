namespace CapabilityExplorer.Explorer;

internal static class SourcePathResolver
{
    public static string GitDirectory(CapabilityExplorer.Capabilities.RepositorySource source, string workspace)
    {
        CapabilityExplorer.Capabilities.SourceParser.Validate(source);
        if (source.Kind == CapabilityExplorer.Capabilities.SourceKind.Local)
        {
            var path = source.Location;
            if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal))
            {
                var home = Environment.GetEnvironmentVariable("CE_HOST_HOME") is { Length: > 0 } host
                    ? host : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                path = path == "~" ? home : Path.Combine(home, path[2..]);
            }
            return Path.GetFullPath(path);
        }
        return Path.Combine(Path.GetFullPath(workspace), "github", new Uri(source.Location).AbsolutePath.Trim('/') + ".git");
    }

    public static string? ForCSharpComponent(ViewIndex view, string symbolKey)
    {
        var symbol = view.Node(symbolKey)?.Record;
        var typeKey = TypeKey(symbolKey, symbol?.NodeKind);
        if (typeKey is null || !view.Nodes.TryGetValue(typeKey, out var occurrences))
        {
            return null;
        }

        return SelectPath(occurrences
            .Where(o => o.Record.NodeKind == "Type")
            .SelectMany(o => o.Record.Evidence
                .Where(o.Artifact.EvidenceById.ContainsKey)
                .Select(id => o.Artifact.EvidenceById[id].Path)));
    }

    public static string? ForRequest(ViewIndex view, string fromKey, IEnumerable<string> evidencePaths)
    {
        var paths = evidencePaths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.Ordinal).ToList();
        var caller = view.Node(fromKey);
        var callerPaths = caller is null
            ? []
            : caller.Record.Evidence.Where(caller.Artifact.EvidenceById.ContainsKey)
                .Select(id => caller.Artifact.EvidenceById[id].Path).ToHashSet(StringComparer.Ordinal);
        var matching = paths.Where(callerPaths.Contains).ToList();
        return SelectPath(matching.Count > 0 ? matching : paths);
    }

    internal static string? TypeKey(string symbolKey, string? nodeKind)
    {
        var segments = symbolKey.Split('|');
        if (segments.Length < 4 || segments[0] != "cs")
        {
            return null;
        }

        if (nodeKind == "Type")
        {
            return symbolKey;
        }

        var body = segments[3].Length > 2 && segments[3][1] == ':' ? segments[3][2..] : segments[3];
        var paren = body.IndexOf('(', StringComparison.Ordinal);
        var head = paren >= 0 ? body[..paren] : body;
        var dot = head.LastIndexOf('.');
        return dot > 0 ? $"cs|{segments[1]}|{segments[2]}|T:{head[..dot]}" : null;
    }

    internal static string? SelectPath(IEnumerable<string> paths) => paths
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .FirstOrDefault();
}