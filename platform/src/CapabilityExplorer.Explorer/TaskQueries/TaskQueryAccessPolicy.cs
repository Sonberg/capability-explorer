using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CapabilityExplorer.Explorer;

/// <summary>TaskQueries:Operations[] / TaskQueries:Repositories[] from configuration (appsettings, env
/// TaskQueries__Operations__0, command line). Deny by default: a missing or empty grant list means every call
/// is denied with no snapshot, catalog or source read.</summary>
public sealed class TaskQueryAccessPolicy
{
    private readonly HashSet<string> _operations;
    private readonly HashSet<string> _repositories;

    public TaskQueryAccessPolicy(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("TaskQueries");
        _operations = (section.GetSection("Operations").Get<string[]>() ?? [])
            .Where(op => TaskQueryOperations.All.Contains(op)).ToHashSet(StringComparer.Ordinal);
        _repositories = (section.GetSection("Repositories").Get<string[]>() ?? [])
            .Where(repo => repo.Length is > 0 and <= 100).ToHashSet(StringComparer.Ordinal);
        Digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            operations = _operations.Order(StringComparer.Ordinal),
            repositories = _repositories.Order(StringComparer.Ordinal),
        }))));
    }

    /// <summary>False when either grant list is empty: the operator has not opted in to any task query.</summary>
    public bool Configured => _operations.Count > 0 && _repositories.Count > 0;

    public string Digest { get; }

    public bool AllowsOperation(string operation) => Configured && _operations.Contains(operation);

    public bool AllowsRepository(string repositoryId) => Configured && _repositories.Contains(repositoryId);

    /// <summary>Removes ungranted repositories from a selection before any read; the removed repositories are
    /// reported through coverage as "denied", never silently dropped.</summary>
    public (IReadOnlyDictionary<string, string> Allowed, IReadOnlyList<string> Denied) Partition(IReadOnlyDictionary<string, string> selection)
    {
        var allowed = new Dictionary<string, string>(StringComparer.Ordinal);
        var denied = new List<string>();
        foreach (var (repo, sha) in selection)
        {
            if (AllowsRepository(repo)) allowed[repo] = sha;
            else denied.Add(repo);
        }
        denied.Sort(StringComparer.Ordinal);
        return (allowed, denied);
    }
}
