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
            var temporary = Path.Combine(root, ".coordination-domain." + Guid.NewGuid().ToString("N") + ".tmp");
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            try
            {
                using (var stream = new FileStream(temporary, options))
                {
                    stream.Write(System.Text.Encoding.UTF8.GetBytes(Current()));
                    stream.Flush(true);
                }
                // Publish only complete bytes. Never replace the canonical marker, including an invalid one.
                try { File.Move(temporary, path, overwrite: false); }
                catch (IOException) when (File.Exists(path)) { /* Another creator won; validate its marker below. */ }
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
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
