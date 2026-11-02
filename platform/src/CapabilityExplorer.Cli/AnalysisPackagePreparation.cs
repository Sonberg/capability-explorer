using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CapabilityExplorer.Capabilities.Analysis;

namespace CapabilityExplorer.Cli;

/// <summary>Online package acquisition. Credentials exist only in host memory and the fetch child environment.</summary>
internal static class AnalysisPackagePreparation
{
    private const string CSharp = "platform/src/CapabilityExplorer.Extractors.CSharp/bin/Debug/net10.0/ce-cs.dll";
    private const string NpmHelper = "scripts/fetch-npm-packages.mjs";
    private const int MaximumSettingsBytes = 65536;
    private static readonly string[] ReservedNpmEnvironmentPrefixes = ["CE_", "SANDBOX_", "DOCKER_", "CONTAINER_", "NODE_", "npm_config_"];

    internal static async Task<Dictionary<string, string>> Run(string root, AnalysisRun run, AnalysisProcess process,
        IReadOnlyList<AnalysisRepository> repositories, string work, string locks, string packageDir)
    {
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var repo in repositories)
            foreach (var file in SourceFiles(repo.Source).Where(file => Path.GetFileName(file) is "package-lock.json" or "npm-shrinkwrap.json"))
                inputs["npm-lock:" + repo.Id + ":" + Relative(repo.Source, file)] = AnalysisWorkerCommand.HashFile(file);
        if (!run.Settings.Packages) return inputs;

        var path = Path.GetFullPath(Environment.GetEnvironmentVariable("CE_PACKAGE_FEEDS_FILE") ?? Path.Combine(root, ".sandbox-cache/package-feeds/settings.json"));
        AnalysisPreparation.RejectLinks(path);
        var settings = File.Exists(path) ? ReadBounded(path, MaximumSettingsBytes) : "{\"feeds\":[]}";
        using var configuration = JsonDocument.Parse(settings);
        var feeds = ValidateFeeds(configuration.RootElement);
        // Hash descriptors only: credential values and credential-derived hashes never enter run evidence.
        var descriptors = feeds.OrderBy(feed => feed.GetProperty("id").GetString(), StringComparer.Ordinal)
            .Select(feed => new { id = feed.GetProperty("id").GetString(), kind = feed.GetProperty("kind").GetString(),
                url = new Uri(feed.GetProperty("url").GetString()!).AbsoluteUri, scope = Optional(feed, "scope") });
        inputs["package-feed-policy"] = AnalysisWorkerCommand.HashText(JsonSerializer.Serialize(descriptors));
        string InContainer(string file) => "/work/" + Relative(root, file);
        var nuget = SerializeFeeds(feeds.Where(feed => feed.GetProperty("kind").GetString() == "nuget"));
        var packageEnvironment = new Dictionary<string, string>
        {
            ["CE_PACKAGE_FEEDS"] = nuget,
            ["SANDBOX_PASS_ENV"] = "CE_PACKAGE_FEEDS,CE_NUGET_ALLOWED_ORIGINS" + (run.Settings.NugetPassEnv.Length > 0 ? "," + run.Settings.NugetPassEnv : ""),
            ["CE_NUGET_ALLOWED_ORIGINS"] = run.Settings.NugetAllowedOrigins ?? "",
        };
        foreach (var name in run.Settings.NugetPassEnv.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (Environment.GetEnvironmentVariable(name) is { } credential) packageEnvironment[name] = credential;
        foreach (var repo in repositories)
        {
            var arguments = new List<string> { "dotnet", "dotnet", CSharp, "fetch-packages", "--source", InContainer(repo.Source),
                "--packages", InContainer(packageDir), "--lock", InContainer(Path.Combine(locks, repo.Id + ".json")) };
            if (run.Settings.IncludeTests) arguments.Add("--include-tests");
            // Fetch programs can include server-controlled errors. Suppress both streams from durable analysis logs.
            var output = await process.Run("scripts/sandbox", arguments, packageEnvironment, capture: true, captureLimit: 1048576);
            var coverage = Regex.Match(output, @"(?m)^packages: (\d+) downloaded, (\d+) already present, (\d+) unavailable;");
            if (!coverage.Success) throw new InvalidDataException("NuGet fetch did not report dependency coverage");
            inputs["nuget-coverage:" + repo.Id] = string.Join(':', coverage.Groups.Cast<Group>().Skip(1).Select(group => group.Value));
        }

        var npm = SerializeFeeds(feeds.Where(feed => feed.GetProperty("kind").GetString() == "npm"));
        var resolverEnvironment = HostNpmEnvironment(npm);
        var resolvedNpm = await process.Run("node", [NpmHelper, "--resolve-host-feeds"], resolverEnvironment, capture: true, captureLimit: 1048576);
        if (Encoding.UTF8.GetByteCount(resolvedNpm) > MaximumSettingsBytes) throw new InvalidDataException("resolved npm feed settings exceed the bounded environment transport");
        using (var resolved = JsonDocument.Parse(resolvedNpm)) ValidateFeeds(resolved.RootElement);
        var npmEnvironment = new Dictionary<string, string> { ["CE_PACKAGE_FEEDS"] = resolvedNpm, ["SANDBOX_PASS_ENV"] = "CE_PACKAGE_FEEDS" };
        var npmOutput = await process.Run("scripts/sandbox", ["node", "node", NpmHelper, InContainer(Path.Combine(work, "manifest.tsv"))],
            npmEnvironment, capture: true, captureLimit: 1048576);
        var npmCoverage = Regex.Match(npmOutput, @"(?m)^npm packages: (\d+) lockfile-backed project\(s\) installed, (\d+) unavailable;");
        if (!npmCoverage.Success) throw new InvalidDataException("npm fetch did not report dependency coverage");
        inputs["npm-coverage"] = string.Join(':', npmCoverage.Groups.Cast<Group>().Skip(1).Select(group => group.Value));
        process.CheckCancellation();
        return inputs;
    }

    private static Dictionary<string, string> HostNpmEnvironment(string feeds)
    {
        var environment = new Dictionary<string, string> { ["CE_PACKAGE_FEEDS"] = feeds };
        var userConfiguration = Path.GetFullPath(Environment.GetEnvironmentVariable("npm_config_userconfig") ?? Environment.GetEnvironmentVariable("NPM_CONFIG_USERCONFIG")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".npmrc"));
        AnalysisPreparation.RejectLinks(userConfiguration);
        environment["npm_config_userconfig"] = userConfiguration;
        if (!File.Exists(userConfiguration)) return environment;
        var npmrc = ReadBounded(userConfiguration, MaximumSettingsBytes);
        using var document = JsonDocument.Parse(feeds);
        var requested = document.RootElement.GetProperty("feeds").EnumerateArray().Select(feed => new Uri(feed.GetProperty("url").GetString()!))
            .Select(uri => "//" + uri.Authority + (uri.AbsolutePath.EndsWith('/') ? uri.AbsolutePath : uri.AbsolutePath + "/") + ":_authToken").ToHashSet(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in npmrc.Split('\n'))
        {
            var assignment = Regex.Match(line, @"^\s*(//[^\s=]+:_authToken)\s*=\s*(.*?)\s*$");
            if (!assignment.Success || !requested.Contains(assignment.Groups[1].Value)) continue;
            foreach (Match variable in Regex.Matches(assignment.Groups[2].Value, @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}"))
            {
                var name = variable.Groups[1].Value;
                if (name.Length > 100 || name is "HOME" or "PATH" or "TMPDIR" or "CI" ||
                    ReservedNpmEnvironmentPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("npm credential references must not alter runtime configuration");
                if (names.Add(name) && Environment.GetEnvironmentVariable(name) is { } value) environment[name] = value;
                if (names.Count > 50) throw new InvalidDataException("too many npm credential environment references");
            }
        }
        return environment;
    }

    private static List<JsonElement> ValidateFeeds(JsonElement settings)
    {
        if (settings.ValueKind != JsonValueKind.Object || !settings.TryGetProperty("feeds", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 32)
            throw new InvalidDataException("invalid operator package feed settings");
        var feeds = list.EnumerateArray().ToList();
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var feed in feeds)
        {
            var id = Optional(feed, "id");
            var kind = Optional(feed, "kind");
            if (id is null || !Regex.IsMatch(id, @"\A[a-z0-9][a-z0-9._-]{0,63}\z") || !identifiers.Add(id) || kind is not ("nuget" or "npm"))
                throw new InvalidDataException("invalid operator feed identity");
            if (!Uri.TryCreate(Optional(feed, "url"), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                throw new InvalidDataException("operator feeds require HTTPS URLs without credentials");
            var secret = Optional(feed, "secret");
            if (secret?.Length > 8192 || secret?.Any(character => character is '\r' or '\n' or '\0') == true)
                throw new InvalidDataException("invalid operator feed credential");
            if (kind == "nuget" && !string.IsNullOrEmpty(secret) && string.IsNullOrWhiteSpace(Optional(feed, "username")))
                throw new InvalidDataException("NuGet operator credentials require a username");
        }
        return feeds;
    }

    private static string? Optional(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null
        ? value.ValueKind == JsonValueKind.String ? value.GetString() : throw new InvalidDataException("invalid package feed value") : null;
    private static string SerializeFeeds(IEnumerable<JsonElement> feeds) => JsonSerializer.Serialize(new { feeds });
    private static string ReadBounded(string path, int maximum)
    {
        AnalysisPreparation.RejectLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximum) throw new InvalidDataException("package configuration exceeds its bounded transport");
        var bytes = new byte[maximum + 1];
        var count = stream.ReadAtLeast(bytes, maximum + 1, throwOnEndOfStream: false);
        if (count > maximum) throw new InvalidDataException("package configuration exceeds its bounded transport");
        return Encoding.UTF8.GetString(bytes, 0, count);
    }
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    internal static string SourceIdentity(string source) => AnalysisWorkerCommand.HashText(string.Join('\n', SourceFiles(source).Order(StringComparer.Ordinal)
        .Select(file => Relative(source, file) + ":" + AnalysisWorkerCommand.HashFile(file))));

    internal static IEnumerable<string> SourceFiles(string source)
    {
        var pending = new Stack<string>(); pending.Push(source);
        var count = 0;
        while (pending.TryPop(out var directory))
        {
            AnalysisPreparation.RejectLinks(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++count > 200000) throw new IOException("source entry limit exceeded");
                if (Path.GetFileName(entry) == "node_modules") continue;
                AnalysisPreparation.RejectLinks(entry);
                if (Directory.Exists(entry)) pending.Push(entry);
                else { if (new FileInfo(entry).Length > 64L * 1024 * 1024) throw new IOException("source file too large"); yield return entry; }
            }
        }
    }

    /// <summary>Hashes installed files and confined npm link identities without following link directories.</summary>
    internal static string DependencyIdentity(string source)
    {
        AnalysisPreparation.RejectLinks(source);
        var root = Path.GetFullPath(source);
        var pending = new Stack<(string Directory, bool Dependency)>(); pending.Push((root, false));
        var identities = new List<string>();
        var count = 0;
        while (pending.TryPop(out var item))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(item.Directory))
            {
                if (++count > 500000) throw new IOException("dependency entry limit exceeded");
                var dependency = item.Dependency || Path.GetFileName(entry) == "node_modules";
                var info = new FileInfo(entry);
                if (info.LinkTarget is not null)
                {
                    if (!dependency) { AnalysisPreparation.RejectLinks(entry); continue; }
                    var direct = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(entry)!, info.LinkTarget));
                    if (Path.IsPathRooted(info.LinkTarget) || !Within(root, direct)) throw new IOException("dependency link escapes the exported source");
                    var target = info.ResolveLinkTarget(returnFinalTarget: true) ?? throw new IOException("unresolved dependency link");
                    if (!Within(root, target.FullName)) throw new IOException("dependency link target escapes the exported source");
                    AnalysisPreparation.RejectLinks(target.FullName);
                    identities.Add("link:" + Relative(root, entry) + ":" + Relative(root, target.FullName));
                    continue;
                }
                AnalysisPreparation.RejectLinks(entry);
                if (Directory.Exists(entry)) pending.Push((entry, dependency));
                else if (dependency)
                {
                    if (info.Length > 64L * 1024 * 1024) throw new IOException("dependency file too large");
                    identities.Add("file:" + Relative(root, entry) + ":" + AnalysisWorkerCommand.HashFile(entry));
                }
            }
        }
        return AnalysisWorkerCommand.HashText(string.Join('\n', identities.Order(StringComparer.Ordinal)));
    }
    private static bool Within(string root, string path) => Path.GetRelativePath(root, path) is var relative &&
        relative != ".." && !relative.StartsWith("../", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
}
