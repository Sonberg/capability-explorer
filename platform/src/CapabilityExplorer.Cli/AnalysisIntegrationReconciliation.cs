using CapabilityExplorer.Capabilities.Analysis;
using CapabilityExplorer.Binding;
using CapabilityExplorer.Contracts;
using System.Text.Json;

namespace CapabilityExplorer.Cli;

internal static class AnalysisIntegrationReconciliation
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static void Reconcile(string root, FileRunStore store)
    {
        var catalogueStore = new IntegrationMatchCatalogueStore(root);
        using var writer = catalogueStore.AcquireWriter();
        var runs = store.ReadPublishedRuns();
        var inputs = new List<IntegrationMatchInput>();
        var bindings = new List<BindingEntry>();
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var run in runs)
        {
            var acceptedPath = Path.Combine(store.RunDirectory(run.RunId), "accepted.json");
            AnalysisPreparation.RejectLinks(acceptedPath);
            AnalysisPreparation.RejectLinks(run.ConfigurationPath);
            if (new FileInfo(acceptedPath).Length > 1024 * 1024 || AnalysisWorkerCommand.HashFile(acceptedPath) != run.AcceptedInputsHash)
                throw new InvalidDataException("Published accepted inputs changed.");
            using var accepted = JsonDocument.Parse(File.ReadAllBytes(acceptedPath));
            var configurationHash = accepted.RootElement.GetProperty("configuration").GetString()
                ?? throw new InvalidDataException("Missing accepted configuration identity.");
            if (AnalysisWorkerCommand.HashFile(run.ConfigurationPath) != configurationHash)
                throw new InvalidDataException("Published frozen configuration changed.");
            files[acceptedPath] = run.AcceptedInputsHash;
            files[run.ConfigurationPath] = configurationHash;
            if (run.BindingsPath is not null)
            {
                AnalysisPreparation.RejectLinks(run.BindingsPath);
                var bindingHash = accepted.RootElement.GetProperty("bindings").GetString();
                if (bindingHash is null || AnalysisWorkerCommand.HashFile(run.BindingsPath) != bindingHash)
                    throw new InvalidDataException("Published frozen bindings changed.");
                files[run.BindingsPath] = bindingHash;
            }
            else if (accepted.RootElement.GetProperty("bindings").ValueKind != JsonValueKind.Null)
                throw new InvalidDataException("Published binding selection changed.");
            var manifestPath = Path.Combine(store.RunDirectory(run.RunId), "resolved.json");
            AnalysisPreparation.RejectLinks(manifestPath);
            if (new FileInfo(manifestPath).Length > 1024 * 1024) throw new InvalidDataException("Published manifest exceeds its budget.");
            var bytes = File.ReadAllBytes(manifestPath);
            if (bytes.Length > 1024 * 1024 || AnalysisWorkerCommand.HashFile(manifestPath) != run.ManifestHash)
                throw new InvalidDataException("Published manifest identity changed.");
            var manifest = JsonSerializer.Deserialize<ResolvedManifest>(bytes, Json) ?? throw new InvalidDataException("Missing manifest.");
            if (manifest.AcceptedInputsHash != run.AcceptedInputsHash) throw new InvalidDataException("Published input identity changed.");
            if (run.BindingsPath is not null) bindings.AddRange(BindingFile.Load(run.BindingsPath).Entries);
            foreach (var revision in manifest.Inputs.Where(pair => pair.Key.StartsWith("revision:", StringComparison.Ordinal)))
            {
                var repository = revision.Key[9..];
                if (!SafePart(repository) || revision.Value.Length != 40 || !revision.Value.All(char.IsAsciiHexDigit))
                    throw new InvalidDataException("Invalid published repository identity.");
                var destination = Path.Combine(root, ".sandbox-cache", "artifacts", repository, revision.Value);
                if (!run.Effects!.Any(effect => effect.Surface == "local-artifacts" && effect.Destination == destination && effect.State == "confirmed"))
                    throw new InvalidDataException("Published artifact receipt missing.");
                foreach (var artifact in manifest.Inputs.Where(pair => pair.Key.StartsWith("artifact:" + repository + ":", StringComparison.Ordinal)))
                {
                    var relative = artifact.Key[(10 + repository.Length)..];
                    if (Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Split('/').Any(part => !SafePart(part))
                        || !relative.EndsWith(".jsonl", StringComparison.Ordinal)) throw new InvalidDataException("Invalid artifact path.");
                    var path = Path.Combine(destination, relative);
                    AnalysisPreparation.RejectLinks(path);
                    var text = File.ReadAllText(path);
                    if (AnalysisWorkerCommand.HashText(text) != artifact.Value) throw new InvalidDataException("Published artifact hash changed.");
                    var report = ArtifactValidator.Validate(text);
                    if (!report.IsValid || report.Artifact!.Header.RepositoryId != repository || report.Artifact.Header.CommitSha != revision.Value)
                        throw new InvalidDataException("Published artifact contract changed.");
                    var loaded = report.Artifact!;
                    files[path] = artifact.Value;
                    inputs.Add(new(run.CapabilityId, run.RunId, new(repository, revision.Value, loaded.Header.Unit.Path), loaded));
                }
            }
        }
        var groups = bindings.GroupBy(entry => entry.Hint, StringComparer.Ordinal).ToList();
        if (groups.Any(group => group.Select(entry => entry.Target).Distinct(StringComparer.Ordinal).Count() > 1))
            throw new InvalidDataException("Conflicting published bindings require review.");
        var bindingFile = BindingFile.Parse(JsonSerializer.Serialize(new { bindings = groups.Select(group => group.First()) }, Json));
        var matches = IntegrationMatching.Build(inputs, bindingFile);
        var observations = inputs.SelectMany(input => input.Artifact.Edges
            .Where(edge => IntegrationRelation(edge.Relation)).Select(edge => (input.CapabilityId, input.RunId, input.Artifact.ContentSha256, edge.Id)))
            .Distinct().ToList();
        var matched = matches.Where(match => match.Status == "matched")
            .Select(match => (match.SourceCapabilityId, match.SourceRunId, match.Source.ArtifactHash, match.EdgeId)).ToHashSet();
        if (!runs.Select(run => run.RunId).SequenceEqual(store.ReadPublishedRuns().Select(run => run.RunId))
            || files.Any(file => AnalysisWorkerCommand.HashFile(file.Key) != file.Value))
            throw new IOException("Published inputs changed during matching; retry required.");
        catalogueStore.Write(new(IntegrationMatching.AlgorithmVersion, IntegrationMatchCatalogueStore.Fingerprint(inputs, bindingFile.Sha256),
            DateTimeOffset.UtcNow, inputs.Select(input => new IntegrationMatchArtifact(input.CapabilityId, input.RunId,
                new(input.From.RepositoryId, input.From.CommitSha, input.From.UnitPath, input.Artifact.ContentSha256))).ToList(),
            observations.Count, observations.Count(observation => !matched.Contains(observation)), matches));
    }

    internal static bool IntegrationRelation(string relation) => relation is "REQUESTS" or "PUBLISHES" or "CONSUMES" or "READS" or "WRITES" or "MAPS_TO";

    internal static void TryReconcile(string root, FileRunStore store)
    {
        try { Reconcile(root, store); }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or JsonException or FormatException)
        { Console.Error.WriteLine("analysis: integration matching pending; published artifacts preserved. Run ce match-integrations to retry."); }
    }

    internal static int Command(IReadOnlyDictionary<string, string> options, IReadOnlyList<string> files)
    {
        if (options.Count != 0 || files.Count != 0) return 2;
        var root = Path.GetFullPath(Environment.CurrentDirectory);
        try
        {
            Reconcile(root, new FileRunStore(Path.Combine(root, ".sandbox-cache", "requests")));
            var catalogue = new IntegrationMatchCatalogueStore(root).Read()!;
            Console.WriteLine(JsonSerializer.Serialize(new { catalogue.Fingerprint, capabilities = catalogue.Inputs.Select(input => input.CapabilityId).Distinct().Count(),
                artifacts = catalogue.Inputs.Count, catalogue.Observations, catalogue.Unresolved,
                matched = catalogue.Matches.Count(match => match.Status == "matched"),
                inferred = catalogue.Matches.Count(match => match.Status == "inferred"),
                clusters = catalogue.Matches.Where(match => match.ClusterId is not null).Select(match => match.ClusterId).Distinct(StringComparer.Ordinal).Count(),
                suggested = catalogue.Matches.Count(match => match.Status == "suggested") }, Json));
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or JsonException or FormatException)
        { Console.Error.WriteLine("integration matching failed (" + error.GetType().Name + "); existing catalogue and analysis receipts preserved."); return 1; }
    }

    private static bool SafePart(string part) => part.Length > 0 && part is not ("." or "..")
        && part.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
}