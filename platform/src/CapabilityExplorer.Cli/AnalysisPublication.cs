using CapabilityExplorer.Capabilities.Analysis;
using CapabilityExplorer.Binding;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Store;
using Npgsql;

namespace CapabilityExplorer.Cli;

internal static class AnalysisPublication
{
    internal static Func<CancellationToken,Task>? BeforeHostConfirmationForTests { get; set; }
    internal static Func<Task>? BeforeDatabaseImportForTests { get; set; }

    internal static async Task Publish(string root, AnalysisRun run, PreparedAnalysis prepared, PublicationPermit permit)
    {
        permit.RequireIdentity(new AttemptIdentity(run.RunId,run.AttemptId!),run.AcceptedInputsHash,run.ManifestHash!);
        prepared = AnalysisPublicationBinding.Validate(root,run,prepared);
        var files = new List<string>();
        var originalHashes = new Dictionary<string,string>(StringComparer.Ordinal);
        var canonicalHashes = new HashSet<string>(StringComparer.Ordinal);
        // Validate every artifact before the first externally visible effect.
        foreach (var pair in prepared.Artifacts.OrderBy(p => p.Key, StringComparer.Ordinal))
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
            var publication = Path.Combine(root,".sandbox-cache/artifact-publication",run.RunId,run.AttemptId!,repo.Id,repo.Sha);
            var preparedDirectory = Path.Combine(publication,"prepared");
            var previous = Path.Combine(publication,"previous");
            AnalysisPreparation.RejectLinks(destination); AnalysisPreparation.RejectLinks(publication);
            AnalysisPreparation.RejectLinks(preparedDirectory); AnalysisPreparation.RejectLinks(previous);
            if (Directory.Exists(publication) || File.Exists(publication) || File.Exists(destination))
                throw new IOException("publication destination contains unreconciled evidence");
            var expected = prepared.Artifacts.Where(p => p.Key.StartsWith(repo.Staging + Path.DirectorySeparatorChar,StringComparison.Ordinal))
                .OrderBy(p => p.Key,StringComparer.Ordinal)
                .ToDictionary(p => Path.GetRelativePath(repo.Staging,p.Key),p => p.Value,StringComparer.Ordinal);
            AssertFiles(repo.Staging,expected);
            Directory.CreateDirectory(preparedDirectory);
            foreach (var path in expected.Keys.Order(StringComparer.Ordinal))
            {
                var target = Path.Combine(preparedDirectory,path);
                AnalysisPreparation.RejectLinks(target);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(repo.Staging,path),target,overwrite:false);
            }
            AssertFiles(preparedDirectory,expected);
            AssertFiles(repo.Staging,expected);
            permit.Intent("local-artifacts",destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (Directory.Exists(destination)) Directory.Move(destination,previous);
            Directory.Move(preparedDirectory,destination);
            AssertFiles(destination,expected);
            var evidence = AnalysisWorkerCommand.HashText(string.Join('\n',expected.OrderBy(p => p.Key,StringComparer.Ordinal).Select(p => p.Key + ":" + p.Value)));
            permit.Confirm("local-artifacts",destination,evidence);
            foreach (var path in expected.Keys.Order(StringComparer.Ordinal))
            {
                var file = Path.Combine(destination,path);
                files.Add(file);
                originalHashes.Add(file,expected[path]);
            }
            // Preserve the previous directory as crash evidence. No unguarded cleanup/retry.
        }
        if (Environment.GetEnvironmentVariable("CE_DATABASE") is not { Length: > 0 } connection) return;
        permit.Intent("postgres",run.CapabilityId);
        await using var database = NpgsqlDataSource.Create(connection);
        await Migrator.MigrateAsync(database);
        var options = new Dictionary<string,string> { ["scope"] = run.CapabilityId };
        if (run.BindingsPath is {} binding) options["bindings"] = binding;
        if (BeforeDatabaseImportForTests is { } importBarrier) await importBarrier();
        var expectations = new List<PublicationArtifactExpectation>(files.Count);
        for (var index = 0; index < files.Count; index++)
        {
            AnalysisPreparation.RejectLinks(files[index]);
            var bytes = await File.ReadAllBytesAsync(files[index]);
            var sourceHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            if (sourceHash != originalHashes[files[index]])
                throw new IOException("artifact changed from its original publication binding");
            var report = ArtifactValidator.Validate(System.Text.Encoding.UTF8.GetString(bytes));
            if (!report.IsValid) throw new FormatException("artifact changed after the publication move");
            var header = report.Artifact!.Header;
            expectations.Add(new(index, header.RepositoryId, header.CommitSha, header.Unit.Path,
                header.Extractor.Name, header.Extractor.Version, header.ConfigHash,
                report.Artifact.ContentSha256, sourceHash));
        }
        var result = await ImportCommand.RunAnalysisAsync(database,files,options,run.RunId,run.AttemptId!,
            run.AcceptedInputsHash,run.ManifestHash!,expectations);
        if (result != 0) throw new InvalidOperationException("database publication not confirmed");
        var receipt = await new SnapshotStore(database).ConfirmPublicationAttemptAsync(run.RunId,run.AttemptId!,expectations);
        var orderedHashes = string.Join('\n',canonicalHashes.Order(StringComparer.Ordinal));
        var bindingIdentity = run.BindingsPath is null ? "no-bindings" : AnalysisWorkerCommand.HashText(
            BindingStore.ImportedSnapshotIdentityVersion + "\n" + Binder.RuleName + "\n" + Binder.RuleVersion + "\n" + orderedHashes + "\n" + BindingFile.Load(run.BindingsPath).Sha256);
        var expectedIdentity = AnalysisWorkerCommand.HashText("ce-analysis-snapshot.v2\n" + bindingIdentity + "\n" + orderedHashes + "\n");
        if (receipt.Outcome != "confirmed-published" || receipt.CapabilityId != run.CapabilityId ||
            receipt.AcceptedInputsHash != run.AcceptedInputsHash || receipt.ManifestHash != run.ManifestHash ||
            receipt.CandidateInputIdentity != expectedIdentity || receipt.CandidateSnapshotId is null ||
            receipt.CandidatePublicationState != "published" || receipt.AttemptPublishedGeneration is null ||
            receipt.CandidatePublishedGeneration != receipt.AttemptPublishedGeneration ||
            receipt.Artifacts.Count != expectations.Count ||
            receipt.Artifacts.Any(a => a.Outcome is not ("imported" or "reused")) ||
            !receipt.Artifacts.Select(a => a.ContentSha256).ToHashSet(StringComparer.Ordinal).SetEquals(canonicalHashes))
            throw new InvalidOperationException("durable publication attempt receipt does not match the resolved artifact selection");
        if (BeforeHostConfirmationForTests is { } barrier)
            await barrier(CancellationToken.None);
        permit.Confirm("postgres",run.CapabilityId,AnalysisWorkerCommand.HashText(
            run.AcceptedInputsHash + ":" + run.ManifestHash + ":" + receipt.CandidateSnapshotId + ":" + receipt.CandidateInputIdentity));
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
