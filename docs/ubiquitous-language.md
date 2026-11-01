# Ubiquitous language

Established for this workstream on 2026-10-31 at the owner's request. This is the
shared vocabulary for code, tests, issues, reviews and user-facing explanations.
It consolidates the existing contract; it does not introduce a new schema or
claim that every contributor has reviewed it. Changes in meaning require an ADR
and the corresponding contract/test changes, rather than silently redefining a word.

Governance: [MADR-0002: establish and govern a shared ubiquitous language](adr/0002-ubiquitous-language.md).

## Authority and usage

The executable [ce.v1 schema](../schema/v1/record.schema.json),
[relation catalogue](../schema/v1/relations.json) and validators define wire
behavior. The [contract guide](../schema/README.md) explains its semantics.
This glossary names those concepts consistently. If they disagree, report the
conflict and reconcile the sources; do not silently use the glossary to weaken
validation. Domain knowledge that code does not establish stays unknown.

## Core terms

| Term | Project meaning |
|---|---|
| Capability | A named set of repositories examined together. It scopes exploration to those repositories and locked revisions; it is not a deployment or an inferred business function. |
| Repository | A source repository with a globally unique repository ID in this model. Several capabilities may refer to it. |
| Revision | An exact source commit. A branch or tag is a reference to resolve, not a locked revision. |
| Source snapshot | The result of checking capability sources and locking repository references to exact commits. |
| Revision selection | One chosen revision per repository in the explorer. It is a selection of source versions, not evidence of a deployed system. |
| Analysis unit | The extractor's project/module grouping emitted as one artifact at one revision. A repository can contain several units. |
| Analysis | Extraction under specific source, extractor/rule versions and configuration. Do not use as a synonym for capability or artifact file. |
| Artifact | One framed ce.v1 JSONL document for one analysis unit/revision: header, body records, final complete record. Truncation invalidates it. |
| Analysis key | Import identity: repository, commit, unit path, extractor name, extractor version and configHash. Same key/different bytes is a determinism error. |
| Symbol key | A symbol identity stable across line moves within the scheme's rules. An occurrence's artifact fixes the revision; key equality alone does not prove cross-revision continuity. |
| Occurrence | One observation of a symbol in a particular artifact. |
| Lineage | Explicitly matched possible continuity across revisions; not an identity invented by the extractor. |
| Node | A declared graph entity, for example a method, endpoint, routine, table or column. |
| Edge | A typed relationship claim with source/target, derivation, resolution and evidence. |
| Extracted fact | An evidence-backed observation. Its certainty is qualified by resolution and coverage; a candidate edge does not prove a runtime connection. |
| Evidence | Exact repository, commit, path and source span plus the normalized snippet hash supporting a claim. Evidence location is necessary, not proof of semantic correctness by itself. |
| Derivation | How a claim was produced: compiler, rule or human in ce.v1. LLM output is excluded from extractor artifacts. |
| Resolution | Target certainty: resolved, static-target, candidate or unresolved. Independent of derivation and human review. |
| Target candidate | A possible target, not a proven one. Say continuity candidate or concept proposal for those different contexts. |
| Coverage | Whether the unit's source was fully observed: full or partial, with explicit omissions and coverage-affecting diagnostics. Full does not mean every possible language feature is supported. |
| Not observed | Missing from partial analysis. It must not be reported as removed. |
| Unbound hint | A logical resource name scoped to repository/unit whose concrete identity is not established. Same text in different scopes is not the same resource. |
| Binding | A separate snapshot-level mapping from an unbound hint to a concrete resource. It adds derived edges without rewriting extractor observations. |
| Binding snapshot | Binding results for exact artifact inputs and mapping inputs. Distinct from a source snapshot. |
| Calculation | A representation of a code calculation with inputs, output, expression IR, evidence and attributes. Not a guessed business formula. |
| Calculation attribute | An evidence-qualified property with known, partial or unknown status. Unknown is not a default value to fill from names. |
| Calculation IR | Language-neutral expression structure preserving constants, numeric types, operators and operand order. Not algebraic-equivalence normalization. |
| irHash | SHA-256 of canonical JSON of the expression. Equal hashes identify equal serialized IR, not equal full artifact bytes or universally equivalent runtime behavior. |
| Term candidate | Read-only grouping by an exact normalized explicit name. It is navigation assistance, not a reviewed shared concept or assertion that values are interchangeable. |
| Glossary occurrence | One source observation identified by repository, revision, unit, symbol and role; calculation observations also retain a stable record discriminator. |
| Glossary concept | Read-only group of declarations with an equal stem (representation suffixes and plurals folded, nothing else), ranked by an explainable score. A concept is a navigation aid, not a reviewed domain model or an assertion that values are interchangeable. See [MADR-0005](adr/0005-glossary-concepts.md). Example: `Reseller`, `ResellerDto` and a web `ResellerDto` form one concept. |
| Concept tier | Presentation band of a concept: core, supporting or plumbing. Derived from the score; plumbing stays reachable with its reasons. |
| Concept feature | A boolean fact from the artifacts (for example persisted, message contract, role suffix) with a checked-in integer weight. Features are the explanation of a tier. |
| Attribute | A property, field or column nested under the concept that declares it. |
| Comparison completeness | Whether every possible calculation pair in a candidate was compared. A bounded partial comparison cannot establish absence of differences. |
| Interpretation proposal | An optional model-produced explanation or conceptual suggestion, separate from immutable extracted facts and validated against permitted input. |
| Review | Human judgment attached separately to a fact/proposal; it does not silently rewrite original observations. |
| Golden artifact | A curated expected artifact used for evaluation. It is a reviewed oracle, not infallible truth or output to regenerate to hide an implementation defect. |
| Evaluation gate | Executable comparison and thresholds that can fail. A passing gate says only what its actual assertions checked. |

## Context boundaries

These are existing implementation boundaries, not a claim of a completed formal
DDD redesign. Share the ce.v1 language across them; preserve each context's ownership.

| Context | Owns | Implementation |
|---|---|---|
| Capability/source acquisition | Definitions, repository identities, commit locking, read-only export | [Capabilities](../platform/src/CapabilityExplorer.Capabilities) |
| Extraction | Source analysis into ce.v1 records and calculations | [C#](../platform/src/CapabilityExplorer.Extractors.CSharp), [TS/SQL](../extractors/ts/src) |
| Contract/evidence | Framing, grammar, identities, relations, span verification | [schema](../schema), [Contracts](../platform/src/CapabilityExplorer.Contracts) |
| Evaluation | Reference comparisons, metrics and thresholds | [Evaluation](../platform/src/CapabilityExplorer.Evaluation) |
| Persistence | Immutable import, idempotence, stored binding snapshots | [Store](../platform/src/CapabilityExplorer.Store) |
| Binding | Service-map application and additional derived edges | [Binding](../platform/src/CapabilityExplorer.Binding) |
| Exploration | Revision selections, graphs, comparisons, presentation | [Explorer](../platform/src/CapabilityExplorer.Explorer) |
| Interpretation | Optional validated model proposals/explanations | [Interpretation](../platform/src/CapabilityExplorer.Interpretation) |

## Distinctions reviewers must preserve

* **Partial coverage** means incomplete observation; a **partial attribute** means
  only part of that property's meaning has evidence. Neither is a synonym for unknown.
* **Resolved extraction** and **resolved binding** retain distinct derivation and
  evidence. Binding cannot retroactively make an extractor's unresolved edge proven.
* **Deterministic artifact bytes** require identical explicit source/config/tool
  inputs. Cosmetic edits can change source/evidence hashes while leaving irHash
  unchanged. A renamed declaration can change symbol keys without changing IR.
* The schema permits **opaque IR**. A particular extractor may instead reject an
  unsupported calculation with a diagnostic. Never turn that policy into a fabricated
  approximate formula; state the supported subset explicitly.
* An identifier named `currency`, `amount_ore` or `price` is not proof of a currency
  domain, monetary unit or conversion. Division by 100 proves scaling, not money.
* Say **proposed ADR**, **planned work package** or **interpretation proposal**.
  None implies accepted architecture, completed implementation or reviewed AI output.
* **Queen**, **worker**, **swarm** and **hive memory** describe the development
  coordination process, not entities in the capability-explorer domain model.

## Applying the language

The proposed PostgreSQL read model adds precise implementation terms:

* **Analysis snapshot**: one immutable selection of exact artifact IDs and default
  revisions. It is not a capability's Git acquisition snapshot or a deployment.
* **Candidate analysis snapshot**: a stored selection not yet visible through the
  publication pointer. Valid imported facts alone do not publish it.
* **Published analysis snapshot**: a selection assigned a generation by a successful
  compare-and-swap publication. Retained published snapshots remain pageable.
* **Publication generation**: a monotonically increasing counter per capability or
  workspace scope; it is not a Git revision or a model-training epoch.
* **Continuation cursor**: signed, expiring page position bound to snapshot and
  query selection. It never grants authorization or changes the selected facts.

Use these terms in new public interfaces, diagnostics, tests and GitHub work claims.
A new overloaded term needs a definition and example here before becoming a public
API. If code/schema meaning changes, update its tests, contract guide and glossary
in the same reviewed change. Proposed future concepts must be labelled proposed.

For example, say “an unresolved REQUESTS edge with a configuration hint was bound
by a human service map in this revision selection,” not “we proved the deployed
services call each other.” Say “the SQL unit has full graph coverage but this
calculation is unsupported,” not “the analyzer understands all SQL.”
