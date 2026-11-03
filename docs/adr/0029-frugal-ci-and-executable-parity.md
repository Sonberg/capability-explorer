# Frugal CI with executable local and hosted parity

* Status: proposed
* Date: 2026-11-03
* Decision owner: repository contributors
* Coordination: [issue #3](https://github.com/Sonberg/capability-explorer/issues/3)
* Related: [MADR-0003](0003-local-ci-before-push.md), [MADR-0011](0011-source-bound-security-validation.md), [MADR-0028](0028-portable-decision-ledger.md)

## Context and problem statement

The full local act gate and hosted job execute the same sandbox Make target.
Download caches already exist, but `npm ci` still reinstalls dependencies.
Separate build invocations repeat preparation; within one Make invocation
shared prerequisites already execute once. A previous local gate took about
nine minutes; this is context, not a comparable optimization benchmark.
Blind Make parallelism would race fixture regeneration, restore, shared
`bin`/`obj`, UI output and extraction paths.

## Decision drivers

Preserve complete deterministic coverage, explicit failures, credential
isolation and exact-source non-reusable receipts. Reduce measured work or
wall time without silently increasing hosted jobs, accepting stale installed
outputs or treating a cache hit as a test pass.

## Considered options

1. Unbounded `make -j`: rejected because of shared mutable outputs.
2. Installed-output stamps or cached successful tests: rejected; directory
   presence and lock hashes do not validate installed bytes or current tests.
3. Keep the existing serial Make gate, reuse download caches and parallelize
   only independent fixture cases within a single phase: selected for review.

## Decision outcome

Retain `make EXECUTION=sandbox ci` as the executable canonical plan for both
local act and GitHub. Serialize Make recipes even if a caller supplies `-j`.
Fixture compilation alone may run two cases concurrently, each in its own
`fixtures/.work/<repository>@<tag>` directory and log. Each .NET case's
HOME/CLI first-use migration state is isolated as well.
The shared NuGet download cache stays in the sandbox's declared package path.
Restore and build phases,
fixture generation, extraction and platform tests must not overlap. The helper
accepts only jobs `1` or `2`; no environment-driven unbounded pool. It waits for
launched workers, propagates unexpected failures and fails on interruption.
The intentionally broken C# fixture retains its diagnostic expectation.

`scripts/ci/check-frugal-ci` expands the actual Make graph without executing
its recipes and validates it using `check-frugal-ci.mjs`. CI then runs the
ordinary Make graph, not an unrelated descriptive manifest. Its reviewed
`config/ci/required-plan.json` snapshot includes the ordered expanded commands
and hashes of the build, workflow, isolation, cache and fixture-worker policy
inputs. Missing, extra, reordered or narrowed commands fail; duplicate work
is visible in review. Both drift checker and negative tests run in full CI.
This is a deterministic change-control guard, not proof against an author
maliciously changing both the implementation and its reviewed policy.

Only existing download/tool caches are restored by Actions. Cache keys include
OS, architecture, pinned runtime images, sandbox/install configuration and
dependency manifests/locks. Install/restore still executes; npm validates
package integrity and a missing/incomplete cache fetches or fails normally.
The sandbox sets `npm_config_prefer_offline=true`: npm bypasses cached-data
staleness requests but still fetches missing data, as documented in
[npm's configuration contract](https://docs.npmjs.com/cli/v11/using-npm/config#prefer-offline).
This does not enable full offline installation or reuse `node_modules`.
Do not restore `node_modules`, `bin`, `obj`, test results or local-CI success
receipts. Preserve disabled npm lifecycle scripts and the mandatory pinned
Trivy scan. No new cache producer receives credentials or publication authority.
No new installed-output reuse mechanism is authorized by this decision.

The pre-push gate still runs all required checks against clean exact HEAD,
checks main alignment before/after, invalidates prior success on failure and
does not reuse test passes. Local/hosted infrastructure differs explicitly:
act retains local package caches and audits actions on the host; hosted
Actions restores the declared package cache and uses its job audit step.
Semantic plan parity is required, not identical elapsed times.

## Maintenance rule and validation

The integration owner must review **every** plan/input change when workflow,
Make prerequisites, test filters, sandbox/runtime/cache settings or worker
resource ownership changes. Run:

```sh
node scripts/ci/check-frugal-ci.mjs
node --test tests/frugal-ci.test.mjs tests/fixture-concurrency.test.mjs
bash tests/local-ci.test.sh
```

For intentional changes, `node scripts/ci/check-frugal-ci.mjs --record`
creates a candidate snapshot. Review its diff against the prior required
coverage before committing; never regenerate it blindly to make CI green.
The standard full local pre-push gate and exact-head hosted CI remain required.
Update this record, the navigation index/diagram and portable source receipts
together when changing the policy, according to MADR-0028.

Benchmark changes on the same host, image identities, source and cache state.
Record cold/warm profiles separately, phase commands, exit status, case count
and wall time; compare serial and two-worker runs after identical preparation.
Run both modes more than once in alternating order where practical. Do not
claim that lower wall time means lower billing or CPU consumption.
An optimization is not demonstrated until measurements improve while case
inventory and failures remain equivalent. No noisy wall-time threshold blocks
ordinary contributors: structural plan/cache/concurrency invariants are the
hard gate. Re-measure after material toolchain, fixture or runner changes and
when reviewing a reported slowdown.

## Consequences and limits

One hosted job remains the default. Tests/security stages are not removed,
test success is never cached, and full preparation remains intentionally
conservative. Two fixture builds may use more instantaneous CPU/memory, so the
serial option remains available for diagnosis; any higher cap requires new
resource evidence and policy review.

The snapshot records commands and selected policy inputs; it is not an
exhaustive semantic audit of all test code or an attestation of every process
executed. Focused mutation tests exercise omission, filter/entrypoint drift,
cache scope/key omissions, worker bounds, failure and interruption. Actual
full-run results and source-bound measurements belong in the PR evidence;
neither this proposal nor a merge certifies performance on every workstation
or accepts this decision repository-wide.
