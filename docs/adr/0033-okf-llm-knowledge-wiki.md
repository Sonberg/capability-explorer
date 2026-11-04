# Maintain reusable knowledge as an interlinked OKF LLM wiki

* Status: proposed
* Date: 2026-11-04
* Scope: knowledge maintenance, not product storage
* Related: [wiki](../wiki/index.md), [playbook](../wiki/maintenance.md)

## Context and decision drivers

Disconnected research reports require readers to reconstruct knowledge across
runs. Contributors need reusable, source-attributed understanding with explicit
uncertainty, reviewable history and low runtime cost.

## Considered options

1. Dated reports as the primary knowledge interface: poor cross-source discovery.
2. One folder containing raw sources, runtime ledgers and knowledge: conflates
   custody, execution state and synthesis.
3. Topic-centered Markdown knowledge over separately controlled sources:
   selected as the proposed maintenance approach.
4. Mandatory wiki application/vector service: unnecessary for this convention.

## Decision outcome

Use `docs/wiki/` for connected concepts, research, terminology, comparisons,
contradictions, decisions, open questions and readable source summaries.
Preserve provenance and distinguish observations, interpretations, requirements
and accepted decisions. Keep canonical MADRs in `docs/adr/` and implemented
architecture in `docs/architecture/`.

Follow OKF 0.2 with source-keyed footnotes, progressive indexes and an ISO-dated
log. The local profile adds titles, descriptions, tags, lifecycle and generation
metadata. See the [playbook](../wiki/maintenance.md) for precise rules.

Raw/private inputs and execution ledgers are not the public knowledge interface.
Do not publish restricted evidence or private Git ancestry. Public summaries
require an independent publication review; source permission is not a general
redistribution grant.

Update existing affected topics and links rather than append disconnected
reports. Structural validation uses `python3 scripts/check-wiki.py` and its
`--self-test` mode. Passing lint is not semantic verification or owner acceptance.

## Consequences

Knowledge remains portable and readable without a new service. Contributors
must maintain links, qualifications and source freshness. This proposed decision
does not approve business rules, runtime tooling, data contracts or publication.
