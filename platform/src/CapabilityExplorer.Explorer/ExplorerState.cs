namespace CapabilityExplorer.Explorer;

/// <summary>One consistent load of artifacts and capabilities. Replaced as a whole, never mutated.</summary>
public sealed record ExplorerSnapshot(ExplorerIndex Index, CapabilityService Capabilities, long Generation);

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
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
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
        _fingerprint = Fingerprint();
        var (index, capabilities) = options.Build();
        return new ExplorerSnapshot(index, capabilities, generation);
    }

    /// <summary>Names, sizes and write times of every artifact plus the capability files.</summary>
    private string Fingerprint()
    {
        var parts = new List<string>();
        foreach (var dir in options.ArtifactDirectories.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                parts.Add($"{file}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
            }
        }

        foreach (var file in new[] { options.CapabilitiesFile, Path.Combine(options.Workspace, "snapshots.json") })
        {
            var info = new FileInfo(file);
            parts.Add(info.Exists ? $"{file}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : $"{file}|-");
        }

        parts.Sort(StringComparer.Ordinal);
        return string.Join('\n', parts);
    }
}
