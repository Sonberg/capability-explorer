# Stakeholder task and oracle contracts: issue #100 preparation

## Status and authority

These are two **SYNTHETIC draft** task and oracle contracts for
[issue #100](https://github.com/Sonberg/capability-explorer/issues/100).
They prepare one process/decision question and one engineering contract
question before the shared bounded query contract (#99) and the thin new Vite
UI (#29) are designed.

| Item | Value |
|---|---|
| Base | main `43b90cc45d381eabf85ac67462a48a3346221613` |
| Claim | issue #3, session `next-round-94729006`, package C |
| Roles | Proposed. No stakeholder has acknowledged a role, task or wording. |
| Oracle author | AI task-design worker in the session above |
| Independent oracle reviewer | Unassigned |
| Oracle status | **unreviewed — qualification blocked** |
| Human data | None. No study, participant, session, survey or telemetry exists. |

This document does not implement a query, UI, recorder, telemetry, study
platform or fixture. It does not close #100 or any acceptance check. An AI
review of this draft is advisory. It is not oracle review, stakeholder
acceptance or participant evidence. Expected answers become usable for scored
trials only after a named independent reviewer confirms them and the owner
records that review.

## Sequence

1. #100 preparation: this document, then independent oracle review.
2. #99: one bounded query contract that can return the states these tasks
   need (exact snapshot, pages, partial, denied, stale, unavailable).
3. #29: a thin new UI that serves these two task paths only.
4. #100 execution: automated scenarios, expert review and, only if separately
   authorized, observed human sessions.

This is phase sequencing. #99 and #29 consume these contracts; #100 execution
consumes their delivered slice. Do not add issue-level blocking edges in both
directions.

## Rules shared by both tasks

### Answer outcomes

Score every attempt against the frozen oracle, never against a later preference.

| Outcome | Meaning |
|---|---|
| Correct | States the expected answer, its evidence status and the required citations. |
| Acceptable variant | A listed variant with the same meaning, status and scope. |
| Incorrect | Wrong or incomplete answer without a critical error. |
| Critical | Any critical trap below, or an unsupported claim of absence, completeness or authority. |
| Abstained | "Insufficient evidence". Success only where the oracle requires insufficiency. Otherwise non-success. Critical only when phrased as absence ("there are none"). |
| Abandoned / timed out / invalid | Recorded with the reason. Bounds are owner decisions, not set here. |

Freeze these rules before any trial. Do not change them to improve results.

### Evidence status

- **Authored model reference**: an explicit reference written in an authored
  BPMN/DMN model. It is evidence of what the model says. It is not business
  approval and not a reviewed assertion.
- **Static candidate**: a non-executable interpretation of code
  (`ce.candidates.v1`) or an unresolved/candidate edge. A candidate is not confirmed.
- **Reviewed assertion**: a recorded human review of a claim. No reviewed
  assertion exists in either frozen input set.
- For engineering evidence keep four separate kinds, as #100 and #95 require:
  **structural compatibility** (shape matches), **semantic correspondence**
  (both sides evidence the same meaning), **configured binding** (configuration
  connects the consumer to this provider) and **observed communication**
  (runtime observation). One kind never implies another.

### Models disabled

The primary condition runs without Laya or any LLM. Every expected answer below
is reachable from deterministic artifacts and source. A model-enabled run is a
separate condition. Model output never upgrades an evidence status.

## Frozen input identities

Both input sets are SYNTHETIC. Git blob IDs and SHA-256 values are for the
files at base `43b90cc`. Values were recomputed for this draft (see
[Verification](#verification-performed-for-this-draft)).

### Process/decision inputs

`scripts/prepare-model-fixtures.mjs` seals the four files in `fixtures/models`
into a deterministic synthetic Git repository, then extracts them.

| Identity | Value |
|---|---|
| Repository ID | `synthetic-model-fixtures` (provider `local:synthetic`, tenant `synthetic`) |
| Sealed commit / tree at base | `89163015be2951cc266af4f14e245fa1d6fc9789` / `79241b6c70d0ab0290581a4abce52abbdc17ce70` |
| Collection key (script `--collection-key`) | `85a0923e4aaaab2f7c34` |
| Recipe SHA-256 | `49b447c3c2c263fc452e433712b0d8e96e43a9c3d0a072770f1be496e94e3eed` |
| Source rights | `rightsRef: explicit-local-fixture-model-content-consent`, permitted profile `model-content`, policy SHA-256 `18973c5f7876f9c80adcb88cf94117d891e6aa1b3f40343895e341edec638551`. Local explicit permission, not authenticated central authorization. |
| Capability | None assigned in the fixture. See prerequisite X1. |

| Unit path | Git blob | SHA-256 | Bytes |
|---|---|---|---|
| `workflow.bpmn` | `59ca241a6152` | `0b2c2e178791ac4ab6ce24393560c47757207277b3d0396aa4fcfcd06dfadbdf` | 8899 |
| `decision-rich.dmn` | `d09a87321723` | `b909ec204d89a227b315c4507df8e32486eaedc4fe42979f7354d4fa05f1f61c` | 5162 |
| `order.bpmn` | `37cc36e16ef7` | `fdfb5656f55da6b107ca396fbde2188c96a4033c3bd95f496bf74c1775e0dbe1` | 2087 |
| `decision.dmn` | `d405f5204e18` | `3ae2c6b3e160ecab4f32111f85dc03d6a103e34ece68bf3405dcdbfec8285fef` | 1347 |

Stale comparison snapshot: the same recipe applied to the fixture bytes at
`af40250c57b163381d176467f53afa57057314a6` (an ancestor of base) gives sealed
commit `584873726ec38b7b6cb7451294873e15f24a13d8`, tree
`bf40601b7d3a4554e7dbc009ac82124548dbd1d1`. There, `workflow.bpmn` has blob
`bf5d41996d46` and contains neither step `evaluate-order` nor any
`ce:decisionRef`. Commit `46826f9` added both; `workflow.bpmn` and
`decision-rich.dmn` are unchanged from `46826f9` to base.

The extracted `ce.models.v1` products and `collection.json` digest were not
produced for this draft. See prerequisite P0.

### Engineering inputs

`fixtures/revisions.lock.json` (blob `e06537e20a5d`) pins the commits that
`scripts/build-fixtures.sh` must reproduce from `fixtures/src` overlays and
`fixtures/manifest.json` (blob `d72b06e54ea5`). The evidence substrate is the
golden reference artifacts in `fixtures/expected`. They are hand-written
expected facts and a **reference subset**, not complete extractor output.

| Repository @ tag | Commit | Unit artifact | Git blob | Unit status |
|---|---|---|---|---|
| `api-cs@v3-filter` (provider) | `29571e3528e537313f938a1fedae43aaeb4dbef9` | `api-cs/v3-filter/Sales.Api.jsonl` | `8dbb5ba35147` | full |
| `web-ts@v1-base` | `4f254bb0209550bfe8fd40459ae4136b1d37ebe4` | `web-ts/v1-base/_fixture_web.jsonl` | `aed646fda651` | full |
| `forecast-cs@v1-base` | `8541d29510852bd273f919d264d3dfa5fcae6605` | `forecast-cs/v1-base/Forecast.Api.jsonl` | `5ec630c1bf93` | full |
| `pricing-node@v1-base` | `e6583f75188438fead9b0c086ee018ddab65cc16` | `pricing-node/v1-base/_fixture_pricing.jsonl` | `0ae67e282006` | full |
| `worker-cs@v2-broken` | `b884b04321a12d82c23f8575c94c1980de349ce9` | `worker-cs/v2-broken/Sales.Worker.jsonl` | `39f2416f2cef` | **partial** |

Wrong-revision comparison: `worker-cs@v1-base`
`d9a6b7e1740388e3111cfa476ef3d78bff352b3e`, artifact blob `c0dd3730044c`,
unit status full.

Source rights: the fixtures are public synthetic code in this repository.
No collection plan with `native-content` permissions exists for this
five-repository set. See prerequisites E1 and E3.

## Task P1: process/decision reviewer

### Role and trigger

Proposed role: a process/decision reviewer for the synthetic order fulfilment
process. Trigger: a change to order evaluation is proposed. Before review, the
reviewer must know which decision the selected step uses at the frozen
revision and how strongly that link is evidenced.

### Question

> In repository `synthetic-model-fixtures` at sealed commit `89163015…`,
> which decision governs step **Evaluate order decision** (`evaluate-order`)
> in process **Order fulfilment** (`order-process`, `workflow.bpmn`)?
> Is that link an authored model reference, a static candidate or a reviewed
> assertion? Cite the source.

### Permitted scope

The four sealed unit paths only, under the `model-content` profile. Other
repositories, other revisions and external DMN locations are out of scope.
The answer must not claim anything about them.

### Expected answer (frozen draft, unreviewed)

1. The governing decision is **`order-decision` ("Order decision")** in
   `decision-rich.dmn` (definitions `decision-definitions`, namespace
   `urn:example:decision`), same repository and sealed commit.
2. Evidence status: **authored model reference**. The step carries
   `ce:decisionRef="decision-rich.dmn#order-decision"` (namespace
   `urn:capability-explorer:model-links:v1`). The project resolver accepts only
   an explicit relative DMN path and decision ID in the same repository and
   commit
   ([`decision-links.ts`](../../platform/src/CapabilityExplorer.Explorer/client/src/models/decision-links.ts),
   blob `1d2571a0272a`).
3. It is **not a reviewed assertion**; no review record exists. It is **not a
   static candidate**. No static candidate is linked to this step in the
   frozen inputs.
4. The DMN `authorityRequirement` to knowledge source "Approval policy" is
   authored model content. It does not establish business approval.

Acceptable variants: decision ID or name alone; mention of the decision's
requirements (input `age-data`, required decision `base-decision`, knowledge
model `threshold-policy`) as dependencies. Abstention is non-success because
the oracle has a definite answer.

### Required citations

| Citation | Location at sealed commit | Status |
|---|---|---|
| Selected step and reference | `workflow.bpmn`, element `evaluate-order`, line 18 | authored model reference |
| Governing decision | `decision-rich.dmn`, element `order-decision`, line 32 | authored model |
| Revision identity | repository ID and sealed commit `89163015…` | snapshot identity |

### Coverage

The frozen snapshot contains exactly four authored model files and no code
candidates. Coverage of other repositories is unknown, not empty.

### Critical traps and fixture availability

| ID | Trap | Critical error if the participant… | Fixture |
|---|---|---|---|
| P1-T1 | Stale revision | answers from sealed commit `58487372…` (no `evaluate-order`, no reference), e.g. "no decision" or "step does not exist" | **Available**: bytes at `af40250`. Presentation of a stale snapshot needs #99/#29 (P2). |
| P1-T2 | Same identifier, different scope | uses `order.bpmn`, which also declares process `order-process` (name "Order review") and a task "Review order"; workflow.bpmn has `review-task` "Review order" | **Available** in the sealed snapshot |
| P1-T3 | Same-named decision in another scope | cites an "Order decision" from another repository or capability | **Not available**. Planned prerequisite P1. |
| P1-T4 | Candidate as governing or reviewed | presents a static code candidate, such as `Reviewed.Classify` in `fixtures/candidates/reviewed.cs` (blob `4aff14dadea2`), as the governing decision or as reviewed | **Not available** in this snapshot. Source exists; no frozen collection combines it with the models. Planned prerequisite P3. |
| P1-T5 | Authority overclaim | states the decision is business-approved, for example because of "Approval policy" | **Available** |
| P1-T6 | Denied or missing target read as no decision | reads a denied or missing `decision-rich.dmn` as "no governing decision" | **Not frozen**. Current-client automated precedent only (P4). |

Non-critical incorrect answers include naming `base-decision` (a required
decision) or the called process `child-process` as governing.

### Completion, failure, abandonment and recovery

- Complete: decision, evidence status and the three citations are stated.
- Failure: incorrect or critical outcome, recorded against the trap ID.
- Abandonment: participant stops or an owner-set bound expires.
- Recovery paths the UI must offer: change to the frozen revision; navigate
  from the step to the decision source; for a denied or missing target, state
  that the reference exists, the target cannot be inspected and no alternate
  source was used.

## Task E1: engineering reviewer

### Role and trigger

Proposed role: an engineering reviewer who owns the synthetic Sales API.
Trigger: a change to the response of `GET /api/articles/{id}/weekly-sales` is
proposed. Before the change, the reviewer must know which components are
evidenced consumers and how strong that evidence is.

### Question

> In the frozen five-repository snapshot, which components are evidenced
> consumers of the `api-cs` `Sales.Api` endpoint
> `GET /api/articles/{}/weekly-sales`? For each, which kinds of evidence exist:
> structural compatibility, semantic correspondence, configured binding or
> observed communication? Is the list complete?

### Permitted scope

The five repositories and commits listed in
[Engineering inputs](#engineering-inputs). Other repositories and tags are
out of scope.

### Expected answer (frozen draft, unreviewed)

Provider: `ArticlesController.GetWeeklySales` handles
`http|api-cs/Sales.Api|GET /api/articles/{}/weekly-sales` (edge
`e:f95babcd5563781d`, evidence `ev:da9329dca566c0d0` at
`src/Sales.Api/Controllers/ArticlesController.cs` line 13, unit status full).

**At least three structural candidate consumers.** Each matches method and
normalized route only. Each edge has `resolution: unresolved`,
`unresolvedReason: configuration`.

| Consumer | Edge | Evidence (path:line) | Configuration cited |
|---|---|---|---|
| `web-ts` `fetchWeeklySales` | `e:0c7e09536422f6c5` | `src/api/salesClient.ts:11–12` | `salesApiBaseUrl` = `globalThis.SALES_API_URL ?? "/sales-api"` (`src/config.ts:6`) |
| `forecast-cs` `SalesApiClient.GetWeeklyAverageAsync` | `e:2a67e51c60549753` | `src/Forecast.Api/Clients/SalesApiClient.cs:12–13`, `src/Forecast.Api/Program.cs:15` | `SalesApi:BaseUrl` = `http://sales-api/` (`src/Forecast.Api/appsettings.json:6`) |
| `pricing-node` `SalesApi.getWeeklySales` (generated) | `e:13f76b548e800f60` | `src/generated/salesApi.ts:25`, call site `src/handlers/recalculate.ts:5` | env `SALES_API_URL`, default `http://sales-api` |

Evidence kinds:

- **Structural compatibility**: method and route template match for all three.
  Payload shape compatibility is not part of this oracle.
- **Semantic correspondence**: not established. The pricing-node
  `operationId` `Articles_GetWeeklySales` is a name in generated code. No
  provider-side API description exists in `api-cs@v3-filter`, so no evidence
  exists on both sides.
- **Configured binding**: not established. Configuration keys and default
  values are cited, but nothing binds `sales-api` or `/sales-api` to the
  `api-cs` deployment.
- **Observed communication**: no observation exists in the frozen inputs.
  Neither "communicates" nor "does not communicate" is supported.

**Completeness: not established.**

- `worker-cs@v2-broken` is partial: `src/Sales.Worker/Calculations/CurrencyNormalizer.cs`
  was not analyzed (diagnostics `COMPILER_CS0234`, `MISSING_PROJECT_REFERENCE`). Absence of a
  consumer there is unknown.
- `pricing-node` `SalesApi.request` (edge `e:8079a13aa62054f6`) has
  `unresolvedReason: dynamic-string`. Its target is unknown. Do not count it
  as a separate consumer and do not dismiss it.
- The goldens are a reference subset (prerequisite E1).
- Repositories outside the five are unknown.

Acceptable variants: consumers named by repository or symbol; pricing-node
described as a generated client; any wording that keeps "at least",
the four evidence kinds and incompleteness. Abstention from the whole answer
is non-success, because the oracle lists evidenced consumers.

### Required citations

Each listed consumer needs its repository, frozen commit, path and line, and
its edge resolution. The provider needs its edge and line. Completeness needs
the `worker-cs@v2-broken` partial status and the dynamic-string edge.

### Critical traps and fixture availability

| ID | Trap | Critical error if the participant… | Fixture |
|---|---|---|---|
| E1-T1 | Wrong revision | uses `worker-cs@v1-base` (full, no consumer) to conclude worker-cs is checked and does not consume; or cites any commit other than the frozen ones | **Available** |
| E1-T2 | Partial as complete or as absence | says "exactly three" or "worker-cs has none" | **Available** |
| E1-T3 | Unresolved as absent | drops the dynamic-string request without stating it is unknown | **Available** |
| E1-T4 | Name match as semantic correspondence | treats the `operationId` or route name as semantic or code-verified correspondence | **Available** |
| E1-T5 | Configured value as binding | treats `http://sales-api/` as proof of connection to `api-cs` | **Available** |
| E1-T6 | Observation overclaim | claims observed traffic or absence of traffic | **Available** (no observation evidence) |
| E1-T7 | First page as all | concludes from one page of consumers | **Not available**. Planned prerequisite E2. |
| E1-T8 | Denied repository as zero | reads a denied repository as having no consumers | **Not available**. Planned prerequisite E3. |

Non-critical incorrect answers include listing the provider, or
`pricing-node` `getForecast` (`GET /api/forecasts/{}`, a different contract),
as a consumer.

### Completion, failure, abandonment and recovery

- Complete: consumers with evidence kinds, citations and the completeness
  statement.
- Failure, abandonment: as in task P1.
- Recovery paths: next page with shown-versus-total or "total unknown"; open
  partial-coverage diagnostics; for denied access, state the denial and that
  no alternate source was used.

### Variant answers for planned states (not verified)

- E2 page size below three: page 1 shows two consumers and must state
  "more results" or the total. The expected answer is unchanged.
- E3 one consumer repository denied (for example `pricing-node`): "at least
  two structural candidates (`web-ts`, `forecast-cs`); `pricing-node` access
  denied, consumers there unknown; list not complete."

## Prerequisites and open decisions

None of these is done. Fixture changes need their own claim, because
`fixtures/` is shared with existing tests and goldens, and they change the
identities above. Re-freeze the oracle after any change.

| ID | Prerequisite | Owner / issue |
|---|---|---|
| P0 | Run the model fixture preparation; record `ce.models.v1` and `collection.json` digests | #100 executor |
| P1 | SYNTHETIC second scope with a decision named "Order decision" | separate fixture claim |
| P2 | Load and label a stale snapshot next to the current one | #99, #29 |
| P3 | Frozen SYNTHETIC collection with authored models plus a `ce.candidates.v1` artifact in the same scope | separate fixture claim |
| P4 | Frozen denied/missing target-model variant for the new UI | #99 fixture |
| E1 | Run the actual extractors at the five commits; record artifact hashes; reconcile extra edges to this route with the oracle | #100 executor |
| E2 | Stable explicit paging with a page size below three | #99 |
| E3 | Access variant that denies one consumer repository | #99 fixture |
| E4 | One composite snapshot identity for the five repositories | #99 |
| X1 | Decide whether tasks need a capability identity; fixtures have none | owner |
| O1 | Named independent oracle reviewer, review date and disagreements | owner |
| O2 | Stakeholder acknowledgement of roles and question wording | owner |

Excluded: #106 owns the collection dogfood runner and current-client work.
These contracts do not use them.

## Evidence classes for executed qualification

Report each class separately. Never combine them into one score.

| Class | What it can show | What it cannot show |
|---|---|---|
| A Automated contract/scenario | Specified states and answers are reachable | Human usability, human time |
| P Engineering performance (#13/#63) | Engineering latency gates | Time-to-answer |
| R Expert/manual review, incl. accessibility | Criterion findings for a named build | Conformance; AI critique is advisory only |
| O Oracle review | Expected-answer provenance | Usability acceptance |
| H Human observation | Only after authorized real sessions | Population performance |
| S Human self-report | Only real respondents | Imputed or AI-generated answers |

For any authorized human round, record per task, participant and attempt:
assigned, started, correct unassisted, correct assisted, incorrect, critical
(with trap ID), abstained, abandoned, timed out and missing/invalid, each with
a reason and an explicit denominator. Keep every assigned attempt. Keep
durations separately for correct unassisted, assisted, failed and censored
attempts, with the timing method. Flag repeat participants, oracle authors,
prior exposure, build, snapshot, model condition and protocol deviations.
Search versus navigation is descriptive. Report raw counts. This document
sets no completion, error, time or rating cutoffs. Public norms are context,
not targets.

Blocking (project policy): an unreviewed oracle, an unresolved critical trap
in A, R or H evidence, or an unresolved critical keyboard or screen-reader
blocker on a task path.

## Accessibility checks for the future task paths

Run these on the delivered #29 slice and record the tested browser and
assistive-technology pair per finding. They are tested-specific checks
against applicable WCAG 2.2 criteria, not a conformance claim.

- Keyboard-only completion of both tasks; visible, unobscured focus.
- Screen reader: scope, revision, evidence status, page position and
  partial/denied/stale states are announced.
- Zoom and reflow at 320 CSS px. Diagrams and graphs may use the
  two-dimensional exception only with an accessible list or table that
  answers the task. Navigation, scope controls and status text must reflow.

## Preconditions before any human collection

Agree and record these externally before collecting anything: consent text
and purpose; access owner; retention and deletion; storage outside Git;
collection method (simple observer worksheet preferred); finite participant,
attempt, time and file bounds; who may see results; and the reviewer
assignments. Use fixed task and attempt IDs and coded outcomes only. No
names, free text, source excerpts, screenshots, recordings or network export
by default. Coded records can still be personal data. No passive telemetry or
in-app recorder is authorized.

## Verification performed for this draft

All checks ran locally against base `43b90cc` without changing tracked
fixtures. They are automated source-identity evidence only.

| Check | Result |
|---|---|
| `scripts/build-fixtures.sh` in a private copy | exit 0, "fixture revisions match revisions.lock.json" |
| `node fixtures/golden/build.mjs --check` in that copy | exit 0, "golden files up to date (18 artifacts)" |
| `node scripts/prepare-model-fixtures.mjs --collection-key` | `85a0923e4aaaab2f7c34`, equal to an independent recomputation |
| Sealing recipe reproduced for base and `af40250` | commits and trees as listed above |
| `git merge-base --is-ancestor` for `af40250` and `46826f9` | both ancestors of base |

Not run: model or native extraction, collection sealing, the Explorer, any
browser or assistive-technology check, and any human session.
