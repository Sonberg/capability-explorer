# LikeC4 routing diagnostics

Related: [issue #13](https://github.com/Sonberg/capability-explorer/issues/13). This package measures the existing algorithm; it does not claim the 200 ms p95 settling target or change native edit scheduling.

## Enable and inspect

In the diagram's Settings, enable **Routing diagnostics**, drag a node/group, then choose **Download routing trace**. Diagnostics default off and are not persisted. Standalone `?routingTrace=1` is also supported. Embedded mode uses the switch inside the diagram. User Timing measures named `ce.routing.*` appear in the browser Performance panel. Record a DevTools CPU profile to investigate the functions inside a slow phase.

The local JSON contains numeric interaction/generation IDs, node/edge counts, statuses, timestamps and phase durations. It contains no repository names, labels, model payloads or source text. Storage retains at most 100 interactions, 128 events and spans per interaction, and 128 generation IDs per interaction. User Timing retains at most 256 distinct measurements, replacing duplicate names. Overflow is reported in `dropped`. Nothing is transmitted automatically. Disabling collection preserves the bounded snapshot for export; `window.ceRoutingTrace.reset()` clears records. Exporter failures are counted separately and cannot fail routing.

## Meaning of the intervals

- **release-to-snapshot:** the matching initiating pointer's release through LikeC4's `save-view-snapshot` callback. An unrelated click cannot replace the release timestamp. Rapid gestures supersede pending interactions; the newest snapshot is associated with the newest gesture. Native snapshots do not carry pointer IDs, so this is not proof that every intermediate gesture produces a separate snapshot.
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

Twelve warm node moves per viewport, one run each, headless container Chromium (no CPU profiler):

| Stage | 1920×1080 p50 / p95 | 5120×2160 p50 / p95 |
|---|---:|---:|
| Release → snapshot callback | 1,056 / 1,058 ms | 1,060 / 1,072 ms |
| Worker startup | 22 / 73 ms | 22 / 30 ms |
| Worker computation | 161 / 254 ms | 176 / 206 ms |
| Result → native view | 9 / 13 ms | 10 / 38 ms |
| Release → second frame opportunity | 1,294 / 1,477 ms | 1,344 / 1,410 ms |

These are small diagnostic samples; p95/p99 select the maximum at this sample size. Do not add per-stage percentiles to derive end-to-end percentiles. Source fingerprint (viewer source/model, lock and TypeScript configs): `1e508bf631ccc36ce6fe0258dede3e656b6317a02841579cbfecfedafcfe9abd`. Raw samples and environment are in `.sandbox-cache/likec4-browser/routing-lifecycle.json` in the integration worktree.

The dominant interval is upstream of our router. Inspection of installed LikeC4 1.60.0's editor finds a 50 ms snapshot scheduling delay followed by `wait-after-edit: 1000`. The measured release-to-callback interval is consistent with that sequence. The next optimization should investigate a supported way to shorten/bypass that scheduling for final drag commits while preserving its edit/history semantics; do not patch minified dependency files. Then investigate the second deterministic route candidate's path search (about 114/120 ms median here) and worker startup outliers. Even removing the one-second delay alone does not establish the 200 ms p95 target.
