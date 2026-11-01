using CapabilityExplorer.Contracts;
using Npgsql;

namespace CapabilityExplorer.Store;

public sealed partial class ArtifactImporter
{
    private static async Task ValidateStagedAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
        CompleteRecord complete, int lastLine, CancellationToken ct)
    {
        await Exec(connection, tx, """
            create index on ce_import_record(id);
            create index on ce_import_record(kind,(body->>'symbolKey'));
            analyze ce_import_record;
            """, ct);
        // Only database staging holds global identity/reference sets. Return at most one failure.
        await using (var check = new NpgsqlCommand("""
            select line,message from (
              select min(line) line,'duplicate id '||id message from ce_import_record where id is not null group by id having count(*)>1
              union all
              select line,kind||' id has invalid prefix' from ce_import_record
                where id is not null and not starts_with(id,case kind when 'node' then 'n:' when 'edge' then 'e:'
                  when 'evidence' then 'ev:' when 'calculation' then 'c:' when 'diagnostic' then 'd:' end)
              union all
              select min(line),'duplicate node symbolKey '||(body->>'symbolKey') from ce_import_record where kind='node'
                group by body->>'symbolKey' having count(*)>1
              union all
              select r.line,'unknown evidence reference '||e.value from ce_import_record r
                cross join lateral jsonb_array_elements_text(coalesce(r.body->'evidence','[]')) e
                where not exists(select 1 from ce_import_record v where v.kind='evidence' and v.id=e.value)
              union all
              select r.line,'unknown attribute evidence reference '||e.value from ce_import_record r
                cross join lateral jsonb_each(coalesce(r.body->'attributes','{}')) a
                cross join lateral jsonb_array_elements_text(coalesce(a.value->'evidence','[]')) e
                where r.kind='calculation' and not exists(select 1 from ce_import_record v where v.kind='evidence' and v.id=e.value)
              union all
              select r.line,'edge source is not declared in this artifact' from ce_import_record r where r.kind='edge'
                and not exists(select 1 from ce_import_record n where n.kind='node' and n.body->>'symbolKey'=r.body->>'from')
              union all
              select r.line,'calculation symbol is not declared in this artifact' from ce_import_record r where r.kind='calculation'
                and not exists(select 1 from ce_import_record n where n.kind='node' and n.body->>'symbolKey'=r.body->>'symbolKey')
            ) failures order by line,message limit 1
            """, connection, tx))
        {
            await using var reader = await check.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) throw Invalid(reader.GetInt32(0), reader.GetString(1));
        }
        await using (var edges = new NpgsqlCommand("""
            select e.line,e.body->>'relation',f.body->>'nodeKind',t.body->>'nodeKind' from ce_import_record e
              join ce_import_record f on f.kind='node' and f.body->>'symbolKey'=e.body->>'from'
              left join ce_import_record t on t.kind='node' and t.body->>'symbolKey'=e.body->>'to'
              where e.kind='edge'
            """, connection, tx))
        {
            await using var reader = await edges.ExecuteReaderAsync(ct);
            var catalog = ContractSchema.Instance.Relations;
            while (await reader.ReadAsync(ct))
            {
                var relation = reader.GetString(1);
                if (!catalog.AllowsFrom(relation, reader.GetString(2))) throw Invalid(reader.GetInt32(0), $"{relation} may not start at a {reader.GetString(2)}");
                if (!reader.IsDBNull(3) && !catalog.AllowsTo(relation, reader.GetString(3))) throw Invalid(reader.GetInt32(0), $"{relation} may not end at a {reader.GetString(3)}");
            }
        }
        await using var counts = new NpgsqlCommand("""
            select count(*) filter(where kind='node')::int,count(*) filter(where kind='edge')::int,
                   count(*) filter(where kind='evidence')::int,count(*) filter(where kind='calculation')::int,
                   count(*) filter(where kind='diagnostic')::int,
                   coalesce(bool_or(kind='diagnostic' and (body->>'affectsCoverage')::boolean),false),
                   count(*) filter(where kind='edge' and body->>'resolution'='unresolved')::int from ce_import_record
            """, connection, tx);
        await using var result = await counts.ExecuteReaderAsync(ct);
        await result.ReadAsync(ct);
        var errors = ArtifactValidator.ValidateCompletion(complete,
            new Counts(result.GetInt32(0), result.GetInt32(1), result.GetInt32(2), result.GetInt32(3), result.GetInt32(4)),
            result.GetBoolean(5), result.GetInt32(6));
        if (errors.Count > 0) throw Invalid(lastLine, errors[0]);
    }
}
