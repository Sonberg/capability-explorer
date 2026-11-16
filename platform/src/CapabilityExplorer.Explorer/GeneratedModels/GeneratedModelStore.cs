using System.Text.Json;
using System.Text.Json.Serialization;
using CapabilityExplorer.Binding;

namespace CapabilityExplorer.Explorer.GeneratedModels;

internal sealed class GeneratedModelStore(string root)
{
    private const int MaximumBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly string _directory = Path.Combine(Path.GetFullPath(root), ".sandbox-cache");
    private string FilePath => Path.Combine(_directory, "generated-models.json");

    internal GeneratedModelStoreDocument Read()
    {
        IntegrationMatchCatalogueStore.RejectLinks(FilePath);
        if (!File.Exists(FilePath)) return new(null, null);
        using var stream = File.OpenRead(FilePath);
        if (stream.Length is < 1 or > MaximumBytes)
            throw new InvalidDataException("Generated model store exceeds its byte limit.");
        var document = JsonSerializer.Deserialize<GeneratedModelStoreDocument>(stream, Json)
            ?? throw new InvalidDataException("Generated model store is invalid.");
        if (document.LatestSuccessful is { } snapshot
            && (snapshot.Schema != GeneratedModelEngine.Schema
                || snapshot.Status != "complete"
                || snapshot.ContentHash != GeneratedModelEngine.ContentHash(snapshot)))
            throw new InvalidDataException("Generated model snapshot identity is invalid.");
        if (document.LatestRun is { Status: not ("running" or "complete" or "partial" or "failed" or "cancelled") })
            throw new InvalidDataException("Generated model run status is invalid.");
        if (document.LatestRun is { } run && !IsValidRun(run))
            throw new InvalidDataException("Generated model run identity is invalid.");
        return document;
    }

    internal void Write(GeneratedModelStoreDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.LatestSuccessful is { } snapshot
            && (snapshot.Schema != GeneratedModelEngine.Schema
                || snapshot.Status != "complete"
                || snapshot.ContentHash != GeneratedModelEngine.ContentHash(snapshot)))
            throw new InvalidDataException("Generated model snapshot identity is invalid.");
        if (document.LatestRun is { } run && !IsValidRun(run))
            throw new InvalidDataException("Generated model run identity is invalid.");

        IntegrationMatchCatalogueStore.RejectLinks(_directory);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(_directory);
        else Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, Options()))
            {
                JsonSerializer.Serialize(stream, document, Json);
                if (stream.Length > MaximumBytes)
                    throw new InvalidDataException("Generated model store exceeds its byte limit.");
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

    private static FileStreamOptions Options()
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }

    private static bool IsValidRun(GeneratedModelRunState run) =>
        Guid.TryParseExact(run.RunId, "D", out _)
        && run.Selection is { Count: > 0 and <= 100 }
        && run.Selection.All(pair => pair.Key.Length is > 0 and <= 100
            && pair.Value.Length == 40
            && pair.Value.All(char.IsAsciiHexDigit));
}
