namespace CapabilityExplorer.Explorer;

/// <summary>One consistent load of artifacts and capabilities. Replaced as a whole, never mutated.</summary>
public sealed record ExplorerSnapshot(ExplorerIndex Index, CapabilityService Capabilities, long Generation)
{
    public ModelFileSnapshot Models { get; init; } = ModelFileSnapshot.Empty;
    public Guid ModelSnapshotId { get; init; } = Guid.NewGuid();
}

/// <summary>
/// Holds the current load and replaces it when artifact directories, capability definitions or snapshots change
/// on disk (e.g. `make analyze` finished in another container). Polling instead of file-system events, because
/// events do not cross bind mounts reliably (Docker Desktop on macOS).
/// </summary>
public sealed partial class ExplorerState(ExplorerOptions options, ILogger<ExplorerState>? logger = null) : BackgroundService
{
    private readonly object _gate = new();
    private ExplorerSnapshot? _current;
    private string _fingerprint = "";

    public static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    public ExplorerSnapshot Current
    {
        get
        {
            if (_current is { } c)
            {
                return c;
            }

            lock (_gate)
            {
                return _current ??= Load(0);
            }
        }
    }

    /// <summary>Reloads now if anything changed; returns whether it did.</summary>
    public bool Refresh()
    {
        lock (_gate)
        {
            var current = Current;
            if (Fingerprint() == _fingerprint)
            {
                return false;
            }

            _current = Load(current.Generation + 1);
            if (logger is not null)
            {
                Reloaded(logger, _current.Index.Artifacts.Count, _current.Generation);
            }

            return true;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                Refresh();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
            {
                // A writer is mid-swap; the next tick sees the finished state.
                if (logger is not null)
                {
                    Deferred(logger, e);
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "reloaded {Artifacts} artifacts (generation {Generation})")]
    private static partial void Reloaded(ILogger logger, int artifacts, long generation);

    [LoggerMessage(Level = LogLevel.Debug, Message = "reload deferred")]
    private static partial void Deferred(ILogger logger, Exception exception);

    private ExplorerSnapshot Load(long generation)
    {
        var fingerprint = Fingerprint();
        var models = ModelFileSnapshot.Load(options.CollectionDirectories ?? []);
        var (index, capabilities) = options.Build();
        if (Fingerprint() != fingerprint) throw new IOException("Explorer inputs changed during snapshot loading");
        _fingerprint = fingerprint;
        return new ExplorerSnapshot(index, capabilities, generation) { Models = models };
    }

    /// <summary>Names, sizes and write times of every artifact plus the capability files.</summary>
    private string Fingerprint()
    {
        var parts = new List<string>();
        foreach (var directory in options.CollectionDirectories ?? [])
        {
            if (!Directory.Exists(directory)) { parts.Add($"{directory}|missing"); continue; }
            var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Take(132).ToArray();
            if (files.Length > 130) throw new InvalidOperationException("collection file count exceeds bounds");
            foreach (var file in files)
            {
                var info = new FileInfo(file);
                parts.Add($"{file}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
            }
        }
        foreach (var dir in options.ArtifactDirectories.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                parts.Add($"{file}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
            }
        }

        foreach (var file in new[] { options.CapabilitiesFile,
            Path.ChangeExtension(options.CapabilitiesFile, $".local{Path.GetExtension(options.CapabilitiesFile)}"),
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.CapabilitiesFile))!, ".sandbox-cache", "integration-matches.json"),
            options.BindingsFile ?? "" }.Where(f => f.Length > 0))
        {
            var info = new FileInfo(file);
            parts.Add(info.Exists ? $"{file}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : $"{file}|-");
        }

        var checks = Path.Combine(options.RequestsDirectory, "checks");
        if (Directory.Exists(checks))
        {
            foreach (var file in Directory.EnumerateFiles(checks, "check.json", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                parts.Add($"{file}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
            }
        }

        parts.Sort(StringComparer.Ordinal);
        return string.Join('\n', parts);
    }
}
