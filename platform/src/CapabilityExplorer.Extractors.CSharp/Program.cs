using CapabilityExplorer.Contracts;
using CapabilityExplorer.Extractors.CSharp;

// ce-cs extract --source <dir> --repository <id> --commit <sha> --out <dir> [--packages <nuget folder>]
// Writes one ce.v1 artifact per .csproj. Every artifact is validated before it is written.
string? Arg(string name)
{
    var i = Array.IndexOf(args, $"--{name}");
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

if (args.FirstOrDefault() != "extract" || Arg("source") is not { } source || Arg("repository") is not { } repo || Arg("commit") is not { } commit || Arg("out") is not { } outDir)
{
    Console.Error.WriteLine("usage: ce-cs extract --source <dir> --repository <id> --commit <sha> --out <dir> [--packages <nuget folder>] [--packs <dotnet packs dir>]");
    return 2;
}

var packs = Arg("packs") ?? Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "packs"));
var packages = Arg("packages") is { } p && Directory.Exists(p) ? Path.GetFullPath(p) : null;
var extractor = new CSharpExtractor(new ExtractorOptions(Path.GetFullPath(source), repo, commit, packages, packs));
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
