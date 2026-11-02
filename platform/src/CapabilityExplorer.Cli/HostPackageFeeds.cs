using System.Text;
using System.Text.Json;
using CapabilityExplorer.Capabilities;

namespace CapabilityExplorer.Cli;

/// <summary>Host-only secret storage and explicit, recoverable migration to a public descriptor catalogue.</summary>
internal static class HostPackageFeeds
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    internal static string DefaultCatalog(string root) => Path.Combine(root, ".sandbox-cache/package-feed-catalog/catalog.json");
    internal static string SettingsPath(string root, string? explicitPath = null)
    {
        var path = Path.GetFullPath(explicitPath ?? Environment.GetEnvironmentVariable("CE_PACKAGE_FEEDS_FILE") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config/capability-explorer/package-feeds/settings.json"));
        AnalysisPreparation.RejectLinks(path);
        var relative = Path.GetRelativePath(Path.GetFullPath(root), path);
        if (relative != ".." && !relative.StartsWith("../", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            throw new InvalidDataException("host package secrets must be outside the workspace");
        return path;
    }

    internal static string Read(string root)
    {
        var path = SettingsPath(root);
        var legacy = Path.Combine(root, ".sandbox-cache/package-feeds/settings.json");
        AnalysisPreparation.RejectLinks(legacy);
        if (File.Exists(legacy))
        {
            using var old = JsonDocument.Parse(ReadBounded(legacy));
            if (!old.RootElement.TryGetProperty("migratedToHost", out var migrated) || migrated.ValueKind != JsonValueKind.True)
                throw new InvalidDataException("legacy package feed settings require explicit host migration");
        }
        if (!File.Exists(path))
        {
            if (File.Exists(legacy) || Environment.GetEnvironmentVariable("CE_PACKAGE_FEEDS_FILE") is not null)
                throw new InvalidDataException("host package feed configuration is missing");
            return """{"feeds":[]}""";
        }
        RequirePrivate(path);
        var text = ReadBounded(path);
        using var parsed = JsonDocument.Parse(text);
        AnalysisPackagePreparation.ValidateFeeds(parsed.RootElement);
        return text;
    }

    internal static void Publish(string root)
    {
        using var parsed = JsonDocument.Parse(Read(root));
        WritePublic(DefaultCatalog(root), Catalog(parsed.RootElement));
    }

    private static PackageFeedCatalog Catalog(JsonElement settings) => new(1,
        AnalysisPackagePreparation.ValidateFeeds(settings).Select(feed => new PackageFeedDescriptor(
            feed.GetProperty("id").GetString()!, feed.GetProperty("kind").GetString()!,
            Optional(feed, "name") ?? feed.GetProperty("id").GetString()!, feed.GetProperty("url").GetString()!,
            Optional(feed, "scope"), Optional(feed, "username"), !string.IsNullOrEmpty(Optional(feed, "secret")))).ToArray());
    private static string? Optional(JsonElement value, string name) =>
        value.TryGetProperty(name, out var item) && item.ValueKind != JsonValueKind.Null ? item.GetString() : null;

    internal static int Command(string root, IReadOnlyDictionary<string, string> options)
    {
        try
        {
            if (options.Keys.Any(key => key is not ("import" or "catalog" or "host-file")) ||
                !options.TryGetValue("import", out var input) || !options.TryGetValue("catalog", out var catalog))
                throw new ArgumentException("expected package-feeds --import <legacy-settings> --catalog <public-catalog> [--host-file <outside-workspace>]");
            Import(root, input, catalog, options.GetValueOrDefault("host-file"));
            Console.WriteLine("package feeds: host configuration persisted; legacy credentials removed; public catalogue published");
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine("package feeds: migration failed (" + error.GetType().Name + ")");
            return 2;
        }
    }

    internal static void Import(string root, string input, string catalog, string? hostFile = null)
    {
        input = Path.GetFullPath(input);
        catalog = Path.GetFullPath(catalog);
        AnalysisPreparation.RejectLinks(input);
        AnalysisPreparation.RejectLinks(catalog);
        var host = SettingsPath(root, hostFile);
        if (input == host || input == catalog || host == catalog) throw new InvalidDataException("migration paths must be distinct");
        var text = ReadBounded(input);
        using var document = JsonDocument.Parse(text);
        var descriptors = Catalog(document.RootElement);
        var bytes = Encoding.UTF8.GetBytes(text);
        var directory = Path.GetDirectoryName(host)!;
        CreatePrivateDirectory(directory);
        if (File.Exists(host))
        {
            RequirePrivate(host);
            WritePrivate(Path.Combine(directory, "previous-settings-" + Guid.NewGuid().ToString("N") + ".json"),
                Encoding.UTF8.GetBytes(ReadBounded(host)));
        }
        // A verified, private host backup is preserved before any workspace sanitization.
        var backup = Path.Combine(directory, "import-backup-" + Guid.NewGuid().ToString("N") + ".json");
        WritePrivate(backup, bytes);
        if (!File.ReadAllBytes(backup).SequenceEqual(bytes)) throw new IOException("host backup verification failed");
        WritePrivate(host, bytes);
        RequirePrivate(host);
        if (!File.ReadAllBytes(host).SequenceEqual(bytes)) throw new IOException("host persistence verification failed");
        // Publish only after validated host persistence; a failed public write leaves the original intact.
        WritePublic(catalog, descriptors);
        var sanitized = new { migratedToHost = true, feeds = descriptors.Feeds };
        Atomic(input, JsonSerializer.SerializeToUtf8Bytes(sanitized, Json), true);
    }

    private static string ReadBounded(string path)
    {
        AnalysisPreparation.RejectLinks(path);
        using var stream = File.OpenRead(path);
        if (stream.Length > 65536) throw new InvalidDataException("package settings exceed bounds");
        var bytes = new byte[65537];
        var count = stream.ReadAtLeast(bytes, bytes.Length, false);
        if (count > 65536) throw new InvalidDataException("package settings exceed bounds");
        return Encoding.UTF8.GetString(bytes, 0, count);
    }
    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("host credentials require Unix permissions");
        AnalysisPreparation.RejectLinks(path);
        if (!Directory.Exists(path))
        {
            var parent = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(parent)) CreatePrivateDirectory(parent);
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private static void RequirePrivate(string path)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("host credentials require Unix permissions");
        var forbidden = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        if ((File.GetUnixFileMode(path) & forbidden) != 0 ||
            (File.GetUnixFileMode(Path.GetDirectoryName(path)!) & forbidden) != 0)
            throw new InvalidDataException("host package settings and their directory must be private");
    }
    private static void WritePrivate(string path, byte[] bytes) => Atomic(path, bytes, true);
    private static void WritePublic(string path, PackageFeedCatalog catalog)
    {
        AnalysisPreparation.RejectLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Atomic(path, JsonSerializer.SerializeToUtf8Bytes(catalog, Json), false);
    }
    private static void Atomic(string path, byte[] bytes, bool secret)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("host credentials require Unix permissions");
        AnalysisPreparation.RejectLinks(path);
        var staged = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            using (var stream = new FileStream(staged, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                UnixCreateMode = secret ? UnixFileMode.UserRead | UnixFileMode.UserWrite : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead }))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(staged, path, true);
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }
}
