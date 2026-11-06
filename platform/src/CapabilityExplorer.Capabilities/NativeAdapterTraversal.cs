using System.Security.Cryptography;

namespace CapabilityExplorer.Capabilities;

public static partial class NativeAdapterPlanner
{
    private static List<FileEntry> Enumerate(
        string root,
        NativeAdapterLimits limits,
        List<string> excludedDirectories,
        List<AdapterPlanDiagnostic> diagnostics,
        CancellationToken cancellationToken,
        out bool complete)
    {
        complete = true;
        var entries = new List<FileEntry>();
        var pending = new Stack<(string FullPath, string RelativePath, int Depth)>();
        pending.Push((root, string.Empty, 0));
        var visited = 0;
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (directory.Depth > limits.MaxDepth)
            {
                diagnostics.Add(new("depth-limit-exceeded", directory.RelativePath, "source traversal depth limit exceeded"));
                complete = false;
                continue;
            }

            List<string> children = [];
            try
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(directory.FullPath))
                {
                    if (visited >= limits.MaxEntries)
                    {
                        diagnostics.Add(new("entry-limit-exceeded", directory.RelativePath,
                            "source traversal stopped at the declared entry limit"));
                        complete = false;
                        pending.Clear();
                        children.Clear();
                        break;
                    }

                    visited++;
                    children.Add(child);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new("directory-inaccessible", directory.RelativePath,
                    "source directory could not be enumerated"));
                complete = false;
                continue;
            }

            children.Sort(StringComparer.Ordinal);
            for (var index = children.Count - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var child = children[index];
                var relativePath = ToLogicalPath(Path.GetRelativePath(root, child));
                FileAttributes attributes;
                bool isLink;
                try
                {
                    attributes = File.GetAttributes(child);
                    isLink = (attributes & FileAttributes.ReparsePoint) != 0 ||
                        new FileInfo(child).LinkTarget is not null || new DirectoryInfo(child).LinkTarget is not null;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    diagnostics.Add(new("entry-inaccessible", relativePath, "source entry metadata could not be read"));
                    complete = false;
                    entries.Add(new(relativePath, child, false, true));
                    continue;
                }

                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if (isLink)
                {
                    diagnostics.Add(new("linked-input", relativePath, "symbolic links and reparse points are not followed"));
                    complete = false;
                    entries.Add(new(relativePath, child, isDirectory, true));
                    continue;
                }

                if (isDirectory)
                {
                    var name = Path.GetFileName(child);
                    if (IgnoredDirectories.Contains(name))
                    {
                        excludedDirectories.Add(relativePath);
                        continue;
                    }

                    pending.Push((child, relativePath, directory.Depth + 1));
                }
                else
                {
                    entries.Add(new(relativePath, child, false, false));
                }
            }

            if (!complete && diagnostics.Any(item => item.Code == "entry-limit-exceeded"))
            {
                break;
            }
        }

        return entries;
    }

    private static async Task<HashedInput> HashInputAsync(
        string path,
        long maxFileBytes,
        long remainingTotalBytes,
        CancellationToken cancellationToken)
    {
        if (remainingTotalBytes <= 0)
        {
            throw new InputLimitException("total-byte-limit", "aggregate input byte limit exhausted", 0);
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maxFileBytes)
        {
            throw new InputLimitException("file-byte-limit", "input exceeds the declared per-file byte limit", stream.Length);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long length = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (length + read > maxFileBytes)
            {
                throw new InputLimitException("file-byte-limit", "input exceeds the declared per-file byte limit", length + read);
            }

            if (length + read > remainingTotalBytes)
            {
                throw new InputLimitException("total-byte-limit", "input exceeds the remaining aggregate byte limit", length + read);
            }

            hash.AppendData(buffer, 0, read);
            length += read;
        }

        return new("read", Convert.ToHexStringLower(hash.GetHashAndReset()), length, null);
    }

    private sealed record FileEntry(string Path, string FullPath, bool IsDirectory, bool IsLink);
    private sealed record HashedInput(string Status, string? Sha256, long ByteLength, string? ErrorCode);
    private sealed class InputLimitException(string code, string message, long byteLength) : IOException(message)
    {
        public string Code { get; } = code;
        public long ByteLength { get; } = byteLength;
    }
}
