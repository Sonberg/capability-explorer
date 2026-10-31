using CapabilityExplorer.Binding;
using CapabilityExplorer.Capabilities;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Evaluation;
using CapabilityExplorer.Interpretation;
using CapabilityExplorer.Store;
using Npgsql;

// ce validate <artifact.jsonl...>
// ce verify-evidence --repo <git dir> <artifact.jsonl...>
// ce migrate [--connection <npgsql connection string>]
// ce import  [--connection <npgsql connection string>] [--bindings <bindings.json>] <artifact.jsonl...>
// ce embed  [--connection <npgsql connection string>] [--foundry <settings.json>]
// ce evaluate --expected <dir> --actual <dir> [--config <evaluation.json>] [--repos <dir>] [--repository <id[,id]>] [--language <csharp,typescript,...>] [--json <report.json>]
// ce export --capability <id> [--capabilities capabilities.json] [--workspace .sandbox-cache/sources] --out <dir>
// The connection string defaults to the CE_DATABASE environment variable.
const string Usage = """
    usage:
      ce validate <artifact.jsonl...>
      ce verify-evidence --repo <git dir> <artifact.jsonl...>
      ce migrate [--connection <connection string>]
      ce import [--connection <connection string>] [--bindings <bindings.json>] <artifact.jsonl...>  (--bindings also saves the bound edges)
      ce embed [--connection <connection string>] [--foundry <settings.json>]  (Foundry from CE_FOUNDRY_ENDPOINT + CE_FOUNDRY_EMBEDDING_DEPLOYMENT, else the settings saved in the explorer)
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
        // Parse the bindings first: a bad file must fail before anything is written.
        BindingFile? bindings;
        try
        {
            bindings = options.TryGetValue("bindings", out var bindingsPath) ? BindingFile.Load(bindingsPath) : null;
        }
        catch (Exception e) when (e is FormatException or IOException)
        {
            Console.Error.WriteLine($"bindings: {e.Message}");
            return 2;
        }

        await using var db = OpenDatabase();
        var importer = new ArtifactImporter(db);
        var failed = 0;
        var stored = new List<(long ArtifactId, Artifact Artifact)>();
        foreach (var file in files)
        {
            try
            {
                var text = await File.ReadAllTextAsync(file);
                var result = await importer.ImportAsync(text);
                Console.WriteLine($"{(result.Outcome == ImportOutcome.Imported ? "imported " : "unchanged")} {file} (artifact {result.ArtifactId})");
                stored.Add((result.ArtifactId, ArtifactValidator.Validate(text).Artifact!));
            }
            catch (Exception e) when (e is InvalidArtifactException or ArtifactConflictException)
            {
                failed++;
                Console.WriteLine($"FAIL      {file}: {e.Message}");
            }
        }

        if (bindings is not null && stored.Count > 0)
        {
            var saved = await new BindingStore(db).SaveAsync(stored, bindings);
            Console.WriteLine($"bindings  {(saved.Created ? "saved" : "unchanged")} snapshot {saved.SnapshotId[..12]}: {saved.BoundEdges} bound, {saved.BrokenEdges} broken");
            foreach (var unused in saved.UnusedBindings)
            {
                Console.WriteLine($"          unused binding {unused.Hint} (no analysed edge uses it)");
            }
        }

        return failed == 0 ? 0 : 1;
    }

    case "embed":
    {
        // Environment first (CI, scripts), then what was saved from the explorer's Settings page.
        var foundry = Environment.GetEnvironmentVariable("CE_FOUNDRY_ENDPOINT") is { Length: > 0 } endpoint
            ? new InterpretationOptions { Provider = "foundry", Endpoint = endpoint, EmbeddingDeployment = Environment.GetEnvironmentVariable("CE_FOUNDRY_EMBEDDING_DEPLOYMENT") }
            : FoundrySettings.Load(options.GetValueOrDefault("foundry") ?? FoundrySettings.DefaultPath)?.ApplyTo(new InterpretationOptions());
        if (foundry?.CreateEmbeddingModel() is not { } model)
        {
            Console.Error.WriteLine("no Foundry embeddings deployment: save one in the explorer (Settings) or set CE_FOUNDRY_ENDPOINT and CE_FOUNDRY_EMBEDDING_DEPLOYMENT");
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
