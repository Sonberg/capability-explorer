using CapabilityExplorer.Contracts;
using CapabilityExplorer.Extractors.CSharp;

// ce-cs fetch-packages --source <dir> --packages <folder>   (network: downloads referenced packages as data)
// ce-cs extract --source <dir> --repository <id> --commit <sha> --out <dir> [--packages <nuget folder>] [--include-tests]
// Writes one ce.v1 artifact per .csproj. Every artifact is validated before it is written.
string? Arg(string name)
{
    var i = Array.IndexOf(args, $"--{name}");
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

bool Has(string name) => args.Contains($"--{name}", StringComparer.Ordinal);

if (args.FirstOrDefault() == "fetch-packages")
{
    // Network step, run before the offline extract: pure downloads from the configured feeds, no restore, no MSBuild.
    // Writes the lock file that the extract step uses. Feed credentials (%ENV_VAR% references in nuget.config) are
    // read from this process's environment only and are never printed.
    if (Arg("source") is not { } fetchSource || Arg("packages") is not { } into)
    {
        Console.Error.WriteLine("usage: ce-cs fetch-packages --source <dir> --packages <folder> [--lock <file>]");
        return 2;
    }

    var models = ProjectModel.Discover(Path.GetFullPath(fetchSource), Has("include-tests"));
    var feeds = NuGetConfig.Discover(Path.GetFullPath(fetchSource), models.Select(m => Path.GetDirectoryName(m.FullPath)!), Environment.GetEnvironmentVariable);
    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(2) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("capability-explorer/0.1");
    var fetched = await new PackageFetcher(Path.GetFullPath(into), http, feeds).FetchAsync(models.Select(m => (m.RelativePath, m.PackageRoots())));
    Console.WriteLine($"packages: {fetched.Fetched} downloaded, {fetched.Present} already present, {fetched.Failed.Count} unavailable; feeds: {string.Join(", ", feeds.Select(f => f.Name + (f.Credentials is null ? "" : " (authenticated)")))}");
    foreach (var f in fetched.Failed)
    {
        Console.WriteLine($"  - {f}");
    }

    if (Arg("lock") is { } lockFile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(lockFile))!);
        File.WriteAllText(lockFile, fetched.Lock.ToJson());
    }

    return 0;
}

if (args.FirstOrDefault() != "extract" || Arg("source") is not { } source || Arg("repository") is not { } repo || Arg("commit") is not { } commit || Arg("out") is not { } outDir)
{
    Console.Error.WriteLine("usage: ce-cs extract --source <dir> --repository <id> --commit <sha> --out <dir> [--packages <nuget folder>] [--lock <file>] [--packs <dotnet packs dir>] [--include-tests]");
    return 2;
}

var packs = Arg("packs") ?? Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "packs"));
var packages = Arg("packages") is { } p && Directory.Exists(p) ? Path.GetFullPath(p) : null;
var extractor = new CSharpExtractor(new ExtractorOptions(Path.GetFullPath(source), repo, commit, packages, packs, Arg("lock") is { } lockPath ? PackageLock.Load(lockPath) : null, Has("include-tests")));
var results = extractor.Extract();
Directory.CreateDirectory(outDir);
var failed = 0;
foreach (var r in results)
{
    var report = ArtifactValidator.Validate(r.Jsonl);
    var file = Path.Combine(outDir, $"{string.Concat(r.UnitName.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'))}.jsonl");
    if (!report.IsValid)
    {
        failed++;
        Console.Error.WriteLine($"INVALID {r.UnitPath}: {string.Join("; ", report.Problems.Take(5))}");
        File.WriteAllText(file + ".invalid", r.Jsonl);
        continue;
    }

    File.WriteAllText(file, r.Jsonl);
    var a = report.Artifact!;
    Console.WriteLine($"{a.Complete.Status,-7} {r.UnitPath}: {a.Nodes.Count} nodes, {a.Edges.Count} edges, {a.Diagnostics.Count} diagnostics");
}

if (results.Count == 0)
{
    Console.WriteLine("no C# projects found");
}

return failed == 0 ? 0 : 1;
