# LikeC4 routing diagnostics

Related: [issue #13](https://github.com/Sonberg/capability-explorer/issues/13). The spike measures the routing pipeline and captures completed native edits immediately for local routing. It does not claim the 200 ms p95 settling target.

## Enable and inspect

In the diagram's Settings, enable **Routing diagnostics**, drag a node/group, then choose **Download routing trace**. Diagnostics default off and are not persisted. Standalone `?routingTrace=1` is also supported. Embedded mode uses the switch inside the diagram. User Timing measures named `ce.routing.*` appear in the browser Performance panel. Record a DevTools CPU profile to investigate the functions inside a slow phase.

The local JSON contains numeric interaction/generation IDs, node/edge counts, statuses, timestamps and phase durations. It contains no repository names, labels, model payloads or source text. Storage retains at most 100 interactions, 128 events, spans and invalid timing observations per interaction, and 128 generation IDs per interaction. User Timing retains at most 256 distinct measurements, replacing duplicate names. Overflow is reported in `dropped`. Invalid interval observations are retained in bounded `invalidTimings` arrays with raw endpoints and reason, excluded from duration statistics and OTel/User Timing export. Cross-context timer coarsening can produce tiny reversed intervals; they are never clamped to zero. A missing duration without an explicit anomaly remains a validation failure. Nothing is transmitted automatically. Disabling collection preserves the bounded snapshot for export; `window.ceRoutingTrace.reset()` clears records. Exporter failures are counted separately and cannot fail routing.

## Meaning of the intervals

- **release-to-snapshot:** the matching initiating pointer's release through the completed-edit snapshot captured from LikeC4’s public editor actor and XYFlow state. An unrelated click cannot replace the release timestamp. Rapid gestures supersede pending interactions; the newest snapshot is associated with the newest gesture. Completed-edit snapshots do not carry pointer IDs, so this is not proof that every intermediate gesture produces a separate snapshot.
- **preview / preview-to-worker-effect:** provisional connector computation and the following scheduling/render delay before worker creation.
- **worker-startup / worker-boot-to-request:** construction through execution of the worker module, then receipt of its request. The aggregate **worker-startup-and-request-delivery** includes loading/initialization and request delivery; it is not a pure serialization measurement.
- **postMessage-call:** synchronous call duration, including any synchronous clone work. **result-delivery** includes response cloning/transfer and main-thread scheduling; it cannot separate those without deeper browser profiling.
- **worker-total:** receipt through preparation of the response. Per-order `grid-ports`, `search`, `labels`, `geometry`, and `quality` spans identify work inside the three deterministic routing candidates. Phases are aggregate measurements, not one event per edge/grid cell.
- **result-to-native-view:** main-thread receipt through LikeC4's actor consuming the expected view. A React result commit is recorded separately; the parent commit alone does not prove geometry application.
- **native-view-to-frame-opportunity / release-to-frame-opportunity:** two animation-frame callbacks after native view application, provided that generation is still current. **These are rendering opportunities, not proof of final pixels being painted.** Use browser Performance/screenshot traces to investigate layout/paint, and do not report these measurements as exact final-paint latency or INP.

Worker and main timestamps are normalized using `performance.timeOrigin + performance.now()`. Raw `performance.now()` values across contexts are not comparable. Timing changes after sleep or in hidden/throttled tabs need a fresh run. Diagnostic work and CPU profiling have overhead; compare matching configurations and run profiling separately from baseline timing.

## Optional OpenTelemetry integration

No SDK, provider, exporter or collector is installed by this spike. If the host already has a configured OTel tracer, wire it explicitly **inside the diagram window**:

```js
window.ceRoutingTrace.setOtelTracer(configuredTracer);
// Detach:
window.ceRoutingTrace.setOtelTracer(undefined);
```

The adapter creates manual spans with epoch-based OTel timestamps and `diagram.interaction.id` / `diagram.routing.generation` attributes. They are individual stage spans, correlated by attributes, not an automatically reconstructed distributed trace tree. Transmission, sampling, destination and retention belong to the host's provider. Without an attached tracer, this remains local User Timing/JSON instrumentation. An alternative `setExporter((span, context) => ...)` callback can consume the same bounded stage data. Do not attach exporters that capture models or send data without authorization.

## Repeatable local validation

All commands run through `scripts/sandbox`. `make likec4-trace-check` reuses the existing Playwright image against the candidate on port 5092. It tests disabled collection, groups, unrelated releases, rapid drags, cancellation, real worker load failure and recovery, then runs 12 node moves at 1920×1080 and 5120×2160. No extra hosted CI browser job or image is introduced. Unit coverage remains in the regular spike/whole-project gate.

`test/routing-lifecycle.mjs` accepts these variables **inside** the sandbox:

- `TRACE_URL`: standalone preview (default port 5092) or the embedded Explorer URL ending `#/architecture`.
- `TRACE_MOVES=100 TRACE_RUNS=3`: the larger sample requested by #13, for each viewport. Defaults 12 and 1 are diagnostic samples, not acceptance evidence for tail latency.
- `TRACE_CPU=1`: additionally capture main-thread Chromium CPU profiles. Run separately; profiling changes timing. Worker CPU profiles require attaching DevTools to the worker; phase durations are already recorded.

Outputs under `.sandbox-cache/likec4-browser` include `routing-lifecycle.json` and optional `.cpuprofile` files. The JSON records source/model/lock/config SHA-256, browser/version, viewport, OS/CPU/container facts, raw interactions and p50/p95/p99 per phase. Fresh pages isolate runs; startup completes before timed drags, so these are warm interactions, not cold-start measurements. Display refresh rate/physical reference hardware must be documented separately for formal #13 acceptance. The test verifies actual node movement and generation completion instead of merely waiting on a pre-existing ready flag.

## Sources

- [User Timing API](https://developer.mozilla.org/en-US/docs/Web/API/Performance_API/User_timing)
- [Worker/main time origins](https://developer.mozilla.org/en-US/docs/Web/API/Performance/timeOrigin)
- [OpenTelemetry public Tracer API](https://github.com/open-telemetry/opentelemetry-js/blob/main/api/src/trace/tracer.ts)
- [OpenTelemetry browser status](https://opentelemetry.io/docs/languages/js/getting-started/browser/)

## Initial diagnostic result (2026-11-01)

Baseline instrumentation commit `c274cb0`: twelve warm node moves per viewport, one run each, headless container Chromium (no CPU profiler):

| Stage | 1920×1080 p50 / p95 | 5120×2160 p50 / p95 |
|---|---:|---:|
| Release → snapshot callback | 1,056 / 1,058 ms | 1,060 / 1,072 ms |
| Worker startup | 22 / 73 ms | 22 / 30 ms |
| Worker computation | 161 / 254 ms | 176 / 206 ms |
| Result → native view | 9 / 13 ms | 10 / 38 ms |
| Release → second frame opportunity | 1,294 / 1,477 ms | 1,344 / 1,410 ms |

These are small diagnostic samples; p95/p99 select the maximum at this sample size. Do not add per-stage percentiles to derive end-to-end percentiles. Source fingerprint (viewer source/model, lock and TypeScript configs): `1e508bf631ccc36ce6fe0258dede3e656b6317a02841579cbfecfedafcfe9abd`. Raw samples and environment are in `.sandbox-cache/likec4-browser/routing-lifecycle-baseline.json` in the integration worktree.

The dominant interval is upstream of our router. Inspection of installed LikeC4 1.60.0's editor finds a 50 ms snapshot scheduling delay followed by `wait-after-edit: 1000`. The measured release-to-callback interval is consistent with that sequence. The next optimization should investigate a supported way to shorten/bypass that scheduling for final drag commits while preserving its edit/history semantics; do not patch minified dependency files. Then investigate the second deterministic route candidate's path search (about 114/120 ms median here) and worker startup outliers. Even removing the one-second delay alone does not establish the 200 ms p95 target.

## Immediate edits and exact TypeScript optimization

LikeC4 1.60.0 exposes no public edit-delay setting. The spike subscribes to its public editor actor’s history/redo transitions and reads final XYFlow geometry in a microtask after native actions finish. It retains native history and handlers, absolute group coordinates, evidence and presentation. The native delayed persistence callback is ignored while this adapter is attached, preventing it from replaying an already refined edit. No dependency files are patched. This adapter is version-sensitive: changing LikeC4 requires rechecking native snapshot construction and running the drag/group/undo/redo browser regressions.

The A* implementation now reuses epoch-tagged distance/parent arrays and a typed binary heap across searches within one routing invocation. Costs, neighbor order, heap tie order, ports, candidate orders and quality selection remain unchanged. Tests compare exact paths on 150 seeded grids and complete output on five generated-model cases against the pre-optimization implementation. This improves allocation/search overhead without name-specific geometry rules or changing minimum line spacing.

`test/immediate-layout-browser.mjs` verifies native undo/redo restores exact node coordinates and no extra routing generation appears for 1.5 seconds after drag/undo/redo settles. The regular diagnostics browser test additionally covers group movement, rapid gestures, view switches and worker failure recovery.

## RuVector WASM experiment

The evaluated API is `WasmRoadRouter` from published `@ruvector/mincut-wasm@2.3.0`, not vector similarity search. The test-only adapter explicitly encodes four heading states per grid cell, directed transitions, turn restrictions and existing penalties. Costs are scaled by 200 to retain half-grid corridor preferences. Graph construction and disposal are included on every query because occupancy and corridor costs change between edges. No production dependency or runtime WASM bundle is added.

The API matches minimum costs/reachability on 100 seeded small grids, but its equal-cost tie choices can change full route geometry. Exact output equality is therefore a separate adoption gate. This does not establish worse geometry or rule out a future persistent/incremental WASM design.

To reproduce the CPU comparison, unpack the pinned npm tarball into ignored `.sandbox-cache/ruvector-evaluation/package`, then run inside the node sandbox from `spikes/likec4`:

```sh
WASM_MODULE=/work/.sandbox-cache/ruvector-evaluation/package/node/ruvector_mincut_wasm.js node --experimental-strip-types test/search-performance.mjs
```

The benchmark compares the complete routing pipeline, with two warmups and five measured iterations for each implementation. TypeScript baseline/optimized order alternates; WASM runs separately. Module load is excluded, but per-query graph conversion/construction/free is included. Results go to `.sandbox-cache/likec4-browser/search-performance.json`. These are sandbox CPU timings, not browser or physical-display latency.

Sources: [published router binding](https://github.com/ruvnet/ruvector/blob/main/crates/ruvector-mincut-wasm/src/routing.rs), [routing core](https://github.com/ruvnet/ruvector/blob/main/crates/ruvector-mincut/src/routing/mod.rs), [wrapper](https://github.com/ruvnet/ruvector/blob/main/npm/packages/ruvector/src/core/mincut-wasm-wrapper.ts).

Warm full-pipeline medians (five measured runs after two warmups, Node sandbox):

| Generated view | Original TypeScript | Optimized TypeScript | WASM adapter |
|---|---:|---:|---:|
| Overview | 114.9 ms | 73.4 ms | 928.7 ms |
| Vertical | 101.8 ms | 62.5 ms | 786.4 ms |
| Landscape | 80.4 ms | 53.6 ms | not measured |
| Data | 103.2 ms | 67.0 ms | not measured |
| Node move | 117.3 ms | 69.9 ms | not measured |

All five optimized TypeScript outputs equal the original exactly. Both WASM outputs differ. **Decision: retain optimized TypeScript; do not ship this WASM adapter.** This experiment provides no speed benefit and does not meet the exact-output adoption gate. Model SHA-256: `25f889dcab5b4198b392a3dba304e0f05a9ee2fe64d3779b06d2c86209245f29`. The baseline kernel is preserved in `test/search-baseline.ts`; the evaluated adapter lives only in `test/wasm-search.mjs`.

Timing caveat: an initial larger run stopped on a missing `result-delivery` span. The old recorder silently discarded reversed intervals; the failed raw timestamps were not retained, so its cause is unproven. The recorder now preserves invalid observations. The browser benchmark tolerates only explicitly recorded sub-millisecond reversed **cross-context delivery** intervals; larger reversals, non-finite values and missing spans still fail. Same-context release-to-frame durations remain required for every sample. See [High Resolution Time coarsening](https://www.w3.org/TR/hr-time-3/#dfn-coarsen-time).

## Optimized browser result (2026-11-01)

Three fresh-page runs of 100 warm node drags per viewport (600 total), headless Chromium 156.0.8078.4 in the pinned Playwright container, without CPU profiling:

| Stage | 1920×1080 pooled p50 / p95 / p99 | 5120×2160 pooled p50 / p95 / p99 |
|---|---:|---:|
| Release → captured snapshot | 2.2 / 4.0 / 5.6 ms | 2.8 / 6.6 / 8.1 ms |
| Worker computation | 78.8 / 136.7 / 150.3 ms | 87.3 / 132.6 / 161.3 ms |
| Release → second frame opportunity | 130.1 / 208.0 / 240.8 ms | 184.1 / 268.8 / 314.5 ms |

Per-run p95 settling: 217.6, 194.5, 195.8 ms at 1920; 294.2, 244.5, 256.4 ms at 5120. These are not physical Dell display or exact-paint measurements. The baseline had only 12 moves per viewport, so comparisons are diagnostic rather than a matched statistical experiment. **The 200 ms p95 target remains unmet**, especially on the ultrawide viewport.

Source/model/lock/config fingerprint: `2cf1ed80b8acf337ad182c5ae059a3ad64b1b05ec0ae1365079f21f74cd77298`. Raw data/environment: `.sandbox-cache/likec4-browser/routing-lifecycle-optimized-600.json`. This successful run precedes only the additive invalid-timing reporting change described above; it had all required intervals and did not tolerate missing intervals. The final recorder is covered separately by focused unit/browser regression checks.

Next bounded optimization: measure worker startup/recreation and native-view/frame scheduling tails separately, then evaluate a reusable worker with explicit latest-generation cancellation. Preserve the exact routing differential tests, native history, group movement and evidence semantics. Do not replace the router or weaken spacing/quality to chase the target.
