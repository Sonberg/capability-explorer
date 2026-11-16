using System.Text.Json;
using CapabilityExplorer.Binding;

namespace CapabilityExplorer.Explorer.LandscapeAnalysis;

internal sealed class LandscapeAnalysisStore(string root)
{
    private const int MaxBytes = 128 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _directory = Path.Combine(Path.GetFullPath(root), ".sandbox-cache");
    private string FilePath => Path.Combine(_directory, "landscape-analysis.json");

    internal LandscapeAnalysisSnapshot? Read()
    {
        IntegrationMatchCatalogueStore.RejectLinks(FilePath);
        if (!File.Exists(FilePath)) return null;
        using var stream = File.OpenRead(FilePath);
        if (stream.Length > MaxBytes) throw new InvalidDataException("Landscape snapshot exceeds its byte budget.");
        var snapshot = JsonSerializer.Deserialize<LandscapeAnalysisSnapshot>(stream, Json)
            ?? throw new InvalidDataException("Invalid landscape snapshot.");
        if (!LandscapeAnalysisEngine.IsValid(snapshot)) throw new InvalidDataException("Invalid landscape snapshot identity.");
        return snapshot;
    }

    internal void Write(LandscapeAnalysisSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!LandscapeAnalysisEngine.IsValid(snapshot)) throw new InvalidDataException("Invalid landscape snapshot identity.");
        IntegrationMatchCatalogueStore.RejectLinks(_directory);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(_directory);
        else Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, Options(FileMode.CreateNew)))
            {
                JsonSerializer.Serialize(stream, snapshot, Json);
                if (stream.Length > MaxBytes)
                    throw new InvalidDataException("Landscape snapshot exceeds its byte budget; no partial result was published.");
                stream.Flush(true);
            }
            IntegrationMatchCatalogueStore.RejectLinks(FilePath);
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static FileStreamOptions Options(FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }
}
