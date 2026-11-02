using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CapabilityExplorer.Capabilities.Analysis;

internal static partial class AnalysisPaths
{
    internal const int MaxBytes = 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    [GeneratedRegex(@"^[a-f0-9]{32}\z", RegexOptions.CultureInvariant)] private static partial Regex IdPattern();
    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,99}\z", RegexOptions.CultureInvariant)] private static partial Regex CapabilityPattern();
    internal static void Id(string id) { ArgumentNullException.ThrowIfNull(id); if (id.Length != 32 || !IdPattern().IsMatch(id)) throw new ArgumentException("invalid lifecycle identifier"); }
    internal static void Capability(string id) { ArgumentNullException.ThrowIfNull(id); if (id.Length is < 1 or > 100 || !CapabilityPattern().IsMatch(id)) throw new ArgumentException("invalid capability identifier"); }
    internal static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    internal static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));
    internal static void NoLinks(string path)
    {
        for (var part = Path.GetFullPath(path); part is not null; part = Path.GetDirectoryName(part))
        {
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("symbolic links are unsupported in lifecycle paths");
            // File.Exists is false for a dangling symbolic link.
            if (new FileInfo(part).LinkTarget is not null) throw new IOException("symbolic links are unsupported in lifecycle paths");
        }
    }
    internal static void DirectoryPrivate(string path)
    {
        NoLinks(path);
        if (!OperatingSystem.IsWindows()) Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else Directory.CreateDirectory(path);
    }
    internal static byte[] ReadBounded(string path)
    {
        NoLinks(path);
        if (new FileInfo(path).Length > MaxBytes) throw new InvalidDataException("lifecycle input exceeds 1 MiB");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("lifecycle input exceeds 1 MiB");
        return bytes;
    }
    internal static void Atomic(string path, byte[] bytes)
    {
        NoLinks(path);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("lifecycle record exceeds 1 MiB");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, Options(FileMode.CreateNew))) { stream.Write(bytes); stream.Flush(true); }
        File.Move(temporary, path, true);
    }
    internal static void Save<T>(string path, T value) => Atomic(path, JsonSerializer.SerializeToUtf8Bytes(value, Json));
    internal static T Read<T>(string path) => JsonSerializer.Deserialize<T>(ReadBounded(path), Json) ?? throw new InvalidDataException("empty lifecycle record");
    private static FileStreamOptions Options(FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }
    internal static FileStream Lock(string path, bool wait)
    {
        NoLinks(path);
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (true)
        {
            try
            {
                return new FileStream(path, Options(FileMode.OpenOrCreate));
            }
            catch (IOException) when (wait && DateTime.UtcNow < deadline) { Thread.Sleep(10); }
        }
    }
}
