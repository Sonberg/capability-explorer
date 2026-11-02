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

Brain grounding: `sparc/plugins/sparc/skills/sparc-review/SKILL.md` distinguishes
evidence presence from verified outcome. This package uses existing tools,
not a newly installed SPARC runtime. Subscription cost/savings remain unknown;
no external paid inference is used.
