# Live LikeC4 replacement execution

Status: implementation and browser refinement in progress; final exact-source
act/security and stage-alignment gates remain pending. No PR or release is claimed.

## Contract and source

Approved Brain Prompt: replace the production Architecture canvas with the LikeC4
spike, driven by selected analyzed inputs, preserving the PR #20 glossary canvas.
PR #20 merged into stage at `9aa05475848d27328a2253cb66db87fb77975ffe` before work began.
Integration uses `feat/live-likec4-architecture`; target is exclusively
`stage-1-contract-and-fixtures`. Issue #3 comment 6557010425 records ownership.
No main change, owner merge, auto-merge or release is authorized.

Three native `gpt-6.1-sol` / high workers used isolated worktrees for model projection,
shared canvas and independent browser validation. The coordinator owns application
wiring, dependencies, Makefile, MADR and integration. No two writers shared a worktree.
The Brain grounding `sparc/capability-cards.md#sparc` supports the phased methodology;
it does not prove runtime APIs or gates. No inference-provider calls or new services
were introduced. Native session cost is unavailable, so no savings amount is claimed.

## Implementation

- `architecture-types.ts` separates evidence contracts from the canvas. Both
  Architecture and Glossary consume the same live component and resolve callbacks
  against original IDs before presenting exact evidence.
- `spikes/likec4/src/live-*` is the shared renderer/model implementation. The existing
  standalone spike remains a fixture regression harness. Production no longer copies
  its compiled fixture site into wwwroot or offers an iframe experiment tab.
- A pure validated projection creates typed LikeC4 parsed data and canonical `.c4`
  from the same input. Public `@likec4/core` computation plus the public Graphviz WASM adapter creates
  live layouted models in a browser worker. Dagre is a synchronous test adapter;
  production compilation does not fall back to it. No Node compiler/private runtime API executes
  in the browser; the real Node compiler validates export parity in sandbox tests.
- Stable source identities, original relationship contributor maps, SQL logical
  resource/detail views and conservative association/uncertainty behavior survive
  aggregation. Human same/alias are arrowless; derived_from remains directional.
- Complete input content, snapshot scope and projection version key model reuse.
  Cache retention is capped at three entries/12 MiB; each compilation has a 30-second
  deadline and obsolete workers are terminated. Graph size/evidence complexity and
  generated-view limits fail visibly instead of silently truncating source evidence.
- Kind filtering is presentation-only, including when a native layout snapshot is
  saved while a kind is hidden. Downloaded source retains all original relationships.
- Existing routing refinement, immediate completed-edit capture and native history
  support dragging and undo/redo. Worker startup/message/timeout failures are visible.
  Pinned viewpoints/favorites, ledger, group CRUD, source export, default orthogonal
  routing, optional animation and reduced-motion handling remain shared.

The app React version is aligned with the pinned LikeC4 spike at 19.3.0, with Vite
React/renderer dependency deduplication. LikeC4/core remain pinned at 1.60.0. The app
compiler remains TypeScript 5.9.3; the spike retains its TypeScript 7.0.2 checks and
separate WebWorker library check. No extractor compiler or artifact schema changes.

## Evidence collected during refinement

All build/install/test commands use the repository sandbox. `make explore PORT=5096
GITHUB_AUTH=none` built and served the integrated app, using the existing isolated
review PostgreSQL on port 5544. Port 5097 serves the same build without a database.
The previews use the built output under `bin/Debug/net10.0/wwwroot`; rebuilding only
source wwwroot is insufficient. Served asset identities are checked after rebuilds.

- Main UI TypeScript/build and .NET solution build passed.
- Explorer backend: 138/138 tests passed after generating fixture repositories and
  building UI assets. Earlier missing fixture/UI failures were setup omissions,
  corrected without changing test assertions.
- Shared/spike unit suite: 68/68 passed, including model/worker and group tests.
  The live model suite also passed against a captured real system-level API graph.
- Compiler round-trip: exported source preserves node IDs, metadata, labels and
  viewpoint membership. Shuffled input yields identical source and layout; unrelated
  insertions retain existing source IDs. Invalid/oversized inputs fail explicitly.
- Actual Glossary browser journey: 54 assertions with PostgreSQL, 34 without it;
  both at 1440×900, 5120×2160 and 390×844. CRUD, exact occurrence/evidence selection,
  reviewed association arrows, read-only behavior and source provenance passed.
  Simulated 409/503 cases test browser handling, not a real database outage.
- Architecture browser journey: 182 assertions passed, including exact provenance,
  live scope/revisions, drills, filtering after group edits, group persistence, real
  label bounds, readable initial zoom and explicit Fit. Worker startup failure/retry
  and late API responses are simulated; all three viewport sizes use Chromium.
- Trivy scan: 21 dependency manifests, zero High/Critical findings. This focused scan
  does not substitute for the final clean-HEAD ordinary push gate.

Receipts/screenshots are ignored local artifacts in `.sandbox-cache/glossary-browser/`,
`.sandbox-cache/live-architecture-browser/` and worker review caches. They are not
committed as source or claimed to cover remote deployments.

## Performance investigation (not accepted)

The source-bound comparison uses the captured historical 22-node/31-edge input,
SHA-256 `f6bd4915eb94ae9c0fc63cb22c46170b3deeb99388b380bd615cd149442e72e9`,
and 30 real pointer drags of the same repository node, run sequentially. The accepted
baseline is PR #15's `ba61ede` served on port 5093, asset `index-CgAIXxBW.js`. Port
5091 is an older runtime and is not a valid accepted comparison.

| Measurement | Accepted baseline | Dagre candidate `index-BxAhVrmb.js` |
|---|---:|---:|
| Worker refinement p50 / p95 | 83.9 / 111.9 ms | 270.9 / 310.8 ms |
| Release-to-routing-ready attribute p50 / p95 | 109.5 / 152.0 ms | 294.9 / 350.3 ms |

An in-page MutationObserver measures the routing-ready attribute. Earlier locator
polling inflated candidate results to approximately 830 ms; those values are invalid
and superseded. Neither attribute timestamps nor frame opportunities prove physical
display paint. Both builds have zero overlapping rendered label pairs. The candidate
has readable 90 px initial leaves but fails performance parity.

A coordinate-cache search optimization preserved paths but did not reproduce its
initial microbenchmark gain in the actual module; it was rejected. The public
Graphviz WASM API already present transitively is now integrated in the existing
model worker, without private LikeC4 compiler imports or another service. Its
production adapter passes six focused tests, including malformed output, hostile
display strings, compound endpoints and opposite/parallel/self relationships.
The corrected comparison is reproducible with `make live-architecture-benchmark`,
using already running accepted/candidate previews; no hosted browser job is added.

## Remaining gates and limits

Resolve the measured performance regression and repeat source-bound browser
validation. Then run the full ordinary push hook (`make ci` via act plus Trivy),
fresh `make pr-check`, and create the owner-review PR against stage. Final phase
scores and gate receipt belong in the completed handoff, not this pending record.

Issue #13's 200 ms p95 final-settling target remains open. The initial bundle adds
approximately 700 kB gzip for the lazy shared renderer and a 1.49 MB uncompressed
model worker including embedded Graphviz WASM; the entry bundle stays approximately 97 KiB gzip. No claim is made
for production-corpus capacity, all-browser support or physical-display paint.
Initial readability, the explicit Fit action, narrow-screen source visibility,
collapsed source containers and label spacing were corrected during browser review. General extractor fixes (#6) and
owner-controlled BPMN/DMN scope (#12) remain separate.
