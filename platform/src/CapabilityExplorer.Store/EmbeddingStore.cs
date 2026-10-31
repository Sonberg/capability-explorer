using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace CapabilityExplorer.Store;

public sealed record EmbeddingInput(string SymbolKey, string RepositoryId, string NodeKind, string Name, string Text, string TextSha256);

public sealed record SimilarSymbol(string SymbolKey, string Name, string NodeKind, string RepositoryId, double Score);

/// <summary>
/// Vectors for functions, endpoints, routines and calculations, compared by cosine distance (pgvector).
/// The model is not called from here: callers pass the embedding function, so the store stays model-free.
/// </summary>
public sealed partial class EmbeddingStore(NpgsqlDataSource dataSource)
{
    private const int MaxTextLength = 6000;

    /// <summary>Latest occurrence of every in-scope symbol whose text changed (or was never embedded) for the model.</summary>
    public async Task<IReadOnlyList<EmbeddingInput>> PendingAsync(string model, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select distinct on (n.symbol_key)
                   n.symbol_key, a.repository_id, n.node_kind, n.name, n.signature,
                   (select array_agg(distinct e.relation || ' ' || e.to_key order by e.relation || ' ' || e.to_key)
                      from edge e
                     where e.artifact_id = n.artifact_id and e.from_key = n.symbol_key and e.to_key is not null
                       and e.relation in ('CALLS', 'READS', 'WRITES', 'REQUESTS', 'PUBLISHES', 'CONSUMES')) as targets,
                   (select c.body -> 'expr' from calculation c
                     where c.artifact_id = n.artifact_id and c.symbol_key = n.symbol_key limit 1)::text as expr,
                   x.text_sha256
              from node n
              join artifact a on a.id = n.artifact_id
              left join embedding x on x.symbol_key = n.symbol_key and x.model = $1
             where not n.generated
               and (n.node_kind in ('Method', 'Function', 'Endpoint', 'Routine')
                    or exists (select 1 from calculation c where c.artifact_id = n.artifact_id and c.symbol_key = n.symbol_key))
             order by n.symbol_key, n.artifact_id desc
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(model);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var pending = new List<EmbeddingInput>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = reader.GetString(0);
            var text = Text(key, reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? [] : reader.GetFieldValue<string[]>(5), reader.IsDBNull(6) ? null : reader.GetString(6));
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            if (reader.IsDBNull(7) || reader.GetString(7) != hash)
            {
                pending.Add(new EmbeddingInput(key, reader.GetString(1), reader.GetString(2), reader.GetString(3), text, hash));
            }
        }

        return pending;
    }

    public async Task UpsertAsync(string model, IReadOnlyList<(EmbeddingInput Input, float[] Vector)> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var batch = new NpgsqlBatch(connection);
        foreach (var (input, vector) in rows)
        {
            var upsert = new NpgsqlBatchCommand("""
                insert into embedding (symbol_key, model, text_sha256, repository_id, node_kind, name, vec)
                values ($1, $2, $3, $4, $5, $6, $7::real[]::vector)
                on conflict (symbol_key, model) do update
                   set text_sha256 = excluded.text_sha256, repository_id = excluded.repository_id, node_kind = excluded.node_kind,
                       name = excluded.name, vec = excluded.vec, embedded_at = now()
                """);
            upsert.Parameters.AddWithValue(input.SymbolKey);
            upsert.Parameters.AddWithValue(model);
            upsert.Parameters.AddWithValue(input.TextSha256);
            upsert.Parameters.AddWithValue(input.RepositoryId);
            upsert.Parameters.AddWithValue(input.NodeKind);
            upsert.Parameters.AddWithValue(input.Name);
            upsert.Parameters.AddWithValue(vector);
            batch.BatchCommands.Add(upsert);
        }

        if (batch.BatchCommands.Count > 0)
        {
            await batch.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>Embeds everything pending in batches; returns how many symbols were (re-)embedded.</summary>
    public async Task<int> EmbedPendingAsync(
        string model,
        Func<IReadOnlyList<string>, CancellationToken, Task<float[][]>> embed,
        int batchSize = 64,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(embed);
        var pending = await PendingAsync(model, cancellationToken);
        foreach (var chunk in pending.Chunk(batchSize))
        {
            var vectors = await embed(chunk.Select(p => p.Text).ToList(), cancellationToken);
            await UpsertAsync(model, chunk.Zip(vectors).ToList(), cancellationToken);
        }

        return pending.Count;
    }

    /// <summary>Nearest symbols to an already-embedded symbol. No model call: its stored vector is the query.</summary>
    public Task<IReadOnlyList<SimilarSymbol>> SimilarAsync(string symbolKey, string model, int limit, CancellationToken cancellationToken = default) =>
        QueryAsync("""
            select e.symbol_key, e.name, e.node_kind, e.repository_id, 1 - (e.vec <=> s.vec)
              from embedding s join embedding e on e.model = s.model and e.symbol_key <> s.symbol_key
             where s.symbol_key = $1 and s.model = $2
             order by e.vec <=> s.vec
             limit $3
            """, [symbolKey, model, limit], cancellationToken);

    public Task<IReadOnlyList<SimilarSymbol>> NearestAsync(float[] vector, string model, int limit, CancellationToken cancellationToken = default) =>
        QueryAsync("""
            select symbol_key, name, node_kind, repository_id, 1 - (vec <=> $1::real[]::vector)
              from embedding
             where model = $2
             order by vec <=> $1::real[]::vector
             limit $3
            """, [vector, model, limit], cancellationToken);

    private async Task<IReadOnlyList<SimilarSymbol>> QueryAsync(string sql, object[] parameters, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql);
        foreach (var p in parameters)
        {
            command.Parameters.AddWithValue(p);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var hits = new List<SimilarSymbol>();
        while (await reader.ReadAsync(cancellationToken))
        {
            hits.Add(new SimilarSymbol(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetDouble(4)));
        }

        return hits;
    }

    /// <summary>
    /// What a symbol does, without where it lives: repository and unit are left out of the text so the same logic
    /// in two repositories embeds alike. Symbol keys inside it are reduced to their last path and name.
    /// </summary>
    internal static string Text(string symbolKey, string kind, string name, string? signature, IReadOnlyList<string> targets, string? expr)
    {
        var text = new StringBuilder($"{kind} {name}\n");
        if (signature is not null)
        {
            text.Append(signature).Append('\n');
        }

        text.Append("in ").Append(Location(symbolKey)).Append('\n');
        foreach (var target in targets)
        {
            var space = target.IndexOf(' ', StringComparison.Ordinal);
            text.Append(target[..space].ToLowerInvariant()).Append(' ').Append(Location(target[(space + 1)..])).Append('\n');
        }

        if (expr is not null)
        {
            text.Append("formula ").Append(KeyInJson().Replace(expr, m => Location(m.Groups[1].Value)));
        }

        var s = text.ToString();
        return s.Length <= MaxTextLength ? s : s[..MaxTextLength];
    }

    /// <summary>"ts|repo|unit|path|member#value" → "path member"; otherwise the last segment ("M:Ns.Type.Method(...)" → "Ns.Type.Method(...)").</summary>
    private static string Location(string key)
    {
        var segments = key.Split('|');
        var last = segments[^1];
        return segments.Length >= 5 ? $"{segments[^2]} {last.Split('#')[0]}"
            : last.Length > 2 && last[1] == ':' ? last[2..]
            : last;
    }

    [GeneratedRegex("\"([a-z]+\\|[^\"]+)\"")]
    private static partial Regex KeyInJson();
}
