using System.Text.RegularExpressions;

namespace CapabilityExplorer.Capabilities.Analysis;

public sealed partial class FileRunStore
{
    private string WorkerDefaultsPath()
    {
        CoordinationDomain.Validate(_root);
        var path = Path.Combine(_root, "worker-defaults.json");
        AnalysisPaths.NoLinks(path);
        return path;
    }

    /// <summary>Publishes policy defaults only. No environment values, credentials or liveness claims are read.</summary>
    public void PublishWorkerDefaults(WorkerOwnership owner, AnalysisSettings settings, bool? includeTestsOverride = null)
    {
        Owner(owner);
        var defaults = new WorkerDefaults(settings, includeTestsOverride);
        ValidateWorkerDefaults(defaults);
        using var gate = Gate();
        AnalysisPaths.Save(WorkerDefaultsPath(), defaults);
    }

    /// <summary>Returns the last configured policy. Presence does not establish that a worker is running.</summary>
    public WorkerDefaults? ReadWorkerDefaults()
    {
        var path = WorkerDefaultsPath();
        if (!File.Exists(path)) return null;
        var defaults = AnalysisPaths.Read<WorkerDefaults>(path);
        ValidateWorkerDefaults(defaults);
        return defaults;
    }

    private static void ValidateWorkerDefaults(WorkerDefaults defaults)
    {
        var settings = defaults.Settings ?? throw new InvalidDataException("worker defaults settings are missing");
        if (settings.ExtractionJobs is < 1 or > 4 || settings.CapabilityWorkers is < 1 or > 4 ||
            settings.ExtractionJobs * settings.CapabilityWorkers > 16)
            throw new InvalidDataException("worker and extraction limits exceed the bounded analysis resource ceiling");
        if (!GitHubAuth.IsValidMode(settings.GitHubAuth)) throw new InvalidDataException("invalid worker authentication policy");
        if (settings.RuntimeIdentity is not null && (settings.RuntimeIdentity.Length != 64 || !settings.RuntimeIdentity.All(char.IsAsciiHexDigit)))
            throw new InvalidDataException("worker runtime identity must be a hash or unresolved");
        if (settings.NugetPassEnv is null || settings.NugetPassEnv.Length > 5050) throw new InvalidDataException("invalid worker credential environment names");
        var names = settings.NugetPassEnv.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (names.Length > 50 || names.Any(name => !Regex.IsMatch(name, @"\A[A-Z_][A-Z0-9_]{0,99}\z", RegexOptions.CultureInvariant)))
            throw new InvalidDataException("worker defaults accept credential names, never environment assignments or values");
        if (settings.NugetAllowedOrigins?.Length > 16384) throw new InvalidDataException("worker package origins exceed bounds");
        foreach (var origin in (settings.NugetAllowedOrigins ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 ||
                uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new InvalidDataException("worker package origins must be HTTPS origins without credentials, paths, query or fragment");
        }
    }
}
