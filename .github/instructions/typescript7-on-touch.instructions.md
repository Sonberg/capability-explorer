---
applyTo: "**/*.{js,mjs,cjs,jsx,ts,mts,cts,tsx}"
---

Follow [MADR-0031](../../docs/adr/0031-typescript7-on-touch.md): when modifying
repository-owned, hand-maintained JavaScript, convert the touched file to
TypeScript 7 in the same change. New maintained scripts/tests must also use
TypeScript. Merely reading or running JavaScript does not trigger conversion.

Preserve module kind, public entry points, runtime behavior, errors, signals
and test discovery; update directly dependent callers. Establish a locked,
qualified TypeScript 7 compiler/type-check and execution path for the affected
scope. Renaming files, JSDoc, or type stripping alone is insufficient. Do not
use blanket `any`, `@ts-nocheck` or unchecked casts to bypass migration errors.

Do not bulk-convert unrelated files, generated output, vendored code or
intentional JavaScript language fixtures. Record why those files are excluded.
Other incompatibilities need a scoped owner-approved exception, not silent
deferral. Preserve full local/hosted CI under MADR-0029 and scoped cache
lifecycle controls under MADR-0030. This instruction does not itself install
TypeScript 7 or provide a changed-file CI enforcement guard.
