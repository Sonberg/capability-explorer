# Portable decision ledger: bounded completion and handoff

This is the worker's completion receipt, **not** a claim that the overall Brain
Build, shared CI, hosted checks, publication or decision acceptance is complete.

## Source-bound evidence

Branch: `feat/portable-decision-ledger`.
Approved baseline and freshly read remote main:
`973fa06e6f2d623acadfe49445ac4b74b738b90e`.
Verified implementation/test source:
`e56a3c5029bed1eaa2ec46434cbcc6bb47355f86`.
The completion commit adds only this receipt; its final SHA is recorded in the
session checkpoint and bounded handoff after committing.

Authenticated read-only GitHub check returned `Tigon32`; current remote main was
read through `gh api repos/Sonberg/capability-explorer/commits/main --jq .sha`.
No GitHub write, fetch/remote-ref mutation, deletion, setting change, push or PR
occurred. Authorized local phase commits updated only the assigned topic branch.

| Phase | Local source commit | Final bounded score | Iterations | Actual correction/evidence |
| --- | --- | --- | --- | --- |
| Specification | `57b0483` | 96 | 1 | Seven obligations and fail-closed lead-only policy; specification gate passed |
| Pseudocode | `16bbe5d` | 96 | 1 | Executable positive/negative precedence, coverage and cycle examples passed |
| Architecture | `11cda5b` | 96 | 2 | Excluded fenced shell H1 examples; configured canonical authority and directed JSON semantics; A gate passed |
| Refinement | `e56a3c5` | 96 | 4 | 24→25→26→27 green focused cases; strengthened output boundaries and explicit acceptance profile |
| Completion, owned scope only | this receipt's commit | 96 | 1 | Exact committed-source CLI/test/ownership checks below; full integration remains lead-owned |

These are self-grades against the bounded ownership envelope, not independent
approval. Four points remain withheld for unqualified real-workspace dialects
and unauthenticated recorded authority. No known flaw is concealed by claiming
the owner's out-of-scope release gates passed.

## Actual completion commands

Executed successfully against clean `e56a3c5`:

```sh
node --test tests/decision-ledger.test.mjs
# 27 tests; 27 pass; 0 fail, skipped or cancelled.
node scripts/decisions/check.mjs specification
node scripts/decisions/check.mjs pseudocode
node scripts/decisions/check.mjs architecture
# Each PASS; repository: 28 records, 0 asserted edges, 75 explicit diagnostics.
node scripts/decisions/check.mjs generate --dry-run
# PASS; no files written.
for f in scripts/decisions/*.mjs tests/decision-ledger.test.mjs; do
  node --check "$f" || exit 1
done
git diff --check
# PASS.
```

`node scripts/decisions/check.mjs all` already passed against the final R runtime
source, invoking all S/P/A checks and **one** focused process with 27/27 passing.
The C commit changes documentation only; final exact-head re-verification is
recorded in the session checkpoint.

Additional actual instrument checks:

* `git merge-base --is-ancestor 973fa06 HEAD`: PASS.
* Ownership audit: all 28 pre-completion changed paths are **new owned files**,
  no existing file modified/deleted/renamed; all are under 500 lines.
* All 28 baseline ADR/navigation files remain byte-identical to baseline.
* Two independent CLI dry-run manifests compare byte-identically.
* `node scripts/decisions/check.mjs check` deliberately exits 1 with
  `stale projection: ledger.json` because integration views are not generated
  yet; clean Git status before/after proves that this missing-view check writes
  nothing. This expected negative is not reported as a successful integration
  view check.
* Fixture work directories were removed by the test lifecycle; no persistent
  scratch outputs, dependencies or orchestration framework were added.

Dry-run projection manifests, before any lead lifecycle/source changes:

| Projection | UTF-8 bytes | SHA-256 |
| --- | --- | --- |
| `graph.mmd` | 7153 | `e89be3eed8633b8cb11060f07701d7acd800460077aeea3f2c5e741755d54da4` |
| `index.md` | 25530 | `9cb058c909e0b7376c2377259408c2b111b204595326f001b5b0b0dc0876d50e` |
| `ledger.json` | 79918 | `1cecc2f5eb4301f1122404b9b0707bcf173cf35b2c305c08977ed01710e93417` |

## Explicit handoff to integration owner

1. Review the proposed schema, sidecar and MADR-0028. Empty repository facets
   and zero typed edges are intentional: no folder/title/prose inference.
   The controlled multi-facet mechanism is exercised by synthetic declarations.
2. Own shared make/build/CI, main index/diagram, handoff/changelog and lifecycle
   MADRs 0005/0014/0026. None was modified here. Choose a projection location
   outside canonical discovery or explicitly exclude it.
3. If those source ADRs change, refresh their **existing stable IDs'** revisions,
   status mappings and receipt spans/hashes in the sidecar. A changed file must
   not become a new identity, and a forged self-attested hash cannot pass.
   `authorRecord` is an explicit enrollment aid, never called during validation.
   Preserve reviewed acceptance/facets only after rechecking their source.
4. Wire one offline gate without a duplicate focused suite. `all` invokes the
   focused process once; if the full test runner already discovers this file,
   avoid separately invoking `all` in that same full gate without accounting
   for that duplication.
5. Generate/check actual integration views, verify local links and real Mermaid
   parse/render, run `make test-decision-ledger` once the lead creates its target,
   full local CI and `make pr-check`, and independently review material risks.
6. Only the lead may publish/host-check the coherent exact-head PR. No acceptance
   or merge is implied. Overall completion remains pending these steps.
7. Retain the audited 17 refs under the supplied protection/permission unknowns;
   automatic deletion is **not enabled** (404/readback false, rulesets 403).
   Any later branch action needs fresh facts and a separately authorized lead.

## Not tested, money, retrieval and release

Not tested: real-workspace qualification, arbitrary parser dialects, authenticated
actor identity, semantic equivalence/contradiction reasoning, distributed races,
full/shared/hosted CI, actual Mermaid rendering or remote deletion eligibility.
No claim of a production ontology, RDF/OWL/SKOS/PROV conformance or automatic
decision adoption is made.

Brain C pass `f890bb1cacc4` returned a curated SPARC card, not implementation
evidence. Earlier named source search found only `sparc/docs/tutorial.md`; one
query was routing-declined. Retrieval remains thin, not “brain pass clean.”
No cheap-route/checkpoint helper or cost receipt log exists. Actual paid spend
and savings are unknown; no model-tier routing or dollar savings were invented.
Session SQL persists/read-backs bind phases to actual local source commits.
No new memory namespace or external coordination writer was introduced.

No questions block this bounded handoff. Default recommendation: retain the
explicit unresolved/qualification limits and owner review. Private workspace
qualification and standards exports remain parked until separately requested.

**Ownership release:** after the final exact-head read-only verification and
handoff, this worker releases every owned path and performs no further writes.
The integration owner may then become the sole writer; this receipt is not a
lease, publishing capability, acceptance receipt or permission to delete refs.
