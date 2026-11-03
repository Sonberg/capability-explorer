---
applyTo: ".github/workflows/**,config/build/**,config/runtime/**,config/ci/**,scripts/ci/**,scripts/sandbox,scripts/compile-fixtures.sh,scripts/local-check"
---

Keep local and hosted full CI aligned with proposed
[MADR-0029](../../docs/adr/0029-frugal-ci-and-executable-parity.md).
Preserve all required gates, test filters, credential isolation and exact-source
non-reusable success receipts. Cache downloads, never test passes or unvalidated
installed outputs. Make stays serial; only disjoint fixture cases have a bounded
two-worker pool. No blind `make -j` or additional hosted matrices for wall-time
claims without work/cost evidence.

Run `node scripts/ci/check-frugal-ci.mjs` and the focused drift/concurrency tests
before the mandatory full local workflow. For intentional CI changes, regenerate
the candidate with `--record`, review every required-plan diff for preserved
coverage, and commit the reviewed snapshot with the implementation. Do not
refresh blindly to suppress drift. Record comparable source/image/cache-bound
measurements for performance changes; update the MADR and ledger views when
policy changes. Owner acceptance remains separate from implementation and merge.

Framework/toolchain upgrades must include the cache-lifecycle disposition from
proposed [MADR-0030](../../docs/adr/0030-dependency-cache-lifecycle.md): inventory
and ownership, obsolete versus shared/transitive/rollback/offline retention,
exact approved cleanup or a documented retention reason, measured reclaimed
space and restore/build verification. A cache-key change is not disk cleanup;
`npm cache verify` is not proof of workspace-wide disuse. Never purge global
caches or another builder/workspace to satisfy a project-local housekeeping rule.
