using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CapabilityExplorer.Binding;
using CapabilityExplorer.Store;
using Npgsql;

namespace CapabilityExplorer.Cli;

/// <summary>Trusted handoff import. The published pointer moves only after every input succeeds.</summary>
internal static class ImportCommand
{
    public static async Task<int> RunAsync(NpgsqlDataSource database, IReadOnlyList<string> files,
        IReadOnlyDictionary<string, string> options, bool publish, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(options);
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
            var snapshots = new SnapshotStore(database);
            var expected = publish ? (await snapshots.GetPublishedAsync(scope, cancellationToken))?.Generation ?? 0 : 0;
            if (options.TryGetValue("expected-generation", out var supplied))
            {
                if (!publish || !long.TryParse(supplied, NumberStyles.None, CultureInfo.InvariantCulture, out expected))
                    throw new FormatException("expected generation must be a nonnegative integer on publish");
            }
            var importer = new ArtifactImporter(database);
            var stored = new HashSet<long>();
            var failed = false;
            foreach (var file in files)
            {
                try
                {
                    await using var stream = File.OpenRead(file);
                    var result = await importer.ImportAsync(stream, cancellationToken);
                    stored.Add(result.ArtifactId);
                    Console.WriteLine($"{(result.Outcome == ImportOutcome.Imported ? "imported " : "unchanged")} {file} (artifact {result.ArtifactId})");
                }
                catch (Exception e) when (e is InvalidArtifactException or ArtifactConflictException or IOException)
                {
                    failed = true;
                    Console.Error.WriteLine($"FAIL      {file}: import failed ({e.GetType().Name}); publication unchanged");
                }
            }
            if (failed) return 1;
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
                var identity = await Identity(database, ids, bindings?.Sha256 ?? BindingFile.Empty.Sha256, defaults, cancellationToken);
                var candidate = await snapshots.CreateAsync(scope, identity, ids, defaults, cancellationToken);
                if (bound is not null)
                {
                    await using var link = database.CreateCommand("insert into analysis_snapshot_binding(snapshot_id,binding_snapshot_id) values($1,$2) on conflict do nothing");
                    link.Parameters.AddWithValue(candidate.Id); link.Parameters.AddWithValue(bound.SnapshotId);
                    await link.ExecuteNonQueryAsync(cancellationToken);
                }
                var result = await snapshots.PublishAsync(candidate.Id, expected, cancellationToken);
                Console.WriteLine($"published snapshot {result.Id} scope {scope} generation {result.Generation}");
            }
            return 0;
        }
        catch (Exception e) when (e is FormatException or IOException or ArgumentException or SnapshotConflictException)
        {
            Console.Error.WriteLine($"import: {e.Message}; publication unchanged"); return 2;
        }
        catch (NpgsqlException)
        {
            Console.Error.WriteLine("import: database operation failed; publication unchanged"); return 1;
        }
        catch (InvalidOperationException)
        {
            Console.Error.WriteLine("import: search projection is incomplete or exceeds its normalization budget; publication unchanged"); return 1;
        }
    }

    private static async Task<string> Identity(NpgsqlDataSource database, long[] ids, string bindingHash, IReadOnlyDictionary<string, string> defaults, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes("ce-analysis-snapshot.v1\nservice-map.1\n" + bindingHash + "\n"));
        foreach (var pair in defaults.OrderBy(p => p.Key, StringComparer.Ordinal))
            hash.AppendData(Encoding.UTF8.GetBytes(pair.Key + ":" + pair.Value + "\n"));
        await using var command = database.CreateCommand("select content_sha256 from artifact where id=any($1) order by content_sha256");
        command.Parameters.AddWithValue(ids);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) hash.AppendData(Encoding.UTF8.GetBytes(reader.GetString(0) + "\n"));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
