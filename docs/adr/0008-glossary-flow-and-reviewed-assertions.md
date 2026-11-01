# Separate evidence navigation from reviewed glossary assertions

* Status: applied in this workstream; proposed for owner review
* Date: 2026-11-01
* Related: issue #4 layers 4/3; MADR-0002, MADR-0004, MADR-0006/0007

## Context and problem

Candidates group names, not business meaning. Users need to navigate cross-repository
connections and record intended equivalence without turning a call graph into
proven field-level value flow. Review also needs to survive storage/restarts while
remaining honest when the analyzed revision changes.

## Decision drivers

Determinism, source identity, explicit uncertainty, auditable human decisions,
bounded work, local deployment and existing pipeline/UI reuse.

## Considered options

1. Infer semantic equivalence and propagation from names or topology.
2. Preserve source/binding navigation and add separately attributed human assertions.
3. Delay all navigation until full data-flow extraction exists.

## Decision outcome

Choose option 2. A bounded graph starts from exact glossary occurrence IDs and
retains repository, commit, unit, symbol, evidence and relation direction.
Calls, read/write links, channels and service bindings provide context only; they
do not establish field equality or runtime traffic. Explicit calculation input
origins can establish a dependency, not identity of values. No same-name resource
joins or guessed cross-repository mappings are introduced.

Terms are human-authored review hubs; same, alias and derived_from memberships
remain distinct. Same/alias are associations, not directional source flow;
derived_from points from occurrence to the term derived from it. A shared term
never silently materializes pairwise proven-value edges. Imported artifacts and
extraction certainty are immutable. The engineering ubiquitous language remains
separate from the analyzed-repository Glossary tab.

Each saved link stores the logical symbol reference and the exact reviewer-observed
occurrence/revision/artifact content hash. Only exact current provenance contributes
reviewed graph edges. Reanalysis/revision changes become stale, missing symbols
orphaned, multiple matches ambiguous, and excluded repositories out of scope.
Re-review requires an explicit update against a selected current occurrence.

PostgreSQL stores versioned terms, links and mismatch issues with transactional
append-only audit events. Create IDs are client-generated for retry identity;
updates/deletes use expected versions and return conflicts without overwriting.
Soft deletion preserves history; deleting a term retires its children atomically.
Terms are global; capability selection scopes source references, not authorization.
Audit writes are append-only through the application, not tamper-proof against a
database administrator.
Reviewer names are supplied attribution, not authenticated identities. Resolving
a human issue does not fix or rewrite the source facts it concerns.

New write endpoints are for the existing trusted local deployment: JSON plus a
custom write header, allowed local host and same-origin browser checks. These
prevent ordinary cross-site browser requests/DNS-rebinding hosts; they are not
user authentication. Remote multi-user exposure needs a separate authenticated
access-control decision. Default loopback binding and sandbox port publication
remain unchanged. Database absence is read-only; configured database failure is
an error, never simulated successful persistence.

Navigation defaults to 4 hops/250 nodes/500 edges, hard limits 8/500/1000; requests
outside limits fail. Graph and review-list truncation are disclosed. Stable ordering
and exact source inputs yield stable navigation; human audit timestamps are not
part of that deterministic projection. Existing canvas/source viewer and sandbox
images are reused. No model inference, new renderer or artifact schema version.

## Consequences and validation

Useful navigation arrives before full value-flow extraction; incomplete paths must
remain visibly incomplete. Human review requires explicit revalidation after input
changes. The interface introduces additional endpoints and an additive migration;
existing artifact/CLI/HTTP behavior remains under its current tests. A release bump
is assessed under MADR-0007; this implementation does not publish a version.

Validation requires traversal identity/cycle/ordering/limit negatives, actual
PostgreSQL transaction/concurrency/audit tests, API origin/validation/failure tests,
and real-browser no-database and persisted review journeys. Build evidence and
untested limitations belong in the execution receipt, not invented scores here.
