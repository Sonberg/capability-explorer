# Issue #84: Analysis Job Limit

**State:** Proposed claim draft, not posted to GitHub  
**Claim ID:** `67E9C773-7B62-43D7-B5EB-36B08E56270F`  
**Owner / integration writer:** `per-sonberg_hmgroup`, designated by the operator for this scope  
**Base:** `051a5e634d06dbcc452bd999bd1fbb3e5d64d6e9`  
**Branch:** `feat/harden`  
**Created:** 2026-11-05

## Goal

Set `ANALYZE_JOBS` to a default of `1` for both native and sandbox profiles, and align the regression test. Convert the maintained postmerge test to TypeScript 7 and update its invocation.

## Exclusions

The separate unqualified-engine wrapper failure and broader concurrency changes are out of scope. Preserve the existing Playwright log modification.

## Owned Paths

- `config/build/native.mk`
- `config/build/sandbox.mk`
- `tests/postmerge-analysis.test.mjs` to `tests/postmerge-analysis.test.mts`
- `config/ci/required-plan.json`

Build profiles and the protected CI plan are shared files owned by the integration writer for this scoped change.

## Dependencies and Conflicts

- Issue #84 analysis-performance policy.
- PR #93 at `051a5e634d06dbcc452bd999bd1fbb3e5d64d6e9`; failed Actions run `42050712752`.
- Issue #3 active SourceSync scope was checked; no path overlap was found.

## Validation

Run the focused TypeScript 7 regression and postmerge tests, then check CI plan drift. Full local/act CI is currently unavailable: `act` and Docker are not installed, and Podman is not qualified for the full `ci` goal.

## Publication Blocker

This draft is not a recorded GitHub claim. Both `gh issue comment` and the REST issue-comment endpoint returned HTTP 403 for the current Enterprise Managed User account. Post the claim and obtain the required acknowledgement through an authorized GitHub account before changing the build profiles, test runner, or protected CI plan.

**Next update:** 2026-11-05 16:10 UTC
