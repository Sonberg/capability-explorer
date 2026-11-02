# Issue #33: draft exchange-contract foundation

Source baseline: main `1e1122b62587eda7f3e2817c1619ba29ce292923`.
Integrator: root; branch `feat/platform-exchange-contracts`. Schema/test worker
uses isolated `work/platform-schema`; only root integrates shared Make/docs files.

## Specification

First slice is a transport-neutral, explicitly draft contract profile and owner
MADR/language decision. It creates no central server, authentication authority,
storage product or deployment. Endpoint/transport choices remain later decisions.
Preserve ce.v1; platform metadata envelopes use their own schema identity.

| ID | Acceptance | Instrument |
|---|---|---|
| C1 | Separate enrollment, evidence-bundle and intake-receipt profiles have strict versioned shapes | Existing JsonSchema.Net against positive/negative fixtures |
| C2 | Repository/revision/artifact/config identities remain explicit; missing/partial observation is not deletion | Boundary and malformed identity/coverage cases |
| C3 | Evidence payload is unchanged ce.v1; no envelope/scoring/model field silently changes extractor facts | Namespace/schema and controlled payload-reference cases |
| C4 | Receiving/validation/publication states are distinct; rejected/conflicting/partial inputs cannot masquerade as published | Receipt/state fixtures and negative cases |
| C5 | Catalogue covers all seven boundaries and distinguishes cluster, current Capability and business capability | Executable plan/catalogue checks and proposed MADR |
| C6 | No new dependencies/production behavior; normal exact-main source CI passes | Existing contract suite and full local act/security gate |

No schema proves authorization, verifies an artifact's bytes from its hash label,
or establishes an implemented intake service. Document those required later
semantic validators. No new OpenAPI parser/validator is introduced to pretend
endpoint conformance exists; an HTTP profile follows its transport decision.

Final specification grade: 97/100. Requirements30/30, baseline25/25, negative
cases25/25, boundaries17/20: owner deployment/transport choices remain intentionally
open and outside this first slice. Instrument: six criterion-to-test routes and
existing source/contract inspection. Not tested: implementation or a central API.

## Phase ledger

### Pseudocode

Dispatch on a closed `schema`/`kind` discriminator; validate the appropriate
enrollment, evidence-bundle or intake-receipt definition. Reject unknown fields,
unsafe paths, malformed identity/digest/coverage values and impossible receipt
shapes. Require publication metadata only for published receipts and errors for
rejection/quarantine. Content identity excludes publication attempt metadata.
Collections/string/numeric bounds constrain validation cost O(document bytes).
The schema does not read a referenced artifact or implement ordering/idempotence;
later semantic intake verifies those bytes and transitions before publication.

Instrument: criterion-to-case mapping C1–C6 and explicit unsupported semantics.
Final pseudocode grade 97/100: trace35, failure paths30, boundedness20,
identity12/15 (provider runtime/transport semantics intentionally not implemented).
Not tested: executable schemas, provider, consumers or deployment.

### Architecture

One self-contained JSON Schema Draft 2020-12 at
`schema/platform/v1/envelope.schema.json`, identity `ce.platform.v1`, with three
closed discriminator profiles. Reuse existing JsonSchema.Net 9.6.0 through the
current Contracts.Tests project. New controlled fixtures live under
`tests/fixtures/platform/`; one new C# test file validates actual schema behavior.
No package/project/lockfile changes; ce.v1 schema and golden sources are untouched.

Enrollment maps current repositoryId to provider/authority/providerRepositoryId
and one explicit scope membership with role IDs and versioned registry input.
Bundle content contains exact source/unit/extractor/config/artifact identities
and coverage; its contentSha256 excludes attempt ID/timestamps. Receipt states
distinguish durable receipt/validation/rejection from publication metadata.
Date-time format assertions must be enabled and tested, not merely annotated.

MADR and catalogue remain proposed for owner review. Defer OpenAPI endpoints
until artifact-transfer/authentication semantics are selected; file-schema
validation is not API conformance. Root alone integrates Make/docs changes.
Final architecture grade97/100: contracts30, trust30, compatibility25, cost12/15;
the remaining3 points are external provider/transport qualification beyond scope.
Not tested: implementation or a production trust boundary.

Brain grounding: `sparc/plugins/sparc/skills/sparc-review/SKILL.md` distinguishes
evidence presence from verified outcome. This package uses existing tools,
not a newly installed SPARC runtime. Subscription cost/savings remain unknown;
no external paid inference is used.

### Refinement

Scoped gate: 97/100, three review iterations. Independent review found terminal
LF acceptance in .NET regex anchors and a focused Make recipe that could exit
successfully without running tests. Portable strict-end assertions, 14 regression
cases and platform-directory recipes fix both issues.

| Criterion | Executed evidence |
|---|---|
| C1 | Controlled examples, closed discriminators/required fields and negative profile values |
| C2 | Literal paths, bounds, revision/hash/version controls and partial coverage cases |
| C3 | Wire-family refusal and explicit shape-success-without-digest/authority proof |
| C4 | Published/nonpublished/error-state conditional cases |
| C5 | Seven-boundary catalogue and 24 positive/negative Node plan cases |
| C6 | Normal complete local act CI on candidate `74ce903449e9cef367293c1f4f3eae4fed9794af`, current main `1e1122b`; no new package/lock changes |

Actual focused .NET execution: 94 passed, zero failed/skipped. Independent reviewer
reproduced 94 .NET and 24 Node passes. Full Contracts.Tests: 202 passed. Store
tests: 131 passed. Existing extraction/golden, UI, LikeC4 and other suites passed
in the complete act job; receipt is private `.sandbox-cache/local-ci/last-success.txt`.
The direct macOS Make run stopped at the pre-existing `/var/run/docker.sock`
assumption; the real Linux act job executed that missing store group successfully.

Deduction: three points for an intentionally small synthetic shape matrix without
external consumer qualification; no numerical code-coverage claim. Not tested:
central runtime, authentication, actual artifact hashes/JCS, publication ordering,
multi-tenant reads or deployed API compatibility. These remain future semantics,
not capabilities established by this gate.

Brain review pass retained exact evidence/scope separation from
`sparc/plugins/sparc/skills/sparc-review/SKILL.md`; no additional runtime adopted.

### Completion

Scoped gate: 96/100, one iteration. Documentation, proposed MADR-0020, project
language, schema guide and catalogue match the executable draft. Deployment
checklist: no service, credentials, external enrollment or deployment is created;
owner approval is required for the proposed architecture. Separate issue #34
remains independent. Final documentation commits undergo the ordinary complete
pre-push act/security gate again; PR preparation verifies exact HEAD/current main.

Deduction: four points for pending owner/consumer review before external use.
What was not tested remains the explicit Refinement list. No production readiness
or completion of the whole issue #33 is claimed. No external paid inference calls
were initiated; OpenRouter is unconfigured and subscription cost/savings unknown.
The published PR and final private gate receipt are the handoff identities.
