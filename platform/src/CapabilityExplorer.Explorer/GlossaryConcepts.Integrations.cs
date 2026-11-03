using System.Text.Json;

namespace CapabilityExplorer.Explorer;

public static partial class GlossaryConcepts
{
    private static List<HttpLink> IntegrationLinks(IEnumerable<Decl> members, IReadOnlyList<HttpLink> links)
    {
        var declarations = members.ToList();
        return links.Where(link => declarations.Any(declaration => MatchesIntegration(declaration, link))).ToList();
    }

    private static bool MatchesIntegration(Decl declaration, HttpLink link)
    {
        if (declaration.Repo != link.Request.RepositoryId)
        {
            return false;
        }

        if (CSharpType(declaration.Key) is { } declaredType &&
            CSharpOwnerType(link.Request.From.Key) is { } usedType)
        {
            return string.Equals(declaredType, usedType, StringComparison.Ordinal);
        }

        if (!GlossaryVocabulary.PropIsTrue(declaration.Node, "exported") ||
            TypeScriptType(declaration.Key) is not { } declaredSymbol ||
            link.Request.Props is not { ValueKind: JsonValueKind.Object } props ||
            !props.TryGetProperty("responseType", out var responseType) || responseType.ValueKind != JsonValueKind.String ||
            TypeScriptType(responseType.GetString()!) is not { } usage)
        {
            return false;
        }

        return string.Equals(declaredSymbol.Repository, usage.Repository, StringComparison.Ordinal) &&
            string.Equals(declaredSymbol.Path, usage.Path, StringComparison.Ordinal) &&
            string.Equals(declaredSymbol.Name, usage.Name, StringComparison.Ordinal);
    }

    private static string? CSharpType(string key)
    {
        var parts = key.Split('|');
        return parts.Length == 4 && parts[0] == "cs" && parts[3].StartsWith("T:", StringComparison.Ordinal)
            ? parts[3][2..]
            : null;
    }

    private static string? CSharpOwnerType(string key)
    {
        var parts = key.Split('|');
        if (parts.Length != 4 || parts[0] != "cs")
        {
            return null;
        }

        if (parts[3].StartsWith("T:", StringComparison.Ordinal))
        {
            return parts[3][2..];
        }

        if (parts[3].Length < 3 || parts[3][1] != ':' || parts[3][0] is not ('M' or 'P' or 'F'))
        {
            return null;
        }

        var member = parts[3][2..];
        var parameters = member.IndexOf('(', StringComparison.Ordinal);
        if (parameters >= 0)
        {
            member = member[..parameters];
        }

        var separator = member.LastIndexOf('.');
        return separator > 0 ? member[..separator] : null;
    }

    private static (string Repository, string Path, string Name)? TypeScriptType(string key)
    {
        var parts = key.Split('|');
        if (parts.Length != 5 || parts[0] != "ts" || !parts[4].EndsWith("#type", StringComparison.Ordinal))
        {
            return null;
        }

        var name = parts[4][..^"#type".Length];
        return name.Length == 0 ? null : (parts[1], parts[3], name);
    }
}
