using System.Text.Json;
using CapabilityExplorer.Contracts;

namespace CapabilityExplorer.Explorer;

public sealed partial class PostgresExplorerQueries
{
    private static readonly JsonSerializerOptions IntegrationJson = new(JsonSerializerDefaults.Web);
    private const string IntegrationEdges = """
        , integration_edges as (
          select e.*,a.repository_id,a.commit_sha from selected a join edge e on e.artifact_id=a.id
          where e.relation in ('REQUESTS','PUBLISHES','CONSUMES','READS','WRITES','MAPS_TO','EXPOSES','HANDLES')
            or starts_with(e.to_key,'chan|') or starts_with(e.to_key,'sql|')
          order by e.artifact_id,e.local_id collate "C" limit 1001
        )
        """;

    public async Task<Integrations> IntegrationsAsync(ExplorerQueryContext context, IReadOnlyList<MarkDto> marks,
        CancellationToken cancellationToken = default)
    {
        var observations = await IntegrationEdgesAsync(context, cancellationToken);
        var nodes = await IntegrationNodesAsync(context, cancellationToken);
        var byKey = nodes.ToDictionary(n => n.Key, StringComparer.Ordinal);
        SymbolRef Ref(string key) => byKey.TryGetValue(key, out var n)
            ? new(key, n.Name, n.Kind, true, n.RepositoryId)
            : new(key, Map.DisplayName(key), null, false, SymbolKey.OwningRepository(key));
        var edges = observations.Select(o => new IntegrationEdge(new EdgeDto(o.Relation, Ref(o.From),
            o.To is null ? null : Ref(o.To), o.Derivation, o.Resolution, o.UnresolvedReason, o.Rule,
            o.Props, o.RepositoryId, o.CommitSha, o.Evidence), o.Binding,
            o.Evidence.Select(e => e.Path).FirstOrDefault())).ToList();
        return IntegrationGraph.Build(nodes, edges, marks);
    }

    private async Task<List<NodeDto>> IntegrationNodesAsync(ExplorerQueryContext context, CancellationToken ct)
    {
        await using var command = Scoped(context, Selected + IntegrationEdges + $$"""
            , candidates as (
              select distinct on (n.symbol_key collate "C") n.*,a.repository_id,a.commit_sha,a.unit_name,a.unit_path
              from selected a join node n on n.artifact_id=a.id
              where n.node_kind in ('Service','Endpoint','Table','Routine') or starts_with(n.symbol_key,'chan|')
                or exists(select 1 from integration_edges e where e.from_key=n.symbol_key or e.to_key=n.symbol_key)
                or exists(select 1 from analysis_snapshot_binding sb join bound_edge b on b.snapshot_id=sb.binding_snapshot_id
                  where sb.snapshot_id=@snapshot and b.to_key=n.symbol_key and exists(
                    select 1 from integration_edges e where e.artifact_id=b.artifact_id and e.local_id=b.original_local_id))
              order by n.symbol_key collate "C",a.unit_path collate "C",a.id
              limit 1001
            ), projection as (
              select jsonb_build_object('key',n.symbol_key,'kind',n.node_kind,'name',n.name,'signature',n.signature,
                'generated',n.generated,'props',n.props,'repositoryId',n.repository_id,'commitSha',n.commit_sha::text,
                'unit',n.unit_name,'artifactId',n.artifact_id,'evidence',{{IntegrationEvidence("n")}}) as payload,
                n.symbol_key,n.artifact_id
              from candidates n
            )
            select case when artifact_id<=2147483647 and octet_length(payload::text)<=8192 then payload::text else null end
            from projection order by symbol_key collate "C"
            """);
        var rows = new List<NodeDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await ReadProjectionAsync(reader, ct))
        {
            if (rows.Count == 1000) throw new InvalidOperationException("integration node budget exceeded");
            rows.Add(JsonSerializer.Deserialize<NodeDto>(reader.GetString(0), IntegrationJson)
                ?? throw new InvalidOperationException("integration node projection is invalid"));
        }
        return rows;
    }

    private async Task<List<IntegrationObservation>> IntegrationEdgesAsync(ExplorerQueryContext context, CancellationToken ct)
    {
        await using var command = Scoped(context, Selected + IntegrationEdges + $$"""
            , projection as (
              select e.artifact_id,e.local_id,jsonb_build_object(
                'from',e.from_key,'to',e.to_key,'relation',e.relation,'derivation',e.derivation,'resolution',e.resolution,
                'unresolvedReason',e.unresolved_reason,'rule',case when e.rule is null then null
                  else (e.rule->>'name')||' '||(e.rule->>'version') end,'props',e.props,
                'repositoryId',e.repository_id,'commitSha',e.commit_sha::text,'evidence',{{IntegrationEvidence("e")}},
                'binding',case when b.to_key is null then null else jsonb_build_object(
                  'key',b.to_key,'hint',b.hint,'target',b.target,'note',b.note,'bound',b.status='bound') end) as payload
              from integration_edges e left join lateral (
                select b.* from analysis_snapshot_binding sb join bound_edge b on b.snapshot_id=sb.binding_snapshot_id
                where sb.snapshot_id=@snapshot and b.artifact_id=e.artifact_id and b.original_local_id=e.local_id
                order by b.local_id collate "C" limit 1
              ) b on true
            )
            select case when octet_length(payload::text)<=8192 then payload::text else null end
            from projection order by artifact_id,local_id collate "C"
            """);
        var rows = new List<IntegrationObservation>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await ReadProjectionAsync(reader, ct))
        {
            if (rows.Count == 1000) throw new InvalidOperationException("integration edge budget exceeded");
            rows.Add(JsonSerializer.Deserialize<IntegrationObservation>(reader.GetString(0), IntegrationJson)
                ?? throw new InvalidOperationException("integration edge projection is invalid"));
        }
        return rows;
    }

    private static string IntegrationEvidence(string owner) => $$"""
        coalesce((select jsonb_agg(jsonb_build_object('id',v.local_id,'repositoryId',v.repository_id,
          'commitSha',v.commit_sha::text,'path',v.path,'startLine',v.start_line,'startColumn',v.start_column,
          'endLine',v.end_line,'endColumn',v.end_column) order by v.local_id collate "C")
          from evidence v where v.artifact_id={{owner}}.artifact_id and v.local_id=any({{owner}}.evidence)), '[]'::jsonb)
        """;

    private sealed record IntegrationObservation(string From, string? To, string Relation, string Derivation,
        string Resolution, string? UnresolvedReason, string? Rule, JsonElement? Props, string RepositoryId,
        string CommitSha, IReadOnlyList<EvidenceDto> Evidence, IntegrationBinding? Binding);
}
