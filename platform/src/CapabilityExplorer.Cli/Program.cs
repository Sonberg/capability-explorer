using CapabilityExplorer.Binding;
using CapabilityExplorer.Capabilities;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Evaluation;
using CapabilityExplorer.Interpretation;
using CapabilityExplorer.Store;
using Npgsql;
using CapabilityExplorer.Cli;

// ce validate <artifact.jsonl...>
// ce verify-evidence --repo <git dir> <artifact.jsonl...>
// ce migrate [--connection <npgsql connection string>]
// ce import  [--connection <npgsql connection string>] [--bindings <bindings.json>] <artifact.jsonl...>
// ce embed  [--connection <npgsql connection string>] [--settings <model.json>]
// ce evaluate --expected <dir> --actual <dir> [--config <evaluation.json>] [--repos <dir>] [--repository <id[,id]>] [--language <csharp,typescript,...>] [--json <report.json>]
// ce export --capability <id> [--capabilities capabilities.json] [--workspace .sandbox-cache/sources] --out <dir>
// The connection string defaults to the CE_DATABASE environment variable.
const string Usage = """
    usage:
      ce analysis-worker [--capabilities <file>]
      ce analysis-run --capability <id> [--capabilities <file>]
      ce analysis-all [--capabilities <file>]
      ce validate <artifact.jsonl...>
      ce verify-evidence --repo <git dir> <artifact.jsonl...>
      ce migrate [--connection <connection string>]
      ce import [--connection <connection string>] [--bindings <bindings.json>] <artifact.jsonl...>  (--bindings also saves the bound edges)
      ce publish [--scope <capability-id|*>] [--view <repo:full-sha,...>] [--bindings <bindings.json>] [--expected-generation <n>] <artifact.jsonl...>
      ce embed [--connection <connection string>] [--settings <model.json>]  (model from CE_MODEL_ENDPOINT + CE_EMBEDDING_MODEL, else the saved settings: make ollama / explorer Settings)
      ce export --capability <id> --out <dir> [--capabilities <file>] [--workspace <dir>] [--locked 1]  (1 = use the last check instead of re-checking)
      ce evaluate --expected <dir> --actual <dir> [--config <evaluation.json>] [--repos <dir>] [--repository <id[,id]>] [--language <csharp,typescript,...>] [--json <report.json>] [--max-findings <n>]
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
    case "analysis-worker":
    case "analysis-run":
    case "analysis-all":
        return await AnalysisWorkerCommand.RunAsync(command, options);

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
    case "publish":
    {
        await using var db = OpenDatabase();
        return await ImportCommand.RunAsync(db, files, options, command == "publish");
    }

    case "embed":
    {
        // Environment first (CI, scripts), then what `make ollama` or the explorer's Settings page saved.
        var endpoint = Environment.GetEnvironmentVariable("CE_MODEL_ENDPOINT");
        var settings = endpoint is { Length: > 0 }
            ? new ModelSettings(endpoint, null, Environment.GetEnvironmentVariable("CE_EMBEDDING_MODEL"), null)
            : ModelSettings.Load(options.GetValueOrDefault("settings") ?? ModelSettings.DefaultPath);
        if (settings?.ApplyTo(new InterpretationOptions()).CreateEmbeddingModel() is not { } model)
        {
            Console.Error.WriteLine("no embedding model: run `make ollama`, save one in the explorer (Settings), or set CE_MODEL_ENDPOINT and CE_EMBEDDING_MODEL");
            return 2;
        }

        await using var db = OpenDatabase();
        await Migrator.MigrateAsync(db);
        try
        {
            var count = await new EmbeddingStore(db).EmbedPendingAsync(model.Id, (texts, ct) => model.EmbedAsync(texts, ct));
            Console.WriteLine(count == 0 ? $"embeddings up to date ({model.Id})" : $"embedded {count} symbol(s) with {model.Id}");
            return 0;
        }
        catch (InterpretationUnavailableException e)
        {
            Console.Error.WriteLine($"embed: {e.Message}");
            return 1;
        }
    }

    case "export":
    {
        // Locks the capability's revisions (unless --locked) and writes each locked tree to <out>/src/<repo>,
        // plus <out>/manifest.tsv (repo, commit, path) for the offline extractor runs that follow.
        if (!options.TryGetValue("capability", out var capabilityId) || !options.TryGetValue("out", out var exportDir))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var workspace = options.GetValueOrDefault("workspace") ?? ".sandbox-cache/sources";
        using var store = CapabilityStore.Open(options.GetValueOrDefault("capabilities") ?? "capabilities.json", Path.Combine(workspace, "snapshots.json"));
        if (store.Find(capabilityId) is not { } capability)
        {
            Console.Error.WriteLine($"no capability '{capabilityId}'. Known: {string.Join(", ", store.Capabilities.Select(c => c.Id))}");
            return 2;
        }

        var sync = new SourceSync(workspace, auth: GitHubAuth.FromEnvironment());
        var snapshot = options.GetValueOrDefault("locked") is "1" or "true" && store.Snapshot(capability.Id) is { } existing ? existing : await sync.CheckAsync(capability);
        await store.SaveSnapshotAsync(snapshot);
        var src = Path.Combine(exportDir, "src");
        if (Directory.Exists(src))
        {
            Directory.Delete(src, recursive: true);
        }

        var manifest = new List<string>();
        foreach (var locked in snapshot.Repositories)
        {
            if (locked.CommitSha is null)
            {
                Console.WriteLine($"{locked.Id,-24} skipped: {locked.Error}");
                continue;
            }

            var source = capability.Repositories.Single(r => r.Id == locked.Id);
            var dest = Path.Combine(src, locked.Id);
            try
            {
                var result = await sync.ExportAsync(source, locked.CommitSha, dest);
                manifest.Add($"{locked.Id}\t{locked.CommitSha}\t{Path.GetRelativePath(exportDir, dest).Replace('\\', '/')}");
                Console.WriteLine($"{locked.Id,-24} {locked.CommitSha[..12]} {result.Files} files{(result.Skipped > 0 ? $" ({result.Skipped} skipped: links/large)" : "")}  {locked.Subject}");
            }
            catch (Exception e) when (e is InvalidOperationException or IOException)
            {
                Console.WriteLine($"{locked.Id,-24} {locked.CommitSha[..12]} export failed: {e.Message}");
            }
        }

        Directory.CreateDirectory(exportDir);
        await File.WriteAllTextAsync(Path.Combine(exportDir, "manifest.tsv"), string.Concat(manifest.Select(m => m + "\n")));
        await File.WriteAllTextAsync(Path.Combine(exportDir, "include-tests"), capability.IncludeTests ? "1\n" : "0\n");
        return manifest.Count > 0 ? 0 : 1;
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
            options.TryGetValue("repository", out var only) ? only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null,
            options.TryGetValue("language", out var languages) ? languages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null);
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
