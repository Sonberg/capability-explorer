using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CapabilityExplorer.Binding;

public sealed record IntegrationMatchArtifact(string CapabilityId, string RunId, BindingPreviewSource Source);
public sealed record IntegrationMatchCatalogue(string AlgorithmVersion, string Fingerprint, DateTimeOffset CreatedAt,
    IReadOnlyList<IntegrationMatchArtifact> Inputs, int Observations, int Unresolved,
    IReadOnlyList<IntegrationMatch> Matches);

public sealed class IntegrationMatchCatalogueStore(string root)
{
    private const int MaxBytes = 128 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _directory = Path.Combine(Path.GetFullPath(root), ".sandbox-cache");
    private string FilePath => Path.Combine(_directory, "integration-matches.json");

    public IntegrationMatchCatalogue? Read()
    {
        RejectLinks(FilePath);
        if (!File.Exists(FilePath)) return null;
        using var stream = File.OpenRead(FilePath);
        if (stream.Length > MaxBytes) throw new InvalidDataException("Integration catalogue exceeds its byte budget.");
        var catalogue = JsonSerializer.Deserialize<IntegrationMatchCatalogue>(stream, Json)
            ?? throw new InvalidDataException("Invalid integration catalogue.");
        if (catalogue.AlgorithmVersion != IntegrationMatching.AlgorithmVersion || catalogue.Fingerprint.Length != 64
            || catalogue.Inputs is null || catalogue.Matches is null || catalogue.Observations < 0 || catalogue.Unresolved < 0)
            throw new InvalidDataException("Invalid integration catalogue identity.");
        return catalogue;
    }

    public IDisposable AcquireWriter()
    {
        RejectLinks(_directory);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(_directory);
        else Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(_directory, "integration-matches.lock");
        RejectLinks(path);
        return new FileStream(path, Options(FileMode.OpenOrCreate));
    }

    public void Write(IntegrationMatchCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        RejectLinks(FilePath);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, Options(FileMode.CreateNew)))
            {
                JsonSerializer.Serialize(stream, catalogue, Json);
                if (stream.Length > MaxBytes) throw new InvalidDataException("Integration catalogue exceeds its byte budget; no partial result published.");
                stream.Flush(true);
            }
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string Fingerprint(IEnumerable<IntegrationMatchInput> inputs, string bindingHash) =>
        Hash(IntegrationMatching.AlgorithmVersion + "\n" + bindingHash + "\n" + string.Join('\n', inputs
            .Select(input => string.Join('|', input.CapabilityId, input.RunId, input.From.RepositoryId,
                input.From.CommitSha, input.From.UnitPath, input.Artifact.ContentSha256)).Order(StringComparer.Ordinal)));

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static FileStreamOptions Options(FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }

    public static void RejectLinks(string path)
    {
        for (var part = Path.GetFullPath(path); part is not null; part = Path.GetDirectoryName(part))
            if (new FileInfo(part).LinkTarget is not null || new DirectoryInfo(part).LinkTarget is not null)
                throw new IOException("Integration paths must not contain symbolic links.");
    }
}