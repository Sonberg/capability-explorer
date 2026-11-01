# Live LikeC4 replacement execution

Status: refinement gate **95/100, passed** after the user authorized continuation.
The resumed candidate passes independent review, functional checks and sampled
browser performance parity. Final full act/security and PR gates are pending.
No push, PR, merge or release is claimed.

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
- Shared/spike unit suite: 70/70 passed, including model/worker and group tests;
  six additional independent adversarial adapter tests passed before integration.
  The live model suite also passed against a captured real system-level API graph.
- Compiler round-trip: exported source preserves node IDs, metadata, labels and
  viewpoint membership. Shuffled input yields identical source and layout; unrelated
  insertions retain existing source IDs. Invalid/oversized inputs fail explicitly.
- Actual Glossary browser journey: 54 assertions with PostgreSQL, 34 without it;
  both at 1440×900, 5120×2160 and 390×844. CRUD, exact occurrence/evidence selection,
  reviewed association arrows, read-only behavior and source provenance passed.
  Simulated 409/503 cases test browser handling, not a real database outage.
- Architecture browser journey: 184 assertions passed, including exact provenance,
  live scope/revisions, drills, filtering after group edits, group persistence, real
  label bounds, readable initial zoom and explicit Fit. Worker startup failure/retry
  and late API responses are simulated; all three viewport sizes use Chromium.
- Trivy scan: 21 dependency manifests, zero High/Critical findings. This focused scan
  does not substitute for the final clean-HEAD ordinary push gate.

Receipts/screenshots are ignored local artifacts in `.sandbox-cache/glossary-browser/`,
`.sandbox-cache/live-architecture-browser/` and worker review caches. They are not
committed as source or claimed to cover remote deployments.

## Performance investigation (superseded candidates)

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

The public-WASM candidate was then measured with the tracked benchmark (30 drags,
no competing tests), asset `index-B0WuVpjx.js` (SHA-256
`219126db1c0c7207c387a85032668fe7117eb1d32453fa42ee0d1639908cc2e1`):

| Measurement | Accepted baseline | Public Graphviz candidate |
|---|---:|---:|
| Worker refinement p50 / p95 | 80.5 / 92.9 ms | 161.6 / 201.0 ms |
| Release-to-routing-ready attribute p50 / p95 | 98.8 / 116.7 ms | 180.2 / 225.2 ms |

Both retain zero overlapping rendered label pairs. This is an improvement over the
Dagre attempt but still fails the migration's performance-parity gate. Subsequent
zoom-control/source-overlay changes have functional browser evidence bound to
`index-Cu1KIVXo.js`; they are not claimed as a new performance measurement.

Node-only routing bounds nearly halved one captured CPU sample, but worsened three
crowded parallel-edge cases. That shortcut was rejected. The exact boundary guard
cannot retain the speedup: certified searches perform the same work, and unsafe
searches require a full fallback. A stronger turn heuristic also worsened crossings.
Routing penalties, route-order comparisons and quality criteria remain unchanged.


## Resumed refinement: accepted candidate

The user authorized continuation after the historical handoff below. A generic
DOT serializer now uses public LikeC4 style/ancestry APIs, escaped bounded text,
measured multiline labels, compound-first adjacency declarations and
hierarchy-aware rank weights. It contains no fixture IDs or captured DOT.
Ancestor/leaf/internal-pair indices avoid per-edge full-graph rescanning. Layout
text is XML-normalized and escaped; original model/export strings remain intact.

The offline diagnostic improved routing median from 102.39 to 26.75 ms, with
81 searches and zero failures in both. Shared/near segments stayed zero; crossings
fell from two to zero. Path length increased from 13388 to 16914.83 model units
while segment count fell from 67 to 59. These diagnostics explain the candidate;
the browser comparison below supplies the performance evidence.

Final sequential comparison: 30 accepted drags followed by 30 candidate drags,
with other heavy checks paused. Candidate entry `index-C1M2dSeQ.js`, SHA-256
`6f5761535cfe6fbd9cdb0123d5c94dbffcb6cbd5b172570d4d97d9f588b88081`.

| Measurement (p50 / p95) | Accepted baseline | Resumed candidate |
|---|---:|---:|
| Preview | 0 / 0.1 ms | 0 / 0.1 ms |
| Worker refinement | 82.2 / 124.4 ms | 43.5 / 55.4 ms |
| Release-to-routing-ready attribute | 103.2 / 165.6 ms | 69.1 / 148.2 ms |

Every candidate median/p95 is no slower in this finite sample. Both builds retain
25 rendered labels with zero actual bounding-box overlaps before and after the
final drag. Receipt v2 separates procedure completion from sampled timing parity;
it does not claim final-paint parity. Receipt:
`.sandbox-cache/live-architecture-benchmark/comparison.json`.
Benchmark script SHA-256:
`879bafd68c93b3d43b306f91a06e93c703ee992189d500861b1b8d4cb6151cfa`.
Model worker `live-model.worker-DvZ0296Q.js` SHA-256:
`8bfa623b77b90672360b19a0ab01630c50fae42012d9045110496badd8a8840b`.
Routing worker `routing.worker-CFyqkgR2.js` SHA-256:
`edf1806f519846c661a7c148f1a352402320bfd1dd2319888118dd4a188d92dd`.

The Architecture browser journey passes 184 assertions against this exact entry.
Independent adapter review adds six adversarial tests covering all-view source
provenance, escaping, four directions, compound/self/parallel relationships,
malformed output and hierarchy rejection. Raw lone UTF-16 surrogates fail visibly
in upstream model hashing; layout-only normalization does not claim to repair that
earlier boundary. The reviewer found no remaining adapter blocker.

Refinement score: 95/100, one resumed implementation iteration. Deductions: three
points for the finite fixture-only timing sample and two for Chromium-only browser
coverage (see benchmark limitations and browser scripts). No general production
corpus or final-paint claim is made. Prior failed experiments remain documented.
The resumed Glossary journeys also pass 54 database-backed and 34 read-only
assertions at all three viewport sizes. Both previews serve the same candidate.
The Brain review returned `sparc/docs/tutorial.md`: it supports independently
verified phase evidence, but supplies no additional layout implementation.
No inference-provider calls or new services were added; native agent spend is
unavailable and no monetary savings are claimed.

## Remaining gates and limits

The resumed candidate below resolves the sampled performance regression and
passes the source-bound browser journey. Run the full ordinary push hook
(`make ci` via act plus Trivy),
fresh `make pr-check`, and create the owner-review PR against stage. Final phase
scores and gate receipt belong in the completed handoff, not this pending record.

Issue #13's 200 ms p95 final-settling target remains open. The initial bundle adds
approximately 700 kB gzip for the lazy shared renderer and a 1.49 MB uncompressed
model worker including embedded Graphviz WASM; the entry bundle stays approximately 97 KiB gzip. No claim is made
for production-corpus capacity, all-browser support or physical-display paint.
Initial readability, the explicit Fit action, narrow-screen source visibility,
collapsed source containers and label spacing were corrected during browser review. General extractor fixes (#6) and
owner-controlled BPMN/DMN scope (#12) remain separate.


## Historical refinement handoff

Five bounded refinement passes covered initial live integration, readable/compact
layout, semantic ranking and label clearance, the public Graphviz adapter, and
performance/edge-case review. The previous self-assessment was **90/100**: the ten-point
deduction is the measured routing regression in the table above. Functional source,
worker failure paths and browser journeys pass; the performance criterion does not.
Completion is not graded or claimed. The brain-build phase gate requires at least
95/100 and stops after five unsuccessful refinement iterations.

The final read-only model probe captured canonical DOT through public compiler and
Graphviz APIs. Passing that DOT, with live identities, through the same adapter
produced about 23 ms offline routing versus 96 ms for the adapter's generated DOT.
This is **diagnostic fixture evidence**, not a production solution or browser result.
It locates remaining work in generic DOT construction/content measurement and the
resulting geometry. Hardcoding captured fixture DOT is not an acceptable repair.

Next action: implement and independently review a bounded canonical-style content
measurement/adjacency serializer using public APIs, keeping original identities,
conservative associations and worker limits. Compare the same fixture and the
crowded counterexamples; then rerun the tracked actual-browser benchmark. Only
advance to final act/security, fresh stage alignment and owner-review PR when the
performance gate passes. Do not change routing quality penalties to obtain a pass.

Local research files are in `/private/tmp/ce-live-likec4-model/.sandbox-cache/`
(`captured-1.dot`, `graphviz-final-probe.mjs`, and the order/semantic/native-content
probes). Rejected search-domain probes are in the browser worker's ignored cache.
The reproducible benchmark script and fixture are tracked; ignored probe outputs
are not release evidence. Integration branch: `feat/live-likec4-architecture` in
`/private/tmp/ce-live-likec4`. Preview: `http://127.0.0.1:5096/#/architecture`.

Not tested or accepted: final physical-display paint, general production-corpus
capacity, non-Chromium browsers, remote deployment, and the final clean-HEAD full
act/security gate. Existing focused Trivy/backend results do not replace that gate.
Automatic Brain turn recording reported unhealthy readback; manual AgentDB
checkpoint store/retrieve was used without claiming automatic learning is healthy.

The historical next action above has been completed by the resumed candidate.
Current next action: commit this evidence, run the ordinary push hook's full
act/security gate, refresh stage alignment with `make pr-check`, and create the
owner-review PR. Owner merge and any release remain outside this authorization.
