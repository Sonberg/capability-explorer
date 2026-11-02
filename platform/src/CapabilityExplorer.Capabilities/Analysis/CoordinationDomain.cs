namespace CapabilityExplorer.Capabilities.Analysis;

/// <summary>Rejects cross-kernel lock domains before trusting advisory filesystem locks.</summary>
internal static class CoordinationDomain
{
    internal static string Current()
    {
        if (OperatingSystem.IsMacOS()) return "macos-native";
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("analysis lifecycle requires Linux or macOS");
        var boot = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
        if (!Guid.TryParseExact(boot, "D", out _)) throw new IOException("cannot establish Linux coordination domain");
        return "linux:" + boot;
    }
    internal static void Initialize(string root)
    {
        var path = Path.Combine(root, "coordination-domain");
        AnalysisPaths.NoLinks(path);
        if (!File.Exists(path))
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            try
            {
                using var stream = new FileStream(path, options);
                stream.Write(System.Text.Encoding.UTF8.GetBytes(Current()));
                stream.Flush(true);
            }
            catch (IOException) when (File.Exists(path)) { /* Another creator won. Validate its complete marker below. */ }
        }
        Validate(root);
    }
    internal static void Validate(string root)
    {
        var path = Path.Combine(root, "coordination-domain");
        AnalysisPaths.NoLinks(path);
        if (!File.Exists(path) || new FileInfo(path).Length > 128 || File.ReadAllText(path) != Current())
            throw new IOException("lifecycle coordination domain mismatch or incomplete marker; preserve evidence and use one qualified host domain");
    }
}
