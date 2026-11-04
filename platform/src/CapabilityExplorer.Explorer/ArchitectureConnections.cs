using System.Text.Json;
using CapabilityExplorer.Capabilities;

namespace CapabilityExplorer.Explorer;

public sealed record ArchitectureConnection(string Id, string FromCapabilityId, string ToCapabilityId, string Kind,
    string? Note, string? SourceRepositoryId, string? SourceKey, DateTimeOffset CreatedAt,
    string? TargetRepositoryId = null, string? TargetKey = null);
public sealed record ArchitectureConnectionSnapshot(long Version, IReadOnlyList<ArchitectureConnection> Connections);
public sealed record ArchitectureConnectionRequest(string FromCapabilityId, string ToCapabilityId, string Kind,
    long ExpectedVersion, string? Note = null, string? SourceRepositoryId = null, string? SourceKey = null,
    string? TargetRepositoryId = null, string? TargetKey = null, string? View = null);

internal sealed class ArchitectureConnectionConflictException() : Exception("Connections changed; reload before saving.");

internal sealed class ArchitectureConnections(string capabilitiesFile)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly HashSet<string> Kinds = ["http", "message", "navigation", "data", "schedule", "dependency"];
    private readonly string _directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(capabilitiesFile))!, ".sandbox-cache");
    private string FilePath => Path.Combine(_directory, "architecture-connections.json");

    public ArchitectureConnectionSnapshot Read()
    {
        RejectLink(_directory);
        RejectLink(FilePath);
        if (!File.Exists(FilePath)) return new(0, []);
        using var stream = File.OpenRead(FilePath);
        if (stream.Length > 1024 * 1024) throw new InvalidDataException("Connection catalogue exceeds its limit.");
        var snapshot = JsonSerializer.Deserialize<ArchitectureConnectionSnapshot>(stream, Json)
            ?? throw new InvalidDataException("Invalid connection catalogue.");
        if (snapshot.Version < 0 || snapshot.Connections is null || snapshot.Connections.Count > 1000
            || snapshot.Connections.Any(connection => !Guid.TryParseExact(connection.Id, "N", out _)
                || !Kinds.Contains(connection.Kind) || string.IsNullOrWhiteSpace(connection.FromCapabilityId)
                || string.IsNullOrWhiteSpace(connection.ToCapabilityId) || connection.Note?.Length > 1000
                || connection.SourceKey?.Length > 16384 || connection.TargetKey?.Length > 16384)
            || snapshot.Connections.Select(connection => connection.Id).Distinct(StringComparer.Ordinal).Count() != snapshot.Connections.Count)
            throw new InvalidDataException("Invalid connection catalogue.");
        return snapshot;
    }

    public ArchitectureConnectionSnapshot Save(ArchitectureConnectionRequest request, string? id,
        IReadOnlyList<CapabilityStatus> capabilities, ViewIndex view)
    {
        var from = capabilities.SingleOrDefault(capability => capability.Id == request.FromCapabilityId);
        var to = capabilities.SingleOrDefault(capability => capability.Id == request.ToCapabilityId);
        if (from is null || to is null || from.Id == to.Id || !Kinds.Contains(request.Kind)
            || request.Note?.Length > 1000 || request.SourceKey?.Length > 16384 || request.TargetKey?.Length > 16384
            || !ValidAnchor(from, request.SourceRepositoryId, request.SourceKey, view)
            || !ValidAnchor(to, request.TargetRepositoryId, request.TargetKey, view))
            throw new ArgumentException("Choose different existing capabilities and a valid connection type and source.");
        var sourceRepository = request.SourceRepositoryId ?? (request.SourceKey is { } sourceKey ? view.Node(sourceKey)?.Artifact.RepositoryId : null);
        var targetRepository = request.TargetRepositoryId ?? (request.TargetKey is { } targetKey ? view.Node(targetKey)?.Artifact.RepositoryId : null);
        return Change(request.ExpectedVersion, current =>
        {
            var previous = id is null ? null : current.Connections.SingleOrDefault(connection => connection.Id == id)
                ?? throw new ArgumentException("Connection no longer exists.");
            var connection = new ArchitectureConnection(id ?? Guid.NewGuid().ToString("N"), from.Id, to.Id, request.Kind,
                string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(), sourceRepository,
                request.SourceKey, previous?.CreatedAt ?? DateTimeOffset.UtcNow, targetRepository, request.TargetKey);
            if (current.Connections.Any(existing => existing.Id != id && existing.FromCapabilityId == from.Id
                && existing.ToCapabilityId == to.Id && existing.Kind == connection.Kind && existing.SourceKey == connection.SourceKey
                && existing.SourceRepositoryId == connection.SourceRepositoryId && existing.TargetRepositoryId == connection.TargetRepositoryId
                && existing.TargetKey == connection.TargetKey))
                throw new ArgumentException("This connection already exists.");
            return current.Connections.Where(existing => existing.Id != id).Append(connection).ToArray();
        });
    }

    public ArchitectureConnectionSnapshot Remove(string id, long expectedVersion) => Change(expectedVersion, current =>
    {
        if (!current.Connections.Any(connection => connection.Id == id)) throw new ArgumentException("Connection no longer exists.");
        return current.Connections.Where(connection => connection.Id != id).ToArray();
    });

    private ArchitectureConnectionSnapshot Change(long expectedVersion,
        Func<ArchitectureConnectionSnapshot, IReadOnlyList<ArchitectureConnection>> update)
    {
        RejectLink(_directory);
        Directory.CreateDirectory(_directory);
        var lockPath = FilePath + ".lock";
        RejectLink(lockPath);
        using var gate = AcquireLock(lockPath);
        var current = Read();
        if (current.Version != expectedVersion) throw new ArchitectureConnectionConflictException();
        var next = new ArchitectureConnectionSnapshot(checked(current.Version + 1), update(current));
        if (next.Connections.Count > 1000) throw new ArgumentException("Connection limit reached.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(next, Json);
        if (bytes.Length > 1024 * 1024) throw new ArgumentException("Connection catalogue exceeds its limit.");
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, FilePath, overwrite: true);
            return next;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public ArchitectureGraph Apply(ArchitectureGraph graph, IReadOnlyList<CapabilityStatus> capabilities)
    {
        if (graph.Level is not ("landscape" or "system")) return graph;
        var nodes = graph.Nodes.ToList();
        var edges = graph.Edges.ToList();
        foreach (var connection in Read().Connections)
        {
            var from = capabilities.SingleOrDefault(capability => capability.Id == connection.FromCapabilityId);
            var to = capabilities.SingleOrDefault(capability => capability.Id == connection.ToCapabilityId);
            if (from is null || to is null) continue;
            var sources = graph.Level == "landscape" ? new[] { "cap:" + from.Id }
                : from.Repositories.Where(repository => connection.SourceRepositoryId is null || repository.Id == connection.SourceRepositoryId)
                    .Select(repository => "repo:" + repository.Id).ToArray();
            foreach (var source in sources.Where(source => graph.Nodes.Any(node => node.Id == source)))
            {
                var target = graph.Level == "system" && connection.TargetRepositoryId is { } targetRepository
                    ? "repo:" + targetRepository : "cap:" + to.Id;
                if (!nodes.Any(node => node.Id == target))
                    nodes.Add(connection.TargetRepositoryId is { } repository && graph.Level == "system"
                        ? new(target, "repository", repository, to.Name, null, connection.TargetKey, repository,
                            new Drill("system", to.Id), ["manual target"])
                        : new(target, "capability", to.Name, to.Description, null, null, null,
                            new Drill("system", to.Id), ["manual target"]));
                edges.Add(new("manual:" + connection.Id + (graph.Level == "system" ? ":" + source : ""), source, target,
                    connection.Kind, connection.Kind + " (manual)", 1, "human", [], connection.Id, connection.Note));
            }
        }
        return graph with { Nodes = nodes, Edges = edges };
    }

    private static bool ValidAnchor(CapabilityStatus capability, string? repository, string? key, ViewIndex view)
    {
        if (repository is not null && !capability.Repositories.Any(item => item.Id == repository)) return false;
        if (key is null) return true;
        return view.Node(key) is { } node && capability.Repositories.Any(item => item.Id == node.Artifact.RepositoryId)
            && (repository is null || repository == node.Artifact.RepositoryId);
    }

    private static FileStream AcquireLock(string path)
    {
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new ArchitectureConnectionConflictException(); }
    }

    private static void RejectLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null)
            throw new IOException("Connection storage must not be a symbolic link.");
    }
}

internal static class ArchitectureConnectionsApi
{
    public static void MapArchitectureConnections(this IEndpointRouteBuilder api)
    {
        api.MapGet("/architecture/connections", (ExplorerOptions options) => Respond(() =>
            Results.Ok(new ArchitectureConnections(options.CapabilitiesFile).Read())));
        api.MapPost("/architecture/connections", (ExplorerOptions options, ExplorerState state, ArchitectureConnectionRequest body, HttpRequest request) =>
            Save(options, state, body, request, null));
        api.MapPut("/architecture/connections/{id}", (ExplorerOptions options, ExplorerState state, string id, ArchitectureConnectionRequest body, HttpRequest request) =>
            Save(options, state, body, request, id));
        api.MapDelete("/architecture/connections/{id}", (ExplorerOptions options, string id, long expectedVersion, HttpRequest request) => Respond(() =>
            Allowed(request) ? Results.Ok(new ArchitectureConnections(options.CapabilitiesFile).Remove(id, expectedVersion)) : Denied()));
    }

    private static IResult Save(ExplorerOptions options, ExplorerState state, ArchitectureConnectionRequest body, HttpRequest request, string? id) => Respond(() =>
    {
        if (!Allowed(request)) return Denied();
        if (body.View?.Length > 8192) throw new ArgumentException("Revision selection is too long.");
        state.Refresh();
        var snapshot = state.Current;
        return Results.Ok(new ArchitectureConnections(options.CapabilitiesFile).Save(body, id, snapshot.Capabilities.List(), snapshot.Index.View(body.View)));
    });

    private static bool Allowed(HttpRequest request) => LocalRequestGuard.Allowed(request)
        && request.HasJsonContentType() && request.Headers["X-CE-Architecture-Connections"] == "1";
    private static IResult Denied() => Results.Json(new { error = "Connections require a local same-origin JSON request." }, statusCode: 403);
    private static IResult Respond(Func<IResult> action)
    {
        try { return action(); }
        catch (ArchitectureConnectionConflictException error) { return Results.Json(new { error = error.Message }, statusCode: 409); }
        catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
        catch (Exception error) when (error is IOException or JsonException or OverflowException or UnauthorizedAccessException)
        { return Results.Json(new { error = "Connection storage is unavailable or invalid; existing mappings were preserved." }, statusCode: 503); }
    }
}