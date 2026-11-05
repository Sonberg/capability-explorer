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
// ce reconcile-publication --run <id> --attempt <id> [--connection <npgsql connection string>]
// ce embed  [--connection <npgsql connection string>] [--settings <model.json>]
// ce evaluate --expected <dir> --actual <dir> [--config <evaluation.json>] [--repos <dir>] [--repository <id[,id]>] [--language <csharp,typescript,...>] [--json <report.json>]
// ce export --capability <id> [--capabilities capabilities.json] [--workspace .sandbox-cache/sources] --out <dir>
// The connection string defaults to the CE_DATABASE environment variable.
const string Usage = """
    usage:
      ce analysis-worker [--capabilities <file>]
      ce analysis-run --capability <id> [--capabilities <file>]
      ce analysis-all [--capabilities <file>]
      ce collection-freshness --previous <input-vector.json> --current <input-vector.json> [--as-of <ISO-8601-offset-time> --policy <observation-policy.json>]
      ce collect --plan <plan.json> --out <directory>
      ce validate-collection <directory>
      ce package-feeds --import <legacy-settings> --catalog <public-catalog> [--host-file <outside-workspace>]
      ce validate <artifact.jsonl...>
      ce verify-evidence --repo <git dir> <artifact.jsonl...>
      ce migrate [--connection <connection string>]
      ce import-deprecations --file <legacy-deprecations.json> [--connection <connection string>]
      ce import [--connection <connection string>] [--bindings <bindings.json>] [--collections <directory,...>] [artifact.jsonl...]  (native artifacts or validated model collections required)
      ce publish [--scope <capability-id|*>] [--view <repo:full-sha,...>] [--bindings <bindings.json>] [--collections <directory,...>] [--expected-generation <n>] [artifact.jsonl...]
      ce reconcile-publication --run <id> --attempt <id> [--connection <connection string>]  (read-only; never authorizes recovery)
      ce embed [--snapshot <id> | --scope <capability-id|*>] [--connection <connection string>] [--settings <model.json>]  (whole-snapshot projection; model from environment or saved settings)
      ce export (disabled: use the exclusive host analysis worker)
      ce evaluate --expected <dir> --actual <dir> [--config <evaluation.json>] [--repos <dir>] [--repository <id[,id]>] [--language <csharp,typescript,...>] [--json <report.json>] [--max-findings <n>]
    """;

if (args.Length == 0)
{
    Console.Error.WriteLine(Usage);
    return 2;
}

var command = args[0];
var strictCollectionOptions = command is "collect" or "validate-collection" or "collection-freshness" or "embed";
var options = new Dictionary<string, string>(StringComparer.Ordinal);
var files = new List<string>();
for (var i = 1; i < args.Length; i++)
{
    if (strictCollectionOptions && args[i].StartsWith("--", StringComparison.Ordinal) &&
        (i + 1 == args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal) ||
         options.ContainsKey(args[i][2..])))
    {
        Console.Error.WriteLine($"{command}: missing value or duplicate option {args[i]}");
        return 2;
    }
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
    case "collect":
    case "validate-collection":
        return await CollectionCommand.RunAsync(command, options, files);
    case "collection-freshness":
        if (files.Count != 0) { Console.Error.WriteLine(Usage); return 2; }
        return await CollectionFreshnessCommand.RunAsync(options);
    case "package-feeds":
        return HostPackageFeeds.Command(Path.GetFullPath(Environment.CurrentDirectory), options);
    case "analysis-worker":
    case "analysis-run":
    case "analysis-all":
        return await AnalysisWorkerCommand.RunAsync(command, options);
    case "reconcile-publication":
        return await PublicationReconciliationCommand.RunAsync(options);

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

    case "import-deprecations":
    {
        if (!options.TryGetValue("file", out var path) || files.Count != 0 ||
            options.Keys.Any(key => key is not ("file" or "connection")))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        try
        {
            await using var db = OpenDatabase();
            await using var input = File.OpenRead(Path.GetFullPath(path));
            var report = await new PostgresDeprecationRepository(db).ImportLegacyAsync(input);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
            return report.Issues.Count == 0 ? 0 : 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            DeprecationValidationException or System.Text.Json.JsonException or NpgsqlException or InvalidOperationException)
        {
            Console.Error.WriteLine($"import-deprecations: import failed ({error.GetType().Name}); verify the file and migrated database");
            return 1;
        }
    }

    case "import":
    case "publish":
    {
        await using var db = OpenDatabase();
        return await ImportCommand.RunAsync(db, files, options, command == "publish");
    }

    case "embed":
    {
        try
        {
            if (files.Count > 0 || options.Keys.Any(key => key is not ("snapshot" or "scope" or "connection" or "settings"))
                || options.ContainsKey("snapshot") && options.ContainsKey("scope"))
                throw new ArgumentException("invalid embed options");
            var configuration = EmbeddingConfiguration.Resolve(new InterpretationOptions(), options.GetValueOrDefault("settings") ?? ModelSettings.DefaultPath);
            if (configuration is null)
            {
                Console.Error.WriteLine("no embedding model: save one in Settings or set CE_MODEL_ENDPOINT and CE_EMBEDDING_MODEL");
                return 2;
            }
            var model = configuration.Options.CreateEmbeddingModel()!;
            var connection = options.GetValueOrDefault("connection") ?? Environment.GetEnvironmentVariable("CE_DATABASE")
                ?? throw new InvalidOperationException("PostgreSQL connection is required");
            await using var db = NpgsqlDataSource.Create(connection);
            var store = new SnapshotEmbeddingStore(db, connection);
            var snapshot = options.TryGetValue("snapshot", out var pinned)
                ? Guid.TryParse(pinned, out var id) ? id : throw new ArgumentException("invalid snapshot")
                : await store.ResolvePublishedAsync(options.GetValueOrDefault("scope") ?? "*");
            var identity = configuration.Identity;
            var count = await store.EmbedAsync(snapshot, new(identity.Id, identity.Fingerprint, identity.RecipeVersion),
                (texts, ct) => model.EmbedAsync(texts, ct));
            Console.WriteLine($"Embedding projection ready for snapshot {snapshot}: embedded {count} occurrence(s) ({identity.Id}); PostgreSQL similarity search remains unavailable.");
            return 0;
        }
        catch (Exception e) when (e is EmbeddingConfigurationException or EmbeddingProjectionBusyException or EmbeddingProjectionFailedException
            or InterpretationUnavailableException or NpgsqlException or InvalidOperationException or ArgumentException or IOException)
        {
            Console.Error.WriteLine($"embed: failed ({e.GetType().Name}); verify configuration and retry the same snapshot");
            return 1;
        }
    }

    case "export":
    {
        Console.Error.WriteLine("export: direct acquisition is disabled; use the exclusive host analysis worker");
        return 2;
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
