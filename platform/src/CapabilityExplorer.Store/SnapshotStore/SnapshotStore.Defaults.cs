using Npgsql;

namespace CapabilityExplorer.Store;

public sealed partial class SnapshotStore
{
    private static async Task<Dictionary<string,string>> ResolveDefaultsAsync(NpgsqlConnection c,NpgsqlTransaction tx,
        long[] artifactIds,IReadOnlyDictionary<string,string>? requested,CancellationToken ct)
    {
        if(requested?.Count>MaximumArtifacts) throw new ArgumentException("Too many default repositories",nameof(requested));
        if(requested is not null && requested.Any(p=>string.IsNullOrWhiteSpace(p.Key) || p.Value is not {Length:40}
            || !p.Value.All(ch=>char.IsAsciiHexDigit(ch) && !char.IsAsciiLetterUpper(ch))))
            throw new ArgumentException("Defaults require repository IDs and full lowercase revision SHAs",nameof(requested));
        var repos=requested?.Keys.ToArray() ?? [];
        var shas=requested?.Values.ToArray() ?? [];
        await using var command=new NpgsqlCommand("""
            select a.repository_id,count(distinct a.commit_sha),min(a.commit_sha::text),
              coalesce(bool_or(a.commit_sha::text=d.sha),false)
            from artifact a left join unnest($2::text[],$3::text[]) d(repo,sha) on d.repo=a.repository_id
            where a.id=any($1) group by a.repository_id order by a.repository_id collate "C"
            """,c,tx);
        command.Parameters.AddWithValue(artifactIds);
        command.Parameters.AddWithValue(repos);
        command.Parameters.AddWithValue(shas);
        var defaults=new Dictionary<string,string>(StringComparer.Ordinal);
        await using var reader=await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))
        {
            var repo=reader.GetString(0);
            if(requested is not null && requested.TryGetValue(repo,out var sha))
            {
                if(!reader.GetBoolean(3)) throw new ArgumentException("Default revision is outside selected artifacts",nameof(requested));
                defaults.Add(repo,sha);
            }
            else if(reader.GetInt64(1)==1) defaults.Add(repo,reader.GetString(2));
            else throw new ArgumentException("Repositories with multiple revisions require an explicit default",nameof(requested));
        }
        if(repos.Any(repo=>!defaults.ContainsKey(repo))) throw new ArgumentException("Default repository is outside selected artifacts",nameof(requested));
        return defaults;
    }

    private static async Task<bool> DefaultsMatchAsync(NpgsqlConnection c,NpgsqlTransaction tx,Guid snapshot,
        Dictionary<string,string> expected,CancellationToken ct)
    {
        await using var command=new NpgsqlCommand("select repository_id,commit_sha::text from snapshot_revision where snapshot_id=$1 and is_default",c,tx);
        command.Parameters.AddWithValue(snapshot);
        await using var reader=await command.ExecuteReaderAsync(ct);
        var count=0;
        while(await reader.ReadAsync(ct))
        {
            count++;
            if(!expected.TryGetValue(reader.GetString(0),out var sha) || sha!=reader.GetString(1)) return false;
        }
        return count==expected.Count;
    }
}
