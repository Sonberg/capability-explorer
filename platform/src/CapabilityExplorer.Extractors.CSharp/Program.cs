using System.Text.Json;
using System.Text;
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

bool SelectedCommand(string verb) => args.FirstOrDefault() == verb;

ProjectSelectionContract SelectedContract(string verb)
{
    var allowed = verb switch
    {
        "validate-selected-v1" => new HashSet<string>(["source", "selection-sha256", "source-sha256", "project", "include-tests"], StringComparer.Ordinal),
        "fetch-packages-selected-v1" => new HashSet<string>(["source", "packages", "lock", "selection-sha256", "source-sha256", "project", "include-tests"], StringComparer.Ordinal),
        "extract-selected-v1" => new HashSet<string>(["source", "repository", "commit", "out", "packages", "lock", "packs", "selection-sha256", "source-sha256", "project", "include-tests"], StringComparer.Ordinal),
        _ => throw new ArgumentException("unsupported selected C# command"),
    };
    var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
    var argumentBytes = 0;
    for (var index = 0; index < args.Length; index++)
    {
        argumentBytes += Encoding.UTF8.GetByteCount(args[index]) + 1;
        if (index == 0) continue;
        if (!args[index].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("selected C# commands reject positional arguments");
        var key = args[index][2..];
        if (!allowed.Contains(key)) throw new ArgumentException("selected C# command has an unsupported option");
        if (!values.TryGetValue(key, out var entries)) values[key] = entries = [];
        if (key == "include-tests")
        {
            if (entries.Count > 0) throw new ArgumentException("selected C# command has a duplicate option");
            entries.Add("1");
            continue;
        }
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("selected C# command has a missing option value");
        var value = args[++index];
        argumentBytes += Encoding.UTF8.GetByteCount(value) + 1;
        if (key != "project" && entries.Count > 0)
            throw new ArgumentException("selected C# command has a duplicate option");
        entries.Add(value);
    }

    if (argumentBytes > 65_536) throw new ArgumentException("selected C# argv exceeds 64 KiB");
    string Required(string key) => values.TryGetValue(key, out var entries) && entries.Count == 1 && entries[0].Length > 0
        ? entries[0] : throw new ArgumentException("selected C# command is missing a required option");
    if (!values.TryGetValue("project", out var projects) || projects.Count is < 1 or > 100)
        throw new ArgumentException("selected C# command requires 1 to 100 project paths");
    return new ProjectSelectionContract(projects.ToArray(), Required("selection-sha256"), Required("source-sha256"));
}

bool IncludeSelectedTests() => args.Any(argument => argument == "--include-tests");

if (args.FirstOrDefault() == "extract-candidates")
{
    if (args.Length != 9 || Arg("source") is not { } candidateSource ||
        Arg("repository") is not { } candidateRepo || Arg("commit") is not { } candidateCommit ||
        Arg("out") is not { } candidateOutput)
    {
        Console.Error.WriteLine("usage: ce-cs extract-candidates --source <file.cs> --repository <id> --commit <40sha> --out <file>");
        return 2;
    }
    try
    {
        CandidatePaths.Write(candidateSource, candidateOutput, CandidateExtractor.Extract(candidateSource, candidateRepo, candidateCommit).ToJson());
        return 0;
    }
    catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine("candidate input or output rejected");
        return 2;
    }
}

if (SelectedCommand("validate-selected-v1"))
{
    var contract = SelectedContract("validate-selected-v1");
    var selectedSource = Arg("source") ?? throw new ArgumentException("selected validation source is required");
    var report = ProjectModel.ValidateSelection(Path.GetFullPath(selectedSource), contract, IncludeSelectedTests());
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        schema = "ce.csharp.selection-validation.v1",
        candidateCount = report.CandidateCount,
        testExcludedCount = report.TestExcludedCount,
        selectedCount = report.SelectedCount,
        selectionSha256 = report.SelectionSha256,
        sourceSha256 = report.SourceSha256,
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    return 0;
}

if (args.FirstOrDefault() is "fetch-packages" or "fetch-packages-selected-v1")
{
    var selected = SelectedCommand("fetch-packages-selected-v1");
    var selection = selected ? SelectedContract("fetch-packages-selected-v1") : null;
    // Network step, run before the offline extract: pure downloads from the configured feeds, no restore, no MSBuild.
    // Writes the lock file that the extract step uses. Feed credentials (%ENV_VAR% references in nuget.config) are
    // read from this process's environment only and are never printed.
    if (Arg("source") is not { } fetchSource || Arg("packages") is not { } into)
    {
        Console.Error.WriteLine("usage: ce-cs fetch-packages --source <dir> --packages <folder> [--lock <file>]");
        return 2;
    }

    var models = ProjectModel.Discover(Path.GetFullPath(fetchSource), Has("include-tests") || selected && IncludeSelectedTests(), selection);
    if (selected && models.Count == 0)
    {
        Console.WriteLine("no C# projects selected by test policy");
        return 0;
    }
    var operatorFeeds = OperatorPackageFeedSettings.Load(Environment.GetEnvironmentVariable("CE_PACKAGE_FEEDS"));
    var operatorNuGetFeeds = operatorFeeds.NuGetFeeds();
    var allowedOrigins = string.Join(',', new[] { Environment.GetEnvironmentVariable("CE_NUGET_ALLOWED_ORIGINS") }
        .Concat(operatorNuGetFeeds.Select(feed => new Uri(feed.Url).GetLeftPart(UriPartial.Authority))));
    var policy = new PackageFeedPolicy(allowedOrigins);
    var discovered = NuGetConfig.Discover(Path.GetFullPath(fetchSource), models.Select(m => Path.GetDirectoryName(m.FullPath)!), Environment.GetEnvironmentVariable, policy);
    var operatorUrls = operatorNuGetFeeds.Select(feed => feed.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var feeds = operatorNuGetFeeds.Concat(discovered.Where(feed => !operatorUrls.Contains(feed.Url))).ToList();
    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(2) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("capability-explorer/0.1");
    var fetched = await new PackageFetcher(Path.GetFullPath(into), http, feeds, policy).FetchAsync(models);
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

var selectedExtraction = SelectedCommand("extract-selected-v1");
var extractCommand = args.FirstOrDefault() is "extract" or "extract-selected-v1";
var selectedContract = selectedExtraction ? SelectedContract("extract-selected-v1") : null;
if (!extractCommand || Arg("source") is not { } source || Arg("repository") is not { } repo || Arg("commit") is not { } commit || Arg("out") is not { } outDir)
{
    Console.Error.WriteLine("usage: ce-cs extract --source <dir> --repository <id> --commit <sha> --out <dir> [--packages <nuget folder>] [--lock <file>] [--packs <dotnet packs dir>] [--include-tests]");
    return 2;
}

var packs = Arg("packs") ?? Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "packs"));
var packages = Arg("packages") is { } p && Directory.Exists(p) ? Path.GetFullPath(p) : null;
var options = new ExtractorOptions(Path.GetFullPath(source), repo, commit, packages, packs,
    Arg("lock") is { } lockPath ? PackageLock.Load(lockPath) : null, Has("include-tests") || selectedExtraction && IncludeSelectedTests())
{
    Selection = selectedContract,
    Timing = timing => Console.WriteLine("-- cs-timing: " + JsonSerializer.Serialize(new
    {
        schema = "ce.csharp.timing.v1", stage = timing.Stage, unit = timing.Unit,
        elapsedMilliseconds = timing.ElapsedMilliseconds, outcome = timing.Outcome
    }))
};
var extractor = new CSharpExtractor(options);
var results = extractor.Extract();
if (selectedExtraction)
{
    var report = ProjectModel.ValidateSelection(Path.GetFullPath(source), selectedContract!, options.IncludeTests);
    if (results.Count != report.SelectedCount)
        throw new InvalidDataException("selected C# result count differs from the validated selection");
    var filenames = results.Select(result => OutputName(result.UnitName)).ToArray();
    if (filenames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != filenames.Length)
        throw new InvalidDataException("selected C# outputs have colliding names");
    var outputRoot = Path.GetFullPath(outDir);
    _ = CandidatePaths.Validate(outputRoot);
    foreach (var filename in filenames)
    {
        var destination = CandidatePaths.Validate(Path.Combine(outputRoot, filename + ".jsonl"));
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new InvalidDataException("selected C# output would overwrite an existing artifact");
    }
}
Directory.CreateDirectory(outDir);
var failed = 0;
for (var index = 0; index < results.Count; index++)
{
    var r = results[index];
    using var validation = options.Measure("cs.validate",index);
    var report = ArtifactValidator.Validate(r.Jsonl);
    if (report.IsValid) validation.Complete();
    validation.Dispose();
    var file = Path.Combine(outDir, OutputName(r.UnitName) + ".jsonl");
    if (!report.IsValid)
    {
        failed++;
        Console.Error.WriteLine($"INVALID {r.UnitPath}: {string.Join("; ", report.Problems.Take(5))}");
        File.WriteAllText(file + ".invalid", r.Jsonl);
        continue;
    }

    using (var writing = options.Measure("cs.write",index))
    {
        File.WriteAllText(file, r.Jsonl);
        writing.Complete();
    }
    var a = report.Artifact!;
    Console.WriteLine($"{a.Complete.Status,-7} {r.UnitPath}: {a.Nodes.Count} nodes, {a.Edges.Count} edges, {a.Diagnostics.Count} diagnostics");
}

if (results.Count == 0)
{
    Console.WriteLine("no C# projects found");
}

return failed == 0 ? 0 : 1;

string OutputName(string unitName) =>
    string.Concat(unitName.Select(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_'));
