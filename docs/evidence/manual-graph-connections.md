# Manual Capability Connections

Date: 2026-11-04. Request: connect an integration or capability to another
capability directly from the architecture graph.

## Behavior

The graph toolbar's connection action and the selected node/edge panel open
a capability connection editor. The source capability is preselected from the
selection where possible; source repository and symbol references are retained
when they belong to that capability. The editor supports HTTP, messaging,
navigation, data, schedule/cron and dependency relationships, optional notes,
editing and removal. Persisted links appear in landscape and system views and
are explicitly labelled manual. They do not rewrite extractor artifacts,
pretend an endpoint was resolved, or claim runtime proof. Original extracted
connections and their evidence remain unchanged.

Both connection ends now have repository selectors and searchable analyzed-node
selectors. Node choices use the existing capability/repository/revision-scoped
search product; a repository can also be selected without a node. Selected
anchors are persisted and validated against capability membership and exact
repository ownership. System graphs connect the selected repositories;
landscape graphs continue to aggregate to capabilities. Automatic generation
refreshes preserve unsaved editor selections; explicit scope changes reset them.

File-backed Explorer persists the overlay at
`.sandbox-cache/architecture-connections.json` beside the configured capability
catalogue. It is local workspace data, not a database publication. The existing
database-only Explorer does not gain a migrated architecture product from this
feature. Existing missing extraction evidence remains missing, not proved by
the presence of a manual edge.

Writes require local same-origin JSON requests and the explicit
`X-CE-Architecture-Connections: 1` header. Capability membership, distinct
source/target, relationship type, note/source bounds and duplicate mappings are
validated. An expected-version check and exclusive persistent lock prevent lost
updates across clients/processes. New mapping files are owner-readable/writable
only on Unix; writes are bounded, flushed and atomically renamed. A competing
writer receives 409; a storage failure does not replace existing mappings.

## Executable Scenarios

API tests in
[ArchitectureConnectionsTests](../../platform/tests/CapabilityExplorer.Explorer.Tests/ArchitectureConnectionsTests.cs):

- Given capabilities, when connected, the manual link persists, appears on the
  landscape as human-derived, and can be removed.
- Given mappings, invalid capabilities, stale writes and cross-origin requests
  are rejected without changing existing data.
- Given an edited mapping, a new host instance reloads the same identity, type,
  note and version from a private file.
- Given another writer holding the lock, saving returns a conflict and leaves
  the catalogue unchanged. The held lock is a deterministic concurrency barrier.

Observed first API red: expected 200/400, received 405 before the endpoints
existed. The privacy and competing-lock scenarios independently failed with
group/other read permissions and 503 respectively before their fixes. The
same scenarios pass after implementation.

```sh
cd platform
dotnet test --no-restore tests/CapabilityExplorer.Explorer.Tests/CapabilityExplorer.Explorer.Tests.csproj --filter FullyQualifiedName~ArchitectureConnectionsTests --nologo
dotnet test --no-restore tests/CapabilityExplorer.Explorer.Tests/CapabilityExplorer.Explorer.Tests.csproj --nologo
```

Results before the node-picker extension: 4/4 focused API cases and 304/304
complete Explorer cases passed. The extension adds two focused API scenarios
for persisted source/target anchors, repository-level edges and invalid target
ownership; all 6/6 focused cases pass. Its initial red observations were missing
target fields and an invalid target returning 200 instead of 400. A test guard
was corrected for optional manual metadata on ordinary extracted edges.
The final complete Explorer run after the anchor extension passed 306/306 cases.
One final malformed-catalogue scenario first returned 500, then passed after
null-row validation and explicit storage-error handling. The overlay catalogue
returns 503 without overwriting invalid data, while native graph browsing stays
available with an unavailable-overlay note. Final focused cases pass 7/7 and
the complete Explorer suite passes 307/307.

Typed browser scenarios in
[the UI browser harness](../../platform/src/CapabilityExplorer.Explorer/client/test/architecture-connections.browser.ts)
exercise create, edit, reload, removal, nonblank graph rendering and no page
overflow at 1440x1000 and 390x844. The API is controlled in these browser
scenarios; actual persistence and cross-origin/concurrency behavior are proved
separately by the real API/host tests above. Browser screenshots are retained
under `.sandbox-cache/integration-audit-evidence/manual-connections-*.png`.
Browser controls first failed because the connection action did not exist.
Harness setup needed explicit TypeScript 7 file compilation, Node typings and
Playwright's CommonJS import. Later rendering assertions were corrected to
wait for the graph worker's `data-status="ready"` signal. These tooling and
test corrections are not represented as unchanged behavior red/green evidence.

```sh
cd platform/src/CapabilityExplorer.Explorer/client
npm run build
npm run test:graph-connections
```

Results: TypeScript 7 build and both desktop/mobile browser scenarios pass.
The extended scenarios select source/target repositories and analyzed nodes,
reload/edit those anchors, and trigger an actual metadata-generation response
while the draft is open. They first exposed an editor remount losing selections;
the unchanged assertions pass with the generation-independent editor identity.
Editor screenshots are retained as `manual-editor-1440.png` and
`manual-editor-390.png` alongside the graph screenshots.
On mobile, the active editor precedes a compact 420px diagram instead of being
placed after the normal 1,100px graph. The browser test first failed on editor
ordering and passes after the editing-only responsive layout change.
The production server serves copied assets from its output directory; a .NET
build is required after the UI build before validating those assets.

## Toolchain And Limits

Final owner-only implementation/configuration snapshot:
`.sandbox-cache/integration-audit-evidence/final-source.tgz`, SHA-256
`bdf5fcd4ff86c7d01fd3abc1f470896be5d0d9ce5b3d72e5fc000bffcca5765e`.
Some graph UI/API increments have observed failing commands but no retained
immutable pre-edit source snapshot. They are not represented as fully
source-bound MADR-0032 history; missing historical receipts were not reconstructed
after implementation. Final executable results and source identity are retained,
and release acceptance/full gates remain separate from this local qualification.

This touched UI scope now locks TypeScript 7.0.2 and Node types 22.19.0;
Lucide supplies the connection, edit, save and remove icons. Vite's normal
client declarations qualify CSS side-effect imports under TypeScript 7.
`npm install` replaced this UI's old TypeScript installed output. Extractor
TypeScript 5.9.3 and shared npm downloads remain owned by their other callers
and are retained; no global cache purge or reclaimed-space claim is made.
The application entry bundle remains about 487 kB; connection code stays in the
lazy architecture chunk. `npm audit` reported zero vulnerabilities during install.

Full Docker-only CI was not executed. The broader source/integration comparison
is separate. Its interrupted publications were independently owner-reconciled
with original evidence retained; they were not reset by this graph feature.
All available capabilities subsequently refreshed successfully, including the
final source-citation correction refresh tracked in the private audit. Detailed source
findings remain under `.sandbox-cache/integration-audit-evidence`, not an
automatic export from the graph editor.