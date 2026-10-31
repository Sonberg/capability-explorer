namespace CapabilityExplorer.Explorer;

/// <summary>
/// File handshake with scripts/analyze-worker on the host: the explorer writes &lt;id&gt;.request, the worker renames it to
/// &lt;id&gt;.running, writes the output of scripts/analyze to &lt;id&gt;.log and its exit code to &lt;id&gt;.exit.
/// Stopping a running analysis writes &lt;id&gt;.cancel; the worker kills the run and writes "stopped" as the exit code.
/// </summary>
public sealed record AnalysisJob(string State, DateTime? Since, string? Log)
{
    private const int LogLines = 30;

    public static void Queue(string directory, string id)
    {
        Directory.CreateDirectory(directory);
        File.Delete(Path.Combine(directory, $"{id}.exit"));
        File.Delete(Path.Combine(directory, $"{id}.cancel"));
        File.WriteAllText(Path.Combine(directory, $"{id}.request"), "");
    }

    /// <summary>A queued request is withdrawn here; a running one is stopped by the worker. False when nothing runs.</summary>
    public static bool Stop(string directory, string id)
    {
        var request = Path.Combine(directory, $"{id}.request");
        if (File.Exists(request))
        {
            File.Delete(request);
            File.WriteAllText(Path.Combine(directory, $"{id}.exit"), "stopped\n");
            return true;
        }

        if (File.Exists(Path.Combine(directory, $"{id}.running")))
        {
            File.WriteAllText(Path.Combine(directory, $"{id}.cancel"), "");
            return true;
        }

        return false;
    }

    public static AnalysisJob Read(string directory, string id)
    {
        string F(string extension) => Path.Combine(directory, $"{id}.{extension}");
        string? Log() => File.Exists(F("log")) ? string.Join('\n', File.ReadLines(F("log")).TakeLast(LogLines)) : null;
        if (File.Exists(F("request")))
        {
            return new("queued", File.GetLastWriteTimeUtc(F("request")), null);
        }

        if (File.Exists(F("running")))
        {
            return new(File.Exists(F("cancel")) ? "stopping" : "running", File.GetLastWriteTimeUtc(F("running")), Log());
        }

        if (File.Exists(F("exit")))
        {
            var code = File.ReadAllText(F("exit")).Trim();
            return new(code == "0" ? "done" : code == "stopped" ? "stopped" : "failed", File.GetLastWriteTimeUtc(F("exit")), Log());
        }

        return new("idle", null, null);
    }
}
