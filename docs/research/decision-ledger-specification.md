# Portable decision ledger: specification

Status: research and implementation contract for review, not decision acceptance.
Baseline: `973fa06e6f2d623acadfe49445ac4b74b738b90e`.

## Competency questions and acceptance

The seven observable obligations and intended negative outcomes are recorded in
[`requirements.json`](../../config/decisions/requirements.json). The executable
specification gate validates uniqueness, complete obligation coverage, fixture/test
traceability and all thirteen fail-closed branch guards:

```sh
node scripts/decisions/check.mjs specification
```

1. List accepted decisions only for an exact, explicitly evidenced acceptance
   scope. Show proposed/unknown/contradictory claims without adjudication.
2. Resolve a project-qualified stable record ID independently of numeric alias,
   path and revision. Show external unresolved/private targets explicitly.
3. Keep references, related-to, depends-on, extends, scoped-amends,
   scoped-overrides, and full-supersedes distinct. Never materialize inference.
4. Bind metadata, authority, acceptance and relationships to exact source bytes
   and line spans. A source change invalidates its receipts. Unknown status and
   unsupported adapters are unresolved, not implicitly proposed or accepted.
5. Account for all configured decision files exactly once, preserve their
   titles/raw statuses, and derive node colors from a closed state vocabulary.
   Reordering input cannot change bytes. Checking stale output cannot write it.
6. Exercise two synthetic layouts, Unicode, spaces, mixed metadata, duplicate
   numeric IDs across projects and a rename. Do not copy private workspace data.
7. Publish only a deletion eligibility policy, not a deletion tool. Unknown
   permissions, protection, provenance or ownership mean retain. Fresh exact-SHA
   recheck and independent authorization remain mandatory even after eligibility.

## Source inventory and migration

Current main contains 27 decision files, including two distinct local `0005`
aliases and no `0015`. This is a baseline observation, never a validator constant.
`README.md` is navigation, not a decision. Exact raw status variants include
operator/workstream application alongside proposed repository adoption. `0016`
records operator-workflow acceptance; it does not accept every project scope.
`0021`–`0023` use dash metadata; most others use asterisk metadata. The new
`0028` remains proposed. Source Markdown is not rewritten or normalized.

The migration sidecar explicitly assigns opaque stable IDs and exact source
snapshot hashes; paths remain relocatable locators. A reviewed path change with
unchanged bytes/identity is a move, not a new decision. Missing/extra/duplicate
files and stale snapshots fail. No hyperlink is imported as a semantic edge.
Existing implementation claims are not converted into qualification receipts.

## Boundaries and uncertainties

The bounded Markdown adapter reads one H1 and one scalar Status metadata value;
the bounded frontmatter adapter supports scalar metadata, not arbitrary YAML.
Original metadata is retained. Ambiguous/malformed supported metadata fails;
unknown scalar fields remain visible as uninterpreted metadata, not acceptance.
No Git history resolver, network dereference, OWL reasoner, graph database,
Ruflo runtime dependency, recursive prerequisite inference or private source
reader is required. Snapshot semantics identify exact bytes, not a signed human
review or an authenticated actor.

Current source has many textual mentions but no reviewed sidecar edges yet.
The inventory must report zero asserted relationships honestly, not manufacture
an ontology from the existing Mermaid groups. Synthetic fixtures exercise
reviewed edge mechanics. Unreviewed semantic equivalence, real project adoption,
and scope conflict resolution remain human tasks.

## Prior art, authority and ownership

Reuse the separation of source facts, review and qualification from
[`domain-object-specification.md`](domain-object-specification.md), and explicit
adapter/discovery boundaries from
[`language-neutral-frontends.md`](language-neutral-frontends.md). Do not import
their larger abstractions or claim their proposals adopted.

Only new `schema/decisions`, `config/decisions`, `scripts/decisions`, focused
tests/fixtures, this research family and proposed MADR-0028 belong to this worker.
The integration owner alone changes build/CI, main index, changelog, handoff and
existing lifecycle MADRs. Existing Markdown remains authoritative.

Branch receipt supplied by lead: authenticated Tigon32 can push/triage but cannot
administer/maintain; automatic deletion PATCH returned 404, readback remained
false, ruleset inspection returned 403. All 17 conditional deletions deferred.
No setting was enabled and no branch deletion is performed by this package.

## Phase S evidence and limitations

Gate: `node scripts/decisions/check.mjs specification`.
Score: 96/100, iteration 1. Four points withheld: synthetic portability is not
real-workspace qualification; remote eligibility cannot be established under
the current permission boundary. Neither is silently claimed.
The gate checks a specification contract, not implementation correctness.
No remote mutation, shared integration or full CI was tested in this phase.
Brain retrieval returned SPARC documentation from a 35-day-old corpus, no
implementation proof; useful methodology only, not a clean capability pass.
No cheap-route/checkpoint helper exists; spend is unavailable, no savings claimed.
