# Evaluate tools independently before introducing dependencies

* Status: proposed for repository-wide adoption; applied to issue #31 by operator instruction
* Date: 2026-11-02
* Related: [issue #31](https://github.com/Sonberg/capability-explorer/issues/31), [main-only PRs](0016-main-only-pr-integration.md), [source-bound validation](0011-source-bound-security-validation.md)

## Context and problem statement

Repository insight tools have different purposes: semantic extraction, inventory,
context packing, security analysis and model interpretation. A single leaderboard
or bundled dependency PR can obscure overlap, unsupported claims and operational
cost. Existing compiler-backed facts must remain distinguishable from heuristic
derivations and model proposals. Historical desk scores are not runtime evidence.

## Decision drivers

Frugal implementation, independently demonstrated marginal value, deterministic
where applicable, source-bound evidence, small reviewable PRs and owner merges.

## Considered options

1. Introduce every promising tool together and evaluate afterward.
2. Rely on desk scores or one reviewer to select dependencies.
3. Use independent per-tool councils, separate evidence gates and one tool/mode
   per introduction PR, preceded by a dependency-free validation foundation.

## Decision outcome

Choose option 3 for this workstream. Each tool/mode receives distinct SME,
adversary, integration/cost advisor and non-author judge reviews. Council role
records bind the proposal, claim references, briefs, blockers and verdict to one
canonical packet digest. A change invalidates affected prior judgments.

Desk completion is different from a passing trial, introduction decision or PR
gate. Retain/reject/defer may complete an evaluation without passing later gates.
Runtime claims require matching artifact/configuration/corpus/oracle/receipt
bytes. Safety, license and provenance failures override weighted scores.

Introduce at most one accepted tool/mode per PR, with its thin opt-in adapter,
tests, rollback and appropriate documentation. Target fresh main; dependent
introduction PRs wait for the foundation's owner merge. No auto-merge or direct
main writes. Independent desk evaluation may continue while PR review waits.

Use current host capacity within the configured eight-agent project limit;
schedule independent council roles sequentially when fewer slots are available.
Do not simulate several independent identities in one agent response.

The initial budget and package order are in the
[council protocol](../plans/code-insights-tool-council-protocol.md). No new paid
inference, private-code upload or analyzed-source execution is authorized.

## Validation and limits

`make EXECUTION=sandbox test-research-foundation` checks arithmetic, sealed source
anchors and negative cases for stale/missing receipts, identity drift, conflicting
roles, silent verdict changes and bundled PR records. Ordinary local act/security
and PR checks remain required before publication.

The foundation validator checks data integrity, not remote URL truth, independent
agent authentication, process execution or cryptographic attestation. A consistent
maliciously fabricated receipt cannot be authenticated by a digest. Human/agent
review of underlying evidence remains necessary. Evidence files are bounded to
8 MiB each; larger artifact qualification needs a separately reviewed extension.
The reader assumes an isolated quiescent checkout, not a hostile concurrent writer.
This is not a distributed release authority or branch-protection mechanism.

## Consequences

Smaller PRs and explicit revalidation reduce conflict and adoption risk. Tools
may be rejected or deferred despite strong marketing or historical scores. The
full study remains incomplete until its per-tool and research evidence exists;
passing the foundation does not close issue #31.
