namespace CapabilityExplorer.Extractors.CSharp;

internal static class CandidatePaths
{
    internal static string Validate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Length > 4096 || path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("..") ||
            path.Any(char.IsControl))
            throw new ArgumentException("Unsafe path.");
        var full = Path.GetFullPath(path);
        FileSystemInfo? item = new FileInfo(full);
        while (item is not null)
        {
            if (item.LinkTarget is not null || (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new ArgumentException("Symlinks are outside this recipe.");
            item = item switch { FileInfo file => file.Directory, DirectoryInfo directory => directory.Parent, _ => null };
        }
        return full;
    }

    internal static void Write(string source, string output, string content)
    {
        var path = Validate(output);
        if (string.Equals(Validate(source), path, StringComparison.OrdinalIgnoreCase) || Directory.Exists(path))
            throw new ArgumentException("Output must not replace the source.");
        var staging = Validate(path + ".candidate-staging");
        var created = false;
        try
        {
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
                writer.Write(content);
            }
            File.Move(staging, path, overwrite: true);
        }
        finally
        {
            if (created && File.Exists(staging)) File.Delete(staging);
        }
    }
}
