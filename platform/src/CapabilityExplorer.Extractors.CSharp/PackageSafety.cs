using NuGet.Versioning;

namespace CapabilityExplorer.Extractors.CSharp;

/// <summary>Package metadata is untrusted input, never a filesystem path or URL fragment.</summary>
internal static class PackageSafety
{
    public static bool Id(string value) => value.Length is > 0 and <= 100
        && value is not "." and not ".."
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
    public static bool Version(string value) => value.Length is > 0 and <= 128
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+')
        && NuGetVersion.TryParse(value, out _);
    public static void Validate(string id, string version)
    {
        if (!Id(id) || !Version(version)) throw new InvalidDataException("Invalid package ID or exact version; paths are prohibited");
    }
    public static void NoLinks(string root, string path)
    {
        var boundary = Path.GetFullPath(root);
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Symbolic links in package paths are prohibited");
            if (current == boundary) return;
        }
        throw new InvalidDataException("Package path escapes cache root");
    }
    public static void Tree(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var count = 0;
        while (pending.TryPop(out var dir))
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                if (++count > 10000) throw new InvalidDataException("Package tree exceeds entry limit");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Symbolic links in packages are prohibited");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
    }
    public static bool Https(Uri url) => url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps && url.UserInfo.Length == 0 && url.Fragment.Length == 0;
}
