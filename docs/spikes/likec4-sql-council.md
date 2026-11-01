# SQL resource viewpoints: SME council findings

Status: local spike decision, 2026-11-01. Three independent read-only reviews covered SQL identity/provenance, architecture modeling, and diagram viewpoints/UX. This does not change the ce.v1 extraction contract or establish deployment facts.

## Agreed findings

The fixture's five `datastore` nodes are **not five databases or five proven distinct deployed tables**. The existing Explorer adapter uses this category for SQL object references and collapses column keys to their owning object.

| Exact store reference | Source evidence | Overview label/kind |
| --- | --- | --- |
| `@unbound:api-cs/Sales.Api:conn=Sales` | EF table mapping and reads through the API's logical connection | Sales / API: unresolved database reference |
| `@unbound:worker-cs/Sales.Worker:conn=Sales` | Dapper SELECT and UPDATE object references; no declarations or resolved database target | Sales / Worker: unresolved database reference |
| `@unbound:api-cs/SalesDb:dbproject=SalesDb` | Two declared tables in SQL source | SalesDb / api-cs: SQL source model; deployment unresolved |

Only two object nodes have explicit table declarations in the graph. The remaining three are application references. In particular, SQL SELECT/UPDATE references do not by themselves establish a table rather than a view.

The fixture has loose SQL files, not a demonstrated `.sqlproj`; the extractor can infer the SQL unit name from repository connection configuration. Therefore the UI says **SQL source model**, not a deployed database or necessarily a database-project file.

Both applications' default configuration names `Server=sql-sales;Database=SalesDb`. That supports a future configured-default binding candidate, but the extracted graph still contains scoped unbound identities and the current bindings file does not resolve them. Environment-specific overrides may differ. The spike must not silently promote that candidate to deployed database identity.

## View and shape contract

- Resource overview and data-dependency views collapse each exact SQL store reference into a logical resource. The three scopes remain separate, even where connection names or object names match.
- SQL-object and scope-detail views expand rectangular object references beneath their logical resource. Declared tables are explicitly labeled; unproven object types remain unknown.
- Unresolved database references use component/reference shapes with unresolved descriptions. SQL source models use source-model containers. A database storage cylinder is reserved for a concrete source identity; even then the description does not claim observed deployment.
- Model hierarchy is generated in LikeC4 `model`, with named landscape, resource, object and scoped `views`, following the structure of the user's reference HTML.
- Repository grouping indicates source membership. It does not establish infrastructure hosting or team ownership. SQL scopes remain independent of repository presentation groups by default.
- User-created groups change presentation, never the underlying store identities or evidence. Removing a group leaves every artifact and relation intact.

## Identity and evidence rules

Use the exact `(sql, storeRef)` tuple from `sql|<storeRef>|<schema>.<object>[#column]`, within the current source snapshot. Never merge by `Sales`, `SalesDb`, `dbo.Sales`, or proximity. Parent metadata records the exact scope and source node IDs. Original object and edge metadata remain intact.

LikeC4 aggregates relationships in collapsed views. Keep original declaration, mapping, read/write, certainty and candidate labels in their source relationships; inspect the underlying metadata for original endpoints. A source declaration or routine read is not proof of a live application-to-database connection. This fixture's rollups are tested; arbitrary future mixed-certainty graphs still require a broader evaluation before adopting the adapter in production.

## Evidence inspected

- `schema/README.md`: SQL-object and store identity schemes; scoped unbound references; explicit binding rules.
- `platform/src/CapabilityExplorer.Explorer/Architecture.cs`: resource-key column normalization, `datastore` presentation category, declaration handling and conservative same-name associations.
- `platform/src/CapabilityExplorer.Extractors.CSharp/Rules.cs`: scoped connection references.
- `extractors/ts/src/sql/project.ts` and `extractor.ts`: SQL unit identification and unbound source-model identities.
- `fixtures/src/api-cs/01-base/database/tables/{Sales,ArticleStats}.sql`: CREATE TABLE declarations.
- API `SalesDbContext.cs`, worker `StatsRepository.cs`, and both `appsettings.json` files: mapping, SQL references and configured defaults.
- `spikes/likec4/test/weekly-sales.graph.json`: the exact graph used by the spike, including same-name candidate associations.

The source inspection used pending reconciliation commit `6dc7dfbbac9b4c0e4a71cdef9023e97a3bba4d92`; the snapshot remains bound to the earlier stage fixture baseline documented in `likec4.md`. No bindings, extractors, fixture revisions, or `main` history were modified.
