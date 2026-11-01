using System.Diagnostics;
using System.Text;

namespace CapabilityExplorer.Capabilities;

/// <summary>Export limits may be lowered for constrained hosts; callers cannot disable the hard ceilings.</summary>
public sealed record ExportLimits(int Entries = 100_000, long Bytes = 256L * 1024 * 1024)
{
    internal void Validate()
    {
        if (Entries is <= 0 or > 100_000 || Bytes is <= 0 or > 256L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(Entries), "export limits must be positive and within hard ceilings");
        }
    }
}

public static partial class SourceParser
{
    /// <summary>Validate records loaded directly from JSON as well as records produced by the text parser.</summary>
    public static void Validate(RepositorySource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Id is null || !RepositoryId().IsMatch(source.Id) || string.IsNullOrWhiteSpace(source.Location) || source.Location.Any(char.IsControl))
        {
            throw new ArgumentException("invalid repository identity or location", nameof(source));
        }
        if (source.Ref is null || !SafeRef().IsMatch(source.Ref) || source.Ref.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("not an acceptable branch, tag or commit", nameof(source));
        }
        if (source.Kind == SourceKind.GitHub)
        {
            var match = GitHubUrl().Match(source.Location);
            if (!match.Success || match.Groups["ref"].Success || match.Groups["repo"].Value is "." or "..")
            {
                throw new ArgumentException("only canonical https://github.com/<owner>/<repo> sources are supported", nameof(source));
            }
        }
        else if (source.Kind != SourceKind.Local || source.Location.Contains("://", StringComparison.Ordinal) || source.Location.StartsWith("git@", StringComparison.Ordinal))
        {
            throw new ArgumentException("unsupported repository source kind or local location", nameof(source));
        }
    }
}

internal static class SourceSafety
{
    internal static string PrepareDestination(string destination)
    {
        var full = Path.GetFullPath(destination);
        // macOS exposes these system-owned aliases. Normalize them before checking
        // user-controlled path ancestors; arbitrary symlinks are still rejected.
        if (OperatingSystem.IsMacOS())
        {
            foreach (var alias in new[] { "/tmp/", "/var/" })
            {
                if (full.StartsWith(alias, StringComparison.Ordinal)) full = "/private" + full;
            }
        }
        RejectLinks(full);
        if (File.Exists(full) || (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any()))
        {
            throw new IOException("export destination must be an empty directory");
        }
        Directory.CreateDirectory(full);
        return full;
    }

    internal static void RejectLinks(string target)
    {
        for (var current = Path.GetFullPath(target); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("export path contains a symbolic link or reparse point");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static async Task<string> ReadBoundedAsync(StreamReader reader, Process process, int maximum, CancellationToken cancellationToken)
    {
        var output = new StringBuilder(); var buffer = new char[4096];
        while (await reader.ReadAsync(buffer, cancellationToken) is var count && count != 0)
        {
            if (output.Length > maximum - count)
            {
                Kill(process);
                throw new InvalidOperationException("git output exceeded its bounded response limit");
            }
            output.Append(buffer, 0, count);
        }
        return output.ToString();
    }

    internal static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (process.HasExited) { }
    }
}
