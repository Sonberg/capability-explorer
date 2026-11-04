---
type: Qualification Research
title: Bounded qualification of authored models and static candidates
description: Exact package metadata, static-analysis scope and explicit implementation blockers.
status: draft
tags: [research, bpmn, dmn, roslyn, limitations]
generated: {by: copilot-cli/1.0.105, at: "2026-11-04T01:15:00+01:00"}
sources:
  - id: bpmn
    resource: https://registry.npmjs.org/bpmn-moddle/10.3.1
  - id: dmn
    resource: https://registry.npmjs.org/dmn-moddle/12.2.1
  - id: cfg
    resource: https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.flowanalysis.controlflowgraph?view=roslyn-dotnet-4.9.0
  - id: block
    resource: https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.flowanalysis.basicblock?view=roslyn-dotnet-4.9.0
  - id: fixtures
    resource: ../../fixtures/src
---
# Result and evidence ceiling

The bounded round supports precise parser candidates and a smaller static
analysis slice. It does **not** establish parser compatibility, XML safety,
runtime conformance or equivalent executable DMN.

Three read-only research scopes ran in parallel. Six external document requests
were permitted and used: four parser requests (two successful, two HTTP 404)
and two successful API-document requests. The local reuse scope made no network
requests; its private provenance is excluded from this public export.
No packages were installed, XML parsed or extractor prototype executed.

An isolated local swarm/hive ledger was initialized before workers. Host
researchers were registered as external aliases, not native headless workers.
Shared-memory availability was not uniform across worker environments; no
operational consensus, shared-memory execution or durable coordination proof is
claimed. The ledger does not replace these read-only reports.

# Authored model parsers

Registry metadata retrieved through the `latest` endpoints on 2026-11-04:

| Candidate | Declared scope/license | Registry `gitHead` | Registry tarball SHA-1 |
|---|---|---|---|
| `bpmn-moddle@10.3.1` | BPMN 2.0 wrapper; MIT | `f35959afc443444b706a4f39542530134233db07` | `e674706d6b039a9ea88129788e6df91842aaf832` |
| `dmn-moddle@12.2.1` | DMN 1.3 wrapper; MIT | `75e2f759fa3e168a2a098358825ede2a0de02282` | `0096ebc2c9dda556d810404a2346ac864852387c` |

These are registry declarations, not independently verified package bytes,
license-file review, source behavior or signed release identities. Version URLs
are source locators, not additional retrievals.[^bpmn][^dmn]

Both attempted pinned `src/index.js` raw-source URLs returned HTTP 404. The
worker stopped at its cap instead of guessing more paths. Therefore namespace,
extension, warning and validation behavior remain unverified. DMN 1.3 metadata
does not establish compatibility with later DMN namespaces.

Required implementation gate in #12:

* Pin exact packages and dependency lock; verify package integrity and applicable
  notices. Inspect the actual packaged descriptors/parser implementation first.
* Start with a minimal BPMN start/task/end model and a minimal DMN 1.3 decision
  table, then verify an explicit namespace/extension support matrix.
* Test malformed/truncated XML, unknown namespaces/extensions, duplicate IDs,
  missing references, nesting/count/byte limits and DTD/external entities.
* Fail or report unsupported/partial content explicitly; preserve original bytes
  and ordered decision semantics. Disable network/entity/model/expression
  execution and prove that behavior rather than assume a library default.
* Round-trip assertions must compare semantic structure, not original-byte
  equality. No claim of full XSD validation or process soundness follows.

This round did not retrieve viewer licenses. Viewer/modeler adoption requires a
separate applicable-license check; parser metadata cannot license a viewer.

# Static candidate slice

Roslyn documents CFG as operations in basic blocks, entry/intermediate/exit
blocks and explicit branches. BasicBlock documents ordered operations, a branch
value, conditional successor and fall-through successor.[^cfg][^block]

The inspected C# fixture manifests use nullable-enabled `net10.0`. In the
bounded `api-cs`/`forecast-cs` fixture scope, synchronous calculation helpers do
not form the desired representative orchestrator/classifier pair, while the
multi-step forecast endpoint uses `async`/`await`. Use new synthetic examples;
do not claim the existing corpus already supplies the oracle.[^fixtures]

The first slice is deliberately synchronous and intra-method. This is a scope
choice, not a claim that Roslyn cannot represent async code. No assertion about
compiler-lowered async state machines is supported by this round.

Define independently reviewed semantic expectations **before** extraction:

| Synthetic example | Observable expected behavior, not compiler block counts |
|---|---|
| Synchronous orchestrator | Null guard throws before any effects; empty input returns without effects; validation short-circuits; accepted input records once then returns; recorded-call failure propagates |
| Pure classifier | Negative amount returns Invalid; member and threshold predicate selects Gold; other inputs return None; exact boundary values and evaluation order are explicit |

Use simple scalar predicates and explicit calls first; avoid LINQ/lambda,
reflection or interprocedural expansion in the initial positive oracle.
Include changing inputs, short-circuit evaluation, throws and side-effect order.
Negative cases include async, dynamic dispatch, recursion and unsupported loops.
They must be partial/unsupported, not reconstructed as a complete process.

Do not assert exact BasicBlock counts without compiling the pinned Roslyn
version. CFG operations are not BPMN business roles, messages or an end-to-end
process. Equivalent DMN hit policy/FEEL requires additional semantic proof.
See [process and decision evidence](process-and-decisions.md).

# Reuse qualification

Public delivery should use synthetic tests and generic contract semantics.
Before any private donor transfer, freeze exact assets and establish applicable
redistribution/notice authority. No donor code or private provenance is published.
Compare whole-line versus column-span hashing, unknown versus empty coverage,
exact revision qualifications, cache/configuration invalidation and privacy scope.
Passing donor tests alone cannot prove target-contract parity.

The [source catalogue](sources.md) records all six requests. These gaps become
acceptance gates in the same [six packages](delivery-review.md), not new issues
or an automatic extra research round.

[^bpmn]: Retrieved `https://registry.npmjs.org/bpmn-moddle/latest`; name/version, description, license, gitHead and dist.shasum fields. Tarball was not downloaded.
[^dmn]: Retrieved `https://registry.npmjs.org/dmn-moddle/latest`; same fields. Declared DMN 1.3 scope is not a tested version ceiling.
[^cfg]: Microsoft Learn ControlFlowGraph description/members; requested 4.9.0 view, returned metadata also named a newer default moniker. No source/compiler behavior test.
[^block]: Microsoft Learn BasicBlock description: operations, BranchValue and conditional/fall-through successors.
[^fixtures]: Public stage-1 fixtures: `api-cs/01-base/src/Sales.Api/Calculations/WeeklySalesCalculator.cs:7-20`, `forecast-cs/01-base/src/Forecast.Api/Endpoints/ForecastEndpoints.cs:20-40` and their project manifests. Bounded inspection, not whole-repository absence proof.
