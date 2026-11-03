# Portable decision ledger: executable pseudocode

Status: proposed bounded algorithm. Configured canonical sources retain authority;
this repository authors Markdown, while another profile may author JSON.

```text
load config and sidecar -> reject unknown keys, versions, adapters, vocabularies
resolve relative project roots beneath explicit caller root; forbid symlink escape
discover configured .md files, excluding only explicitly named navigation files
  or configured canonical JSON collections; archive/generated exclusions explicit
  -> fail on unreadable input, duplicate locator or file/byte budget exhaustion
for every source:
  decode strict UTF-8; extract a single H1 and bounded header/frontmatter scalars
  preserve exact raw status, metadata and source bytes
  match exactly one sidecar locator -> stable (project, record), never numeric alias
  verify snapshot SHA256 against bytes actually read, not another declared hash
  map exact raw status through project policy, otherwise unknown + diagnostic
  verify title/status evidence line spans, quote bytes and snapshot binding
  verify explicit authority and exact scope; acceptance needs a separate receipt
for every asserted edge:
  require source evidence + scope + authority; no inferred-edge import path
  resolved target -> require existing project-qualified identity and current revision
  private/unresolved target -> retain explicit diagnostic; never pretend resolved
  validate relation-specific coverage and per-type cycle policy
  supersession covers target's whole declared scope, not an arbitrary scope subset
sort keys and record/edge collections by binary string order, not locale
derive complete JSON, Markdown and Mermaid projections from this one inventory
check -> byte compare only, missing/stale output fails without mkdir/write
generate -> require explicit configured output directory; never author decision text
```

Precedence and executable positive/negative relation examples live in
[`examples.json`](../../config/decisions/examples.json):

```sh
node scripts/decisions/check.mjs pseudocode
```

## Semantics, errors and complexity

All relations are many-to-many; no uniqueness beyond an assertion ID and no
cardinality restriction is inferred from a diagram. `related-to` is semantically
symmetric for navigation, but its source assertion is emitted once, not as an
invented reverse assertion. No relation is promoted to a transitive assertion.
`extends` adds a decision without replacing it: not taxonomy `broader`.
`references` is documentary, not an ordering constraint.
Source-directed `related-from` is distinct from symmetric `related-to`; configured
inverse supersession declarations corroborate one edge rather than duplicating it.
`full-supersedes` replaces the whole target scope; it is not identity, including
across projects. `scoped-amends` changes part of a decision without declaring a
winner. `scoped-overrides` takes precedence only in the explicitly named scope.

Cycles are allowed for references, related-to and scoped amendments. Cycles are
rejected separately for depends-on, extends, scoped-overrides and full-supersedes.
Mixed-type walks have no implicit cycle prohibition and no derived implications.
Iterative DFS handles configured bounded graphs; no unbounded traversal is exposed.

Expected cost is O(B + (V+E) log(V+E)) for parsing/canonical sorting and O(V+E)
per fixed relation type for cycle checks. Memory O(B+V+E) within configured input
budgets. Every unsupported/ambiguous parse, stale hash, duplicate identity,
uncovered source, dangling resolved target, unknown field or invalid cycle fails
with a named diagnostic. Unknown source status is *reported* unknown; an explicit
sidecar claim of proposed/accepted for it fails. Unavailable external targets are
explicit unresolved/private references, not dangling resolved nodes.

There is no heuristic link extraction, status-prefix matching, graph database,
history rewrite or network resolution. Evidence proves the passage exists at
the bound snapshot; it cannot prove the passage's interpretation or actor identity.

## P gate evidence and limitations

Gate executes examples against the same `relationError` used by implementation,
checks exact precedence and both permitted/rejected cycles. Score 96/100,
iteration 1; four points withheld for no production history adapter or remote
authority authentication. Those remain explicit unsupported questions, not flaws
hidden behind a higher score. Full source integration is deferred to A/R.
Brain pass remains documentation-only SPARC guidance, not verified new tooling.
