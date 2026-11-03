using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CapabilityExplorer.Binding;
using CapabilityExplorer.Store;
using Npgsql;

namespace CapabilityExplorer.Cli;

internal sealed record AnalysisPublicationContext(string RunId, string AttemptId,
    string AcceptedInputsHash, string ManifestHash,
    IReadOnlyList<PublicationArtifactExpectation> Artifacts);

/// <summary>Trusted handoff import. The published pointer moves only after every input succeeds.</summary>
internal static class ImportCommand
{
    public static async Task<int> RunAsync(NpgsqlDataSource database, IReadOnlyList<string> files,
        IReadOnlyDictionary<string, string> options, bool publish, CancellationToken cancellationToken = default)
        => await RunCoreAsync(database, files, options, publish, null, cancellationToken);

    internal static Task<int> RunAnalysisAsync(NpgsqlDataSource database, IReadOnlyList<string> files,
        IReadOnlyDictionary<string, string> options, string runId, string attemptId,
        string acceptedInputsHash, string manifestHash,
        IReadOnlyList<PublicationArtifactExpectation> expectedArtifacts,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(database, files, options, true,
            new AnalysisPublicationContext(runId, attemptId, acceptedInputsHash, manifestHash, expectedArtifacts),
            cancellationToken);

    private static async Task<int> RunCoreAsync(NpgsqlDataSource database, IReadOnlyList<string> files,
        IReadOnlyDictionary<string, string> options, bool publish, AnalysisPublicationContext? publicationAttempt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(options);
        var publishStarted = false;
        Guid? candidateId = null;
        SnapshotStore? snapshots = null;
        if (files.Count is < 1 or > SnapshotStore.MaximumArtifacts)
        {
            Console.Error.WriteLine("import requires 1 to 100000 artifact files"); return 2;
        }
        try
        {
            BindingFile? bindings = null;
            if (options.TryGetValue("bindings", out var path))
            {
                if (new FileInfo(path).Length > 1048576) throw new FormatException("bindings file exceeds one MiB");
                bindings = BindingFile.Load(path);
            }
            var scope = options.GetValueOrDefault("scope") ?? "*";
            if (scope != "*" && (scope.Length is < 1 or > 100 || !char.IsAsciiLetterOrDigit(scope[0])
                || scope.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_' or '-'))))
                throw new FormatException("invalid publication scope");
            var defaults = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var part in (options.GetValueOrDefault("view") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = part.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0 || defaults.Count >= 100 || !defaults.TryAdd(part[..colon], part[(colon + 1)..]))
                    throw new FormatException("invalid default revision selection");
                var sha = part[(colon + 1)..];
                if (sha.Length != 40 || !sha.All(char.IsAsciiHexDigit)) throw new FormatException("default revision requires a full SHA");
            }
            snapshots = new SnapshotStore(database);
            var expected = publish ? (await snapshots.GetPublishedAsync(scope, cancellationToken))?.Generation ?? 0 : 0;
            if (options.TryGetValue("expected-generation", out var supplied))
            {
                if (!publish || !long.TryParse(supplied, NumberStyles.None, CultureInfo.InvariantCulture, out expected))
                    throw new FormatException("expected generation must be a nonnegative integer on publish");
            }
            if (publicationAttempt is not null)
            {
                if (!publish || scope != options.GetValueOrDefault("scope") || publicationAttempt.Artifacts.Count != files.Count)
                    throw new InvalidOperationException("analysis publication identity differs from the selected import");
                await snapshots.BeginPublicationAttemptAsync(new PublicationAttemptDescriptor(
                    publicationAttempt.RunId, publicationAttempt.AttemptId, scope,
                    publicationAttempt.AcceptedInputsHash, publicationAttempt.ManifestHash, expected,
                    publicationAttempt.Artifacts), cancellationToken);
            }
            var importer = new ArtifactImporter(database);
            var stored = new HashSet<long>();
            var failed = false;
            for (var index = 0; index < files.Count; index++)
            {
                var file = files[index];
                try
                {
                    await using var stream = File.OpenRead(file);
                    var context = publicationAttempt is null ? null :
                        new PublicationArtifactImport(publicationAttempt.RunId, publicationAttempt.AttemptId,
                            index, publicationAttempt.Artifacts[index].SourceSha256);
                    var result = context is null
                        ? await importer.ImportAsync(stream, cancellationToken)
                        : await importer.ImportForPublicationAsync(stream, context, cancellationToken);
                    stored.Add(result.ArtifactId);
                    Console.WriteLine($"{(result.Outcome == ImportOutcome.Imported ? "imported " : "unchanged ")}{file} (artifact {result.ArtifactId})");
                }
                catch (Exception e) when (e is InvalidArtifactException or ArtifactConflictException or IOException)
                {
                    failed = true;
                    if (publicationAttempt is not null && e is not ArtifactConflictException)
                    {
                        var state = e is FileNotFoundException or DirectoryNotFoundException ? "missing" : "invalid";
                        await snapshots.RecordPublicationArtifactAsync(publicationAttempt.RunId,
                            publicationAttempt.AttemptId, index, state, cancellationToken: cancellationToken);
                    }
                    Console.Error.WriteLine($"FAIL      {file}: import failed ({e.GetType().Name});");
                }
            }
            if (failed)
            {
                if (publicationAttempt is not null)
                {
                    await snapshots.RecordPrepublicationFailureAsync(publicationAttempt.RunId, publicationAttempt.AttemptId,
                        null, cancellationToken);
                    Console.Error.WriteLine("publication attempt confirmed not published; global pointer is not asserted unchanged");
                }
                return 1;
            }
            var ids = stored.Order().ToArray();
            BindingSaveResult? bound = null;
            if (bindings is not null)
            {
                bound = await new BindingStore(database).SaveImportedAsync(ids, bindings, cancellationToken);
                Console.WriteLine($"bindings  {(bound.Created ? "saved" : "unchanged")} snapshot {bound.SnapshotId[..12]}: {bound.BoundEdges} bound, {bound.BrokenEdges} broken");
                foreach (var unused in bound.UnusedBindings)
                    Console.WriteLine($"          unused binding {unused.Hint} (no analysed edge uses it)");
            }
            if (publish)
            {
                var identity = await Identity(database, ids, bound?.SnapshotId ?? "no-bindings", defaults, cancellationToken);
                var candidate = await snapshots.CreateAsync(scope, identity, ids, defaults, cancellationToken);
                candidateId = candidate.Id;
                if (bound is not null)
                {
                    await using var link = database.CreateCommand("insert into analysis_snapshot_binding(snapshot_id,binding_snapshot_id) values($1,$2) on conflict do nothing");
                    link.Parameters.AddWithValue(candidate.Id); link.Parameters.AddWithValue(bound.SnapshotId);
                    await link.ExecuteNonQueryAsync(cancellationToken);
                }
                if (publicationAttempt is not null)
                {
                    await snapshots.BindPublicationCandidateAsync(publicationAttempt.RunId,
                        publicationAttempt.AttemptId, candidate.Id, identity, cancellationToken);
                    publishStarted = true;
                    var result = await snapshots.PublishAttemptAsync(publicationAttempt.RunId,
                        publicationAttempt.AttemptId, cancellationToken);
                    if (!result.Published)
                    {
                        Console.Error.WriteLine(result.Reason == "stale_generation"
                            ? $"publication attempt confirmed not published ({result.Reason}); current generation observed {result.ObservedGeneration}"
                            : "candidate publication was observed without run-attempt attribution; outcome unresolved, do not retry before read-only reconciliation");
                        return 2;
                    }
                    Console.WriteLine($"published snapshot {result.SnapshotId} scope {scope} generation {result.Generation}");
                }
                else
                {
                    var result = await snapshots.PublishAsync(candidate.Id, expected, cancellationToken);
                    Console.WriteLine($"published snapshot {result.Id} scope {scope} generation {result.Generation}");
                }
            }
            return 0;
        }
        catch (Exception e) when (e is FormatException or IOException or ArgumentException or SnapshotConflictException)
        {
            if (publicationAttempt is not null && !publishStarted)
                await TryRecordPrepublicationFailure(snapshots, publicationAttempt, candidateId, cancellationToken);
            Console.Error.WriteLine($"import: {e.Message}; publication was not confirmed"); return 2;
        }
        catch (NpgsqlException e)
        {
            if (publicationAttempt is not null && !publishStarted)
                await TryRecordPrepublicationFailure(snapshots, publicationAttempt, candidateId, cancellationToken);
            Console.Error.WriteLine(publicationAttempt is not null && publishStarted
                ? "import: database response was lost or failed; publication outcome unresolved, do not retry before read-only reconciliation"
                : $"import: database operation failed ({e.GetType().Name}); publication outcome was not confirmed");
            return 1;
        }
        catch (InvalidOperationException e)
        {
            if (publicationAttempt is not null && !publishStarted)
                await TryRecordPrepublicationFailure(snapshots, publicationAttempt, candidateId, cancellationToken);
            Console.Error.WriteLine($"import: operation incomplete ({e.GetType().Name}); publication was not confirmed");
            return 1;
        }
    }

    private static async Task TryRecordPrepublicationFailure(SnapshotStore? snapshots,
        AnalysisPublicationContext attempt, Guid? candidateId, CancellationToken cancellationToken)
    {
        if (snapshots is null)
        {
            Console.Error.WriteLine("import: terminal receipt recording unavailable; publication outcome unresolved");
            return;
        }
        try { await snapshots.RecordPrepublicationFailureAsync(attempt.RunId, attempt.AttemptId, candidateId, cancellationToken); }
        catch (Exception error) when (error is NpgsqlException or InvalidOperationException or OperationCanceledException)
        {
            Console.Error.WriteLine($"import: terminal receipt recording failed ({error.GetType().Name}); publication outcome unresolved");
        }
    }

    private static async Task<string> Identity(NpgsqlDataSource database, long[] ids, string bindingIdentity, IReadOnlyDictionary<string, string> defaults, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // Bind the actual versioned derived snapshot, so upgrading occurrence semantics
        // cannot reuse an immutable analysis linked to an incomplete legacy binding.
        hash.AppendData(Encoding.UTF8.GetBytes("ce-analysis-snapshot.v2\n" + bindingIdentity + "\n"));
        foreach (var pair in defaults.OrderBy(p => p.Key, StringComparer.Ordinal))
            hash.AppendData(Encoding.UTF8.GetBytes(pair.Key + ":" + pair.Value + "\n"));
        await using var command = database.CreateCommand("select content_sha256 from artifact where id=any($1) order by content_sha256");
        command.Parameters.AddWithValue(ids);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) hash.AppendData(Encoding.UTF8.GetBytes(reader.GetString(0) + "\n"));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
