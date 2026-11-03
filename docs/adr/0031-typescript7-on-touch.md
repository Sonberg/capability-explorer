# Convert maintained JavaScript to TypeScript 7 when modified

* Status: proposed
* Date: 2026-11-03
* Decision owner: repository contributors
* Coordination: [issue #3](https://github.com/Sonberg/capability-explorer/issues/3)
* Related: [MADR-0019](0019-independent-tool-evaluation.md), [MADR-0029](0029-frugal-ci-and-executable-parity.md), [MADR-0030](0030-dependency-cache-lifecycle.md)

## Context and problem statement

Maintained JavaScript scripts and tests coexist with TypeScript application
code. Continuing to change JavaScript indefinitely postpones type checking;
a repository-wide rewrite would expand unrelated work and obscure review.
The operator requests conversion whenever we touch or modify JavaScript.

Here, "TS7" means TypeScript 7 and its native compiler toolchain, not merely a
`.ts` extension, JSDoc checking, or executing type-stripped source. Existing
TypeScript is not proof that the repository already uses TypeScript 7.
For example, the Explorer client currently declares TypeScript 5.9.3.

## Decision drivers

Incremental type safety, behavior compatibility, small reviewable changes,
portable tooling, deterministic builds, and frugal local/hosted CI.

## Considered options

1. Continue authoring JavaScript or add JSDoc only: does not meet the request.
2. Convert every JavaScript file immediately: rejected as unrelated bulk work.
3. Convert maintained JavaScript on modification, with a qualified TypeScript 7
   execution path: selected for review and operator-directed work.

## Decision outcome

When modifying a repository-owned, hand-maintained `.js`, `.mjs`, `.cjs` or
`.jsx` file, convert that file in the same change to the appropriate `.ts`,
`.mts`, `.cts` or `.tsx` source. Write new maintained scripts and tests in
TypeScript as well. Reading, inspecting or executing a file is not modification.
Do not convert unrelated files merely because they share a directory.

Preserve ESM/CommonJS semantics, public entry points, executable wrappers,
imports, paths, working-directory assumptions, exit codes, signals, diagnostics
and test discovery. Update directly dependent callers and build/test commands.
JavaScript emitted for a runtime remains valid output; the maintained source
is TypeScript. Never leave two competing hand-maintained implementations.

The first conversion in a toolchain scope must establish a reproducible,
locked TypeScript 7 compiler and a supported runtime/build path, with explicit
type checking in both local and hosted CI. Verify the selected release and
platform support against official distribution information at implementation
time; do not install a floating `latest` dependency or assume preview `tsgo`
and release `tsc` commands are interchangeable. Type stripping or transpilation
without a type-check gate does not satisfy this decision.

Use the existing project configuration where compatible. Keep new compiler
configuration scoped when tooling scripts and browser code need different
module/runtime settings. Preserve strict checking, type public boundaries and
validate external input; do not hide migration errors with blanket `any`,
`@ts-nocheck`, unchecked casts or suppressed diagnostics.

Generated outputs, vendored third-party code and intentional JavaScript
language fixtures are not maintained implementation source. Preserve them in
their required form and identify the generator, upstream owner or fixture
purpose in the change evidence. A genuine tooling incompatibility requires an
explicit, narrowly scoped owner-approved exception with reason, affected files,
follow-up and review condition; silently retaining changed JavaScript is not
an exception. Stop the affected conversion if it cannot be qualified safely.

## Implementation and validation

Each conversion must record the old/new paths and exact compiler/runtime
versions, then pass type checking, affected tests and real entry-point smoke
checks. Cover behavioral boundaries affected by the conversion, rather than
adding tests that merely assert filename extensions. Confirm unchanged failure
reporting, module resolution and executable/test-runner discovery.

Follow MADR-0029: preserve every required gate, review intentional expanded-plan
changes, and run the complete ordinary local/hosted gates before publication.
Do not add another redundant CI job or cache successful test results. Follow
MADR-0030 for toolchain/cache identity changes and scoped obsolete-cache
disposition; this decision grants no global cache-cleanup authority.

The accompanying contributor instruction applies this rule to agent changes.
A future changed-file CI guard should detect added/modified maintained
JavaScript against the reviewed base, including renames and an explicit
exception mechanism. That guard and the TypeScript 7 execution path are
**not implemented by this documentation change**. Neither an instruction nor
a filename rename proves compiler adoption or executable enforcement.

## Consequences

JavaScript debt shrinks with normal maintenance instead of through a bulk
rewrite. A small JavaScript fix can require a larger, but directly related,
toolchain/caller adjustment; plan that cost before assigning parallel work.
Previously untouched JavaScript and existing TypeScript toolchains are not
upgraded by this record alone. Repository-wide acceptance remains a separate
owner decision.

## References

* [TypeScript native-port announcement](https://devblogs.microsoft.com/typescript/typescript-native-port/)
* [Official TypeScript repository](https://github.com/microsoft/TypeScript)
* [Native-port staging README](https://github.com/microsoft/typescript-go#readme),
  including the preview/release command distinction; verify current packaging
  when implementing rather than relying on historical staging instructions.
