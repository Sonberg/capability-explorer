# Intelligence layer on Microsoft Foundry (proposal)

Status: proposal for stage 6 ("Domän och granskning"). Nothing in this repository calls a model yet.

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
