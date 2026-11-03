# Portable decision ledger: proportionality and architecture

Status: proposed; portable mechanics, not real-workspace qualification.
Public sources fetched 2026-11-03. No private workspace contents are reproduced.

## Options against actual competency questions

| Option | Scope-active / explicit depend/amend | Staleness, unreviewed and equivalent aliases | Maintenance / portability / reversibility |
| --- | --- | --- | --- |
| Authored Markdown plus hand-maintained index | Human-readable rationale; scope and relation semantics require reading prose | Index drifts; raw hyperlinks cannot prove prerequisites; duplicate aliases ambiguous | Cheapest authoring; insufficient repeatable provenance/inventory checks |
| Typed JSON sidecar + faceted navigation + canonical-source profiles | Exact-scope acceptance candidates and typed asserted edges; no hidden closure | Read actual source bytes; show unknowns, unreviewed assertions, alias collisions and direct invalidations | Selected minimum: Node built-ins, bounded adapters, reviewed data; regenerate disposable projections |
| RDF with SKOS vocabulary and PROV-O provenance | Useful shared identifiers, concept schemes and entity/activity/agent interchange | Would still need project-specific status, scope, decision relations and source verification | Reconsider when an actual RDF consumer/query needs it; an exporter is reversible, but unrequested now |
| Full OWL ontology/reasoning | Can formally encode class/property axioms and reason under chosen semantics | Does not establish human acceptance, runtime truth or evidence freshness | No present competency question requires class inference; extra expertise/tooling and open-world interpretation are disproportionate |

Answers implemented: source-recorded acceptance candidates by **exact** scope;
direct explicit edges; source-change invalidation of receipts/endpoints; unknown,
unimported, private and unreviewed reports; project-qualified alias collisions.
Not implemented: authoritative semantic contradiction adjudication, equivalence
of decisions, authenticated approval, transitive impact prediction or ontology
classification. Multiple accepted records in one scope trigger review, not a
fabricated contradiction. Equal local IDs are not semantic equivalence.

## Source rules versus project extensions

* [MADR](https://adr.github.io/madr/) defines a lean decision-record format.
  Its [4.0.0 template](https://raw.githubusercontent.com/adr/madr/4.0.0/template/adr-template.md)
  makes metadata optional and includes status/date/decision-makers. Category
  guidance is not a universal ADR ontology. Our required sidecar receipts and
  exact raw-status mappings are **project extensions**, not MADR conformance.
* [SKOS Primer](https://www.w3.org/TR/skos-primer/) distinguishes concept schemes,
  grouping, hierarchy, associative links and cross-scheme mappings.
  [SKOS Reference](https://www.w3.org/TR/skos-reference/#semantic-relations)
  defines `related` symmetry and separates `broader` from its transitive closure.
  SKOS is formally an OWL ontology with RDF data. Borrowing these distinctions
  does **not** make this JSON SKOS-conformant. `extends` is not `broader`;
  supersession is not `exactMatch`; similar wording is not identity.
* [PROV-O](https://www.w3.org/TR/prov-o/#description) distinguishes entities,
  activities and agents. Here source snapshots, assertions and recorded agents
  are separated, but there is no PROV-O implementation/conformance claim.
* [OWL 2 Overview](https://www.w3.org/TR/owl2-overview/) describes formal classes,
  properties, individuals, semantics and profiles. Those capabilities are
  real, but not necessary for the bounded queries above.
* [GitHub automatic deletion](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/configuring-pull-request-merges/managing-the-automatic-deletion-of-branches)
  requires admin permission; protections/rules may prevent deletion. It is
  separate from auditing old refs or deleting local branches/worktrees.

## Contract and authority

`decision-ledger.v1` identities are `(project, stable record id)`, encoded as a
JSON tuple to avoid separator collisions. Local aliases may collide. Source
locators are relative path plus optional JSON pointer; revisions are exact
`sha256:` byte snapshots, **not** Git commit claims or authenticated reviews.
Moving a file preserves an explicitly assigned ID. Changed bytes invalidate
the record, endpoint pins and any evidence bound to its old snapshot.

`ledger.schema.json` is a strict versioned interchange schema. The offline
validator implements **only its audited keyword subset**: local `$defs/$ref`,
type, enum, const, properties, required, additionalProperties, items, minItems,
uniqueItems, minimum, maximum, pattern and minLength. Unknown schema keywords
fail rather than being ignored. It is not a general JSON Schema validator.
Runtime additionally validates inventory, vocabulary, source spans/pointers,
raw status mappings, acceptance scope, target pins and relation semantics.

Each facet and edge has standing, scope, recorded authority and source evidence.
Facet membership is multi-valued and controlled by configuration; missing
categories remain unclassified. Neither titles nor folders create categories.
Source passage verification proves a declaration exists, not its underlying
truth. `review-recorded` is not cryptographic actor authentication.
Implementation/verification is **not assessed** by this MVP; source fields remain
visible and do not become accepted/qualified decisions.

## Configured canonical sources and adapter limits

For this repository, authored Markdown is canonical. Other projects may choose
canonical JSON; Markdown rendered from it is a projection and must be excluded
from discovery. A configured canonical/adapter mismatch fails.

* `markdown-header-v1`: one H1, scalar `Status:` before first H2, plain or `*`/`-`
  metadata. No prose relationship mining. Unknown scalar metadata is preserved
  and reported uninterpreted; ambiguity/missing title is rejected.
* `frontmatter-scalars-v1`: one H1 plus explicitly bounded scalar frontmatter.
  Not a full YAML parser; nested/list/tag/block syntax fails. A shared-adoption
  pointer can be retained as uninterpreted metadata, not imported acceptance.
* `json-collection-v1`: configured JSON pointer to an array of objects, configured
  scalar local-ID/title/status field names, explicitly configured array-valued
  relation fields and direction. Strict JSON rejects duplicate keys. Exact field
  pointers plus full-document line spans and hashes bind declarations. The
  deliberately broad span is not a claim of precise JSON token line tracking.
  Unknown metadata is preserved. Archives/generated trees are config exclusions.
  Other JSON dialects need a separately qualified adapter, never guesses.

Explicit raw `Implemented` mapping is required; it does not imply accepted.
Directed JSON related links use `related-from`, not symmetric `related-to`.
Configured inverse `superseded_by` declarations can corroborate a single
normalized supersession edge; duplicate normalized edges fail. No extra reverse
assertion is manufactured. Unimported declared relations are explicit diagnostics.
There is no production-workspace qualification or external dereference.

## Relation policy

All types are many-to-many, non-transitive as **assertions**, with no identity
implications. `related-to` alone supports symmetric navigation. `related-from`
is source-directed and nonreciprocal. References and scoped amendments allow
cycles; depends-on, extends, scoped-overrides and full-supersedes reject cycles
per type. Mixed-type walks do not create constraints. Supersession requires
whole target scope; overrides remain partial. No OWL reasoning is invoked.

## CLI and integration boundary

```sh
node scripts/decisions/check.mjs specification
node scripts/decisions/check.mjs pseudocode
node scripts/decisions/check.mjs architecture
node scripts/decisions/check.mjs all
node scripts/decisions/check.mjs generate --dry-run
# Integration owner, only after choosing the projection location:
node scripts/decisions/check.mjs generate --output docs/research/decision-ledger-preview
node scripts/decisions/check.mjs check --output docs/research/decision-ledger-preview
```

`--root` and `--config` select a different bounded project/config. Config paths,
project roots, source paths and output must remain within that explicit root.
`check` never writes, including for absent/stale projections. `all` runs S/P/A
and launches the focused test suite once. The lead can wire a single cheap make/
offline CI target around `all`, without running the focused suite twice.
No new dependency, shared manifest, main index or existing decision is edited.
The lead owns shared lifecycle amendments and the exact branch-retention clause.
This proposal changes no existing lifecycle authority by itself.

## A gate and remaining limits

The A gate audits the schema subset and loads/reconciles the real repository
inventory; S/P are re-run separately. Score target 96/100 only after these actual
commands pass. Final A gate: 96/100, iteration 2. First enrollment rejected H1
examples inside an existing fenced shell block; the parser now excludes fenced
code rather than rewriting the historical source. S/P/A and dry-run generation
then passed: 28 records, zero invented edges, 75 explicit diagnostics.
Four points reserved for unqualified external dialects and no
independently authenticated acceptance. Unsupported inference stays unsupported.
Brain grounding is a curated SPARC card, not implementation proof; no claimed
clean retrieval, route savings or measured spend. Integration/full/hosted CI and
Mermaid render remain lead-owned gates, not this worker's evidence.
