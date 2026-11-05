# Intelligence layer on Microsoft Foundry (proposal)

Status: the first task (difference explanations) is implemented in
`platform/src/CapabilityExplorer.Interpretation` and is **optional**: the default provider is `none`, and
the platform works fully without it. Concept proposals and links remain a proposal for stage 6.

## Why Foundry fits

The layer needs an approved model endpoint inside the organisation's Azure boundary, plus the
controls the handoff asks for:
- only permitted code is sent;
- prompts, model versions and input hashes are recorded;
- costs are logged.

Microsoft Foundry provides one control plane for several model families. It includes the Azure OpenAI
models, and Anthropic's Claude models are generally available there as an Azure-hosted service. It
also provides private networking, identity-based access and usage metering. The choice of model then
becomes a configuration value, not an architecture decision.

## What the model is allowed to do

The model only *proposes*. It never writes code facts.

| Task | Input (all from stored artifacts) | Output |
|---|---|---|
| Concept proposals | symbol names, doc comments, calculation attributes, evidence ids | `Concept` / `Definition` candidates citing evidence ids |
| Difference explanations | the deterministic attribute diff between two calculations | prose that explains *only* the listed differences |
| Concept links | implementations with similar `irHash` structure or attributes | "implements concept X" proposals with a confidence *ranking* (not a probability) |

Every proposal:
- is stored with `derivation = "llm"` in interpretation tables, separate from the immutable artifact
  tables. Rejecting one never touches the code graph.
- records the prompt template version, the Foundry deployment and model version, the input content
  hash and the token cost. It is cached by `(input hash, prompt version, model version)`.
- is validated before it is stored. Every cited evidence id must exist in the input. A claimed
  difference must appear in the deterministic diff. Anything else is rejected, not repaired.
- starts as `proposed`. Only a human review moves it to `reviewed`. A review applies to the exact
  input hash it saw.

Repository text (comments, READMEs, strings) is passed as quoted data in the prompt and is never
treated as instructions.

## Shape in code

```
platform/src/CapabilityExplorer.Interpretation/
  IInterpretationModel          // CompleteAsync(prompt, schema) → structured JSON; provider-agnostic
  FoundryInterpretationModel     // Foundry endpoint + deployment name from configuration, Entra ID auth
  RecordedInterpretationModel    // replays stored responses: tests and CI never call a live model
  ProposalValidator              // citation and diff checks above
```

Configuration holds the Foundry project endpoint and deployment name, and uses managed identity. No
API keys go in the repository. A per-repository allow-list decides which code may leave the analysis
sandbox.

## First step

Stage 6 starts with difference explanations. They are the narrowest task, because the deterministic
diff already holds the answer and the model only phrases it. That makes validation strict and
evaluation easy against the fixture variants (calendar vs active weeks, rounding modes, null
handling).

## Integration assessments

The file-backed architecture inspector now offers an optional integration
assessment through the existing configured `IInterpretationModel`. This is a
second-stage suggestion workflow, not a binding-adoption threshold and not an
unattended worker-side model batch. The deterministic post-publication matcher
remains available without a model.

`POST /api/architecture/integration-suggestions` requires a local same-origin
JSON request and `X-CE-Integration-Suggestions: 1`. It takes source and target
capability IDs, an optional source repository/query, and the selected revision
vector. No client-supplied source text or target node catalogue is trusted.
The server retrieves bounded code pages from offline Git objects at the
selected analyzed commits, favors concrete integration callers and receiver
contracts, and excludes test fixtures.

The structured result separates source dependency intent (`observed`,
`not-observed`, `uncertain`) from target compatibility (`compatible`,
`incompatible`, `unverified`). Every accepted model result cites supplied source
and target pages; any returned node key must exist in the supplied catalogue.
Invalid output abstains. The service will not present model-only compatibility
as independently established when target identity is unproven. Even admissible
results remain suggestions or incompatibility findings, never confirmed links.

The evidence, model identity, prompt version and input hash make each result
inspectable. Requests have a two-minute deadline; cancellation and stale UI
selection changes discard pending results. Source/page and response byte budgets
remain bounded. The inspector renders source as text, not executable markup.
Assessments are returned to the caller, not automatically inserted into the
proposed interpretation tables or durable graph facts. The live qualification
receipt is owner-only and separate from settings/credentials.

Citations establish input provenance, not that every model assertion is true.
The live evaluation found unsupported compatibility claims despite valid
citations, so deterministic validation and source review remain necessary. See
[automatic matching evidence](evidence/automatic-integration-matching.md).
