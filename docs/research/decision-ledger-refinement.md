# Portable decision ledger: refinement evidence

Status: bounded worker verification; not acceptance, publication or full CI.
Architecture source commit: `11cda5b`.

## Actual commands and outcomes

```sh
node --test tests/decision-ledger.test.mjs
# First iteration: 24 tests, 24 passed, zero failures.
node scripts/decisions/check.mjs all
# Final refinement iteration: 27 tests, 27 passed, zero failures/skips.
# S/P/A execute, then the focused node:test process executes exactly once.
node scripts/decisions/check.mjs architecture \
  --root tests/fixtures/decision-ledger --config project.json
# 7 synthetic records, 5 asserted edges, 16 explicit diagnostics; PASS.
git diff --check
# PASS.
```

Final repository reconciliation: 28 records, zero invented asserted edges,
75 explicit diagnostics (including unclassified and uninterpreted metadata).
These are observed integration counts, not checker constants.

## Criterion-to-test trace

The executable specification now verifies actual named tests and configured
fixture adapter profiles, not just planned strings.

| Obligation | Focused evidence |
| --- | --- |
| 1: proportional competency answers and honest unsupported questions | scope qualified acceptance; conflicting acceptance; projection inventory titles colors and links |
| 2: project/record/revision identity and target resolution | portable identities and rename; external target states; JSON collection local aliases cannot be ambiguous |
| 3: controlled facets, distinct typed relations, no inference | relation cycles; scope and relation semantics; directed JSON relations and inverse evidence; unimported typed declarations remain visible |
| 4: authority/scope/evidence and uncertainty | source change invalidates; evidence passage hashes cannot self-attest; unknown metadata; JSON acceptance needs an explicit configured field |
| 5: complete deterministic inventory/views and read-only stale failure | inventory coverage; canonical projections; readonly stale check; projections cannot overwrite canonical inputs or enter discovery |
| 6: heterogeneous portable source profiles | heterogeneous adapters; portable identities and rename; parser support boundaries fail closed; strict JSON source boundaries |
| 7: guarded branch lifecycle POLICY only | branch eligibility policy; thirteen mandatory boolean facts; unknown/false/string values retain |

Two heterogeneous fixture layouts use authored Markdown and canonical JSON
collections. A third frontmatter/adoption-pointer profile covers that additional
boundary. Values are synthetic; no private paths, records or claims were copied.
Numeric `0005` occurs independently in all three projects. Unicode titles,
space-containing paths and a rename with parentheses/hash punctuation are tested.
Archived JSON and generated Markdown are excluded, never a second authority.

## Refinement history and score

Final bounded R score: **96/100**, four iterations:

1. 24/24 tests passed. Established inventory, source-change, scope, relationship,
   adapter and read-only projection coverage.
2. 25/25 passed after self-review strengthened actual test/fixture traceability,
   protected canonical inputs from output, encoded URL punctuation, preserved
   separate private/unresolved graph targets and reported facet review state.
3. 26/26 passed after adding explicit ambiguous JSON alias rejection and
   canonicalizing output-root checks; output symlink ancestors remain forbidden.
4. 27/27 passed after requiring a configured JSON acceptance field, rejecting
   unsupported acceptance qualifiers and preventing unknown metadata from becoming
   a receipt merely because it contains similarly named fields.

No phase-advancing score was assigned to an incomplete refinement. The four
points withheld are the two explicit qualification gaps: synthetic dialects do
not qualify a real workspace (2), and recorded authority is not authenticated
approval or semantic truth (2). The self-score is not independent authorization.
The relayed independent portability report materially changed the design:
configured source authority, directed related links, inverse corroboration,
archives/generated exclusions and explicit implementation/verification limits.

## Parser and projection boundaries

Source-declared Markdown edges require literal source/target stable IDs, relation
token and scope in the bound passage (and target project for cross-project
declarations). This is a bounded typed-declaration convention, **not** natural
language understanding. Other reviewed interpretations must explicitly use
review-proposed/review-confirmed standing, with recorded authority and evidence;
the checker cannot independently verify their semantic interpretation.

The JSON profile explicitly maps local-ID/title/status, optional acceptance
field, relation fields and direction. Acceptance's supported source object is
exactly `{scope, agent}`; extra qualifiers require a different qualified profile,
not silent ignoring. Canonical JSON IDs must be unique within the configured
collection namespace/project; use separate namespaces for overlapping collections.
Unknown implementation, verification, category and adoption metadata is retained
and reported, not used to infer approval or facets.

Generated output must not overwrite config/sidecar/source files or enter a
canonical discovery tree unless that generated directory is explicitly excluded.
These checks apply even to dry runs. Checking stale output never writes or mkdirs.
All owned source, schema, data and tests remain below 500 lines.

## What was not tested

No real-workspace adapter qualification, arbitrary YAML/JSON dialect, semantic
equivalence/contradiction reasoning, authenticated reviews, remote branch facts,
distributed concurrent writers, full project CI, hosted exact-head checks or
Mermaid parser/render has been established by these focused tests.
The integration owner retains make/CI/navigation/lifecycle changes and publication.

Brain R pass (`8906d3d8d4fc`) returned only a curated SPARC card. A preceding
architecture query was routing-declined; a narrowed query found the documentation
`sparc/docs/tutorial.md` (snapshot hash
`1b4056deffbb5906dba8a21559ec7069c64ff24c367ba014033df5fe6ff94406`).
That supports honest source-bound gates, not a new running verifier. Retrieval
was thin/documentary, not “brain pass clean.” No orchestration package was added.
Cheap-routing/checkpoint helpers and spend receipts are absent; measured spend
and savings remain unknown. Session SQL checkpoints record actual source commits.
