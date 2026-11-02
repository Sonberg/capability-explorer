using CapabilityExplorer.Capabilities.Analysis;
using CapabilityExplorer.Binding;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Store;
using Npgsql;

namespace CapabilityExplorer.Cli;

internal static class AnalysisPublication
{
    internal static async Task Publish(string root, AnalysisRun run, PreparedAnalysis prepared, PublicationPermit permit)
    {
        var files = new List<string>();
        var canonicalHashes = new HashSet<string>(StringComparer.Ordinal);
        // Validate every artifact before the first externally visible effect.
        foreach (var pair in prepared.Artifacts)
        {
            AnalysisPreparation.RejectLinks(pair.Key);
            if (AnalysisWorkerCommand.HashFile(pair.Key) != pair.Value) throw new IOException("artifact changed after binding");
            var report = ArtifactValidator.Validate(await File.ReadAllTextAsync(pair.Key));
            if (!report.IsValid) throw new FormatException("invalid staged artifact");
            var repo = prepared.Repositories.Single(r => pair.Key.StartsWith(r.Staging + Path.DirectorySeparatorChar,StringComparison.Ordinal));
            if (report.Artifact!.Header.RepositoryId != repo.Id || report.Artifact.Header.CommitSha != repo.Sha) throw new FormatException("artifact revision mismatch");
            canonicalHashes.Add(report.Artifact.ContentSha256);
        }
        if (prepared.Repositories.Any(repo => !prepared.Artifacts.Keys.Any(path => path.StartsWith(repo.Staging + Path.DirectorySeparatorChar,StringComparison.Ordinal))))
            throw new FormatException("each manifest repository requires at least one valid artifact before publication");
        foreach (var repo in prepared.Repositories)
        {
            var destination = Path.Combine(root,".sandbox-cache/artifacts",repo.Id,repo.Sha);
            var previous = Path.Combine(Path.GetDirectoryName(repo.Staging)!,"previous-" + run.RunId + "-" + run.AttemptId);
            AnalysisPreparation.RejectLinks(destination); AnalysisPreparation.RejectLinks(previous);
            if (Directory.Exists(previous) || File.Exists(destination)) throw new IOException("publication destination contains unreconciled evidence");
            var expected = prepared.Artifacts.Where(p => p.Key.StartsWith(repo.Staging + Path.DirectorySeparatorChar,StringComparison.Ordinal))
                .ToDictionary(p => Path.GetRelativePath(repo.Staging,p.Key),p => p.Value,StringComparer.Ordinal);
            AssertFiles(repo.Staging,expected);
            permit.Intent("local-artifacts",destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (Directory.Exists(destination)) Directory.Move(destination,previous);
            Directory.Move(repo.Staging,destination);
            AssertFiles(destination,expected);
            var evidence = AnalysisWorkerCommand.HashText(string.Join('\n',expected.OrderBy(p => p.Key,StringComparer.Ordinal).Select(p => p.Key + ":" + p.Value)));
            permit.Confirm("local-artifacts",destination,evidence);
            files.AddRange(expected.Keys.Select(p => Path.Combine(destination,p)));
            // Preserve the previous directory as crash evidence. No unguarded cleanup/retry.
        }
        if (Environment.GetEnvironmentVariable("CE_DATABASE") is not { Length: > 0 } connection) return;
        permit.Intent("postgres",run.CapabilityId);
        await using var database = NpgsqlDataSource.Create(connection);
        await Migrator.MigrateAsync(database);
        var options = new Dictionary<string,string> { ["scope"] = run.CapabilityId };
        if (run.BindingsPath is {} binding) options["bindings"] = binding;
        var result = await ImportCommand.RunAsync(database,files,options,publish:true);
        if (result != 0) throw new InvalidOperationException("database publication not confirmed");
        var published = await new SnapshotStore(database).GetPublishedAsync(run.CapabilityId)
            ?? throw new InvalidOperationException("database publication receipt unavailable");
        var actual = new HashSet<string>(StringComparer.Ordinal);
        await using (var query = database.CreateCommand("select a.content_sha256 from analysis_snapshot_artifact s join artifact a on a.id=s.artifact_id where s.snapshot_id=$1"))
        {
            query.Parameters.AddWithValue(published.Id);
            await using var reader = await query.ExecuteReaderAsync();
            while (await reader.ReadAsync()) actual.Add(reader.GetString(0));
        }
        var orderedHashes = string.Join('\n',canonicalHashes.Order(StringComparer.Ordinal));
        var bindingIdentity = run.BindingsPath is null ? "no-bindings" : AnalysisWorkerCommand.HashText(
            BindingStore.ImportedSnapshotIdentityVersion + "\n" + Binder.RuleName + "\n" + Binder.RuleVersion + "\n" + orderedHashes + "\n" + BindingFile.Load(run.BindingsPath).Sha256);
        var expectedIdentity = AnalysisWorkerCommand.HashText("ce-analysis-snapshot.v2\n" + bindingIdentity + "\n" + orderedHashes + "\n");
        if (!actual.SetEquals(canonicalHashes) || published.ArtifactCount != canonicalHashes.Count || published.InputIdentity != expectedIdentity)
            throw new InvalidOperationException("published snapshot does not match resolved artifact receipt");
        permit.Confirm("postgres",run.CapabilityId,AnalysisWorkerCommand.HashText(run.AcceptedInputsHash + ":" + run.ManifestHash + ":" + published.Id + ":" + published.InputIdentity));
    }

    private static void AssertFiles(string directory, Dictionary<string,string> expected)
    {
        var files = AnalysisPreparation.CheckedFiles(directory).ToArray();
        if (files.Length != expected.Count) throw new IOException("artifact set changed after binding");
        foreach (var file in files)
            if (!expected.TryGetValue(Path.GetRelativePath(directory,file),out var hash) || AnalysisWorkerCommand.HashFile(file) != hash)
                throw new IOException("artifact bytes changed after binding");
    }
}
