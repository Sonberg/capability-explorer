using CapabilityExplorer.Contracts;
using CapabilityExplorer.Evaluation;
using CapabilityExplorer.Store;
using Npgsql;

// ce validate <artifact.jsonl...>
// ce verify-evidence --repo <git dir> <artifact.jsonl...>
// ce migrate [--connection <npgsql connection string>]
// ce import  [--connection <npgsql connection string>] <artifact.jsonl...>
// ce evaluate --expected <dir> --actual <dir> [--config <evaluation.json>] [--repos <dir>] [--repository <id[,id]>] [--json <report.json>]
// The connection string defaults to the CE_DATABASE environment variable.
const string Usage = """
    usage:
      ce validate <artifact.jsonl...>
      ce verify-evidence --repo <git dir> <artifact.jsonl...>
      ce migrate [--connection <connection string>]
      ce import [--connection <connection string>] <artifact.jsonl...>
      ce evaluate --expected <dir> --actual <dir> [--config <evaluation.json>] [--repos <dir>] [--repository <id[,id]>] [--json <report.json>] [--max-findings <n>]
    """;

if (args.Length == 0)
{
    Console.Error.WriteLine(Usage);
    return 2;
}

var command = args[0];
var options = new Dictionary<string, string>(StringComparer.Ordinal);
var files = new List<string>();
for (var i = 1; i < args.Length; i++)
{
    if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
    {
        options[args[i][2..]] = args[++i];
    }
    else
    {
        files.Add(args[i]);
    }
}

NpgsqlDataSource OpenDatabase()
{
    var cs = options.GetValueOrDefault("connection") ?? Environment.GetEnvironmentVariable("CE_DATABASE")
        ?? throw new InvalidOperationException("no connection string: pass --connection or set CE_DATABASE");
    return NpgsqlDataSource.Create(cs);
}

switch (command)
{
    case "validate":
    {
        var failed = 0;
        foreach (var file in files)
        {
            var report = ArtifactValidator.Validate(await File.ReadAllTextAsync(file));
            failed += Print(file, report.Problems) ? 0 : 1;
        }

        return failed == 0 ? 0 : 1;
    }

    case "verify-evidence":
    {
        if (!options.TryGetValue("repo", out var repo))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var verifier = new EvidenceVerifier(repo);
        var failed = 0;
        foreach (var file in files)
        {
            var report = ArtifactValidator.Validate(await File.ReadAllTextAsync(file));
            var problems = report.IsValid ? await verifier.VerifyAsync(report.Artifact!) : report.Problems;
            failed += Print(file, problems) ? 0 : 1;
        }

        return failed == 0 ? 0 : 1;
    }

    case "migrate":
    {
        await using var db = OpenDatabase();
        var applied = await Migrator.MigrateAsync(db);
        Console.WriteLine(applied.Count == 0 ? "database is up to date" : $"applied: {string.Join(", ", applied)}");
        return 0;
    }

    case "import":
    {
        await using var db = OpenDatabase();
        var importer = new ArtifactImporter(db);
        var failed = 0;
        foreach (var file in files)
        {
            try
            {
                var result = await importer.ImportAsync(await File.ReadAllTextAsync(file));
                Console.WriteLine($"{(result.Outcome == ImportOutcome.Imported ? "imported " : "unchanged")} {file} (artifact {result.ArtifactId})");
            }
            catch (Exception e) when (e is InvalidArtifactException or ArtifactConflictException)
            {
                failed++;
                Console.WriteLine($"FAIL      {file}: {e.Message}");
            }
        }

        return failed == 0 ? 0 : 1;
    }

    case "evaluate":
    {
        if (!options.TryGetValue("expected", out var expected) || !options.TryGetValue("actual", out var actual))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var config = options.TryGetValue("config", out var configPath)
            ? EvaluationConfig.Parse(await File.ReadAllTextAsync(configPath))
            : EvaluationConfig.Default;
        var report = await Evaluator.EvaluateAsync(
            ArtifactFile.LoadDirectory(expected),
            ArtifactFile.LoadDirectory(actual),
            config,
            options.GetValueOrDefault("repos"),
            options.TryGetValue("repository", out var only) ? only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null);
        ReportWriter.WriteText(report, Console.Out, options.TryGetValue("max-findings", out var max) ? int.Parse(max, System.Globalization.CultureInfo.InvariantCulture) : 40);
        if (options.TryGetValue("json", out var jsonPath))
        {
            await File.WriteAllTextAsync(jsonPath, ReportWriter.ToJson(report));
        }

        return report.Passed ? 0 : 1;
    }

    default:
        Console.Error.WriteLine(Usage);
        return 2;
}

static bool Print(string file, IReadOnlyList<Problem> problems)
{
    if (problems.Count == 0)
    {
        Console.WriteLine($"ok   {file}");
        return true;
    }

    Console.WriteLine($"FAIL {file}");
    foreach (var p in problems)
    {
        Console.WriteLine($"     {p}");
    }

    return false;
}
