using System.Text.Json;

namespace CapabilityExplorer.Explorer;

/// <param name="Key">"group|http|Klarna" for a whole integration, otherwise the endpoint, request, channel or table key.</param>
/// <param name="Status">"deprecated" or "obsolete".</param>
public sealed record Mark(string Key, string Status, string Note, DateTimeOffset MarkedAt);

/// <param name="RemovedIn">First analysed commit of <paramref name="RepositoryId"/> without the key, after one that had it.</param>
public sealed record Removal(string RepositoryId, string LastSeen, string RemovedIn, string? Subject);

public sealed record MarkDto(string Key, string Status, string Note, DateTimeOffset MarkedAt, Removal? Removal);

/// <summary>User-written deprecation marks, kept in a small JSON file next to capabilities.json. Marks are keyed without a commit, so they survive re-analysis.</summary>
public sealed class DeprecationStore(string path)
{
    private static readonly object Gate = new(); // ponytail: one process-wide lock, fine for a single-user explorer
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static readonly string[] Statuses = ["deprecated", "obsolete"];

    public IReadOnlyList<Mark> List()
    {
        lock (Gate)
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<Mark>>(File.ReadAllText(path), Json) ?? [] : [];
        }
    }

    public void Upsert(string key, string status, string note)
    {
        lock (Gate)
        {
            var marks = List().Where(m => m.Key != key).Append(new Mark(key, status, note, DateTimeOffset.UtcNow)).OrderBy(m => m.Key, StringComparer.Ordinal).ToList();
            Write(marks);
        }
    }

    public void Delete(string key)
    {
        lock (Gate)
        {
            Write(List().Where(m => m.Key != key).ToList());
        }
    }

    private void Write(List<Mark> marks)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(marks, Json));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Where each marked key disappeared: per repository, the analysed revisions in commit order, and a key is removed when the
    /// newest revision lacks it but an earlier one had it. A key still present in any repository's newest revision is not removed.
    /// </summary>
    public IReadOnlyList<MarkDto> WithRemovals(ExplorerIndex index)
    {
        var marks = List();
        if (marks.Count == 0)
        {
            return [];
        }

        var keysByRevision = new Dictionary<RevisionInfo, HashSet<string>>();
        HashSet<string> Keys(RevisionInfo r)
        {
            if (!keysByRevision.TryGetValue(r, out var set))
            {
                set = [];
                foreach (var a in r.ArtifactIds.Select(id => index.Artifacts[id]))
                {
                    Queries.PresenceKeys(a.Artifact.Nodes, a.Artifact.Edges, set);
                }

                keysByRevision[r] = set;
            }

            return set;
        }

        var repos = index.Revisions.GroupBy(r => r.RepositoryId, StringComparer.Ordinal)
            .Select(g => (Id: g.Key, Revisions: g.OrderBy(r => r.CommitTime ?? DateTimeOffset.MinValue).ToList())).ToList();

        return marks.Select(m =>
        {
            Removal? removal = null;
            foreach (var (id, revisions) in repos)
            {
                var lastSeenAt = revisions.FindLastIndex(r => Keys(r).Contains(m.Key));
                if (lastSeenAt < 0)
                {
                    continue;
                }

                if (lastSeenAt == revisions.Count - 1)
                {
                    removal = null; // still in the newest revision of this repository
                    break;
                }

                var gone = revisions[lastSeenAt + 1];
                removal ??= new Removal(id, revisions[lastSeenAt].CommitSha, gone.CommitSha, gone.Subject);
            }

            return new MarkDto(m.Key, m.Status, m.Note, m.MarkedAt, removal);
        }).ToList();
    }
}
