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

## Snapshot embedding projection status

PostgreSQL embedding projections are optional derived data, not a condition of
core snapshot publication. The confirmed publication ID pins enrichment; a
later publication must not redirect an in-flight operation to another snapshot.
The standalone `ce embed --snapshot <uuid>` path retries that same retained
snapshot without changing its published generation.

Status, automatic enrichment and standalone retry share lazy embedding
configuration. Use the same `CE_MODEL_CONFIG` JSON file in all processes; its
`interpretation` object contains model options, and its optional `modelSettings`
path is relative to that JSON file. The same sources also support
`appsettings[.<environment>].json`, `interpretation__*` environment values,
`CE_MODEL_SETTINGS`, and explicit `--interpretation:<option>` /
`--modelSettings` arguments. Repeat process-local arguments for independently
started processes. `CE_MODEL_ENDPOINT` and `CE_EMBEDDING_MODEL` retain final
precedence; `CE_EMBEDDING_REVISION` distinguishes revisions behind an alias.

An authorized operator with `CE_DATABASE` configured can retry without
republishing:

```sh
CE_MODEL_CONFIG=/absolute/path/embedding-host.json ce embed --snapshot <uuid>
```

Alternatively, supply `--connection` using the existing database credential
mechanism. Explorer's **Refresh status / Retry status** buttons only read
metadata; they never execute this command or invoke the provider.

Projection identity includes the exact snapshot and a model fingerprint
covering provider, canonical endpoint, deployment, explicit revision when
available, and input recipe. Credentials are excluded from the fingerprint and
status response. Changing provider weights behind an unchanged deployment alias
cannot be detected: change its model/revision identity to distinguish the result.

Vectors belong to snapshot occurrences `(artifact_id, node_local_id)`, not a
mutable latest-per-symbol cache. A whole-snapshot writer records its membership
digest and per-artifact enumeration seals/counters. Replaying a successful
projection does not call the provider again or overwrite ready vectors; retrying
failure cannot increment counters twice. Even artifacts with zero eligible
occurrences must be explicitly enumerated and sealed.

`GET /api/embeddings/status?cap=<scope>&snapshot=<uuid>&view=<repo:sha,...>`
reports the pinned snapshot, effective selection,
configured/covered model identities, coverage, completion time and counts.
Reads inspect bounded projection/artifact metadata, never enumerate nodes or
scan vectors. Selected-view readiness considers only the selected artifacts;
an unrelated artifact failure does not invalidate their completed projection.
`coveredArtifactCount` counts fully ready artifacts, not enumeration seals;
a sealed artifact can still contain failed occurrences.

| State | Meaning |
|---|---|
| `not-configured` | No embedding model is configured; core browsing remains available. |
| `pending` | Matching work is absent or incomplete, or historical coverage is unknown. |
| `partial-failure` | Relevant matching work has a recorded failure; standalone retry is available. |
| `stale` | A completed historical projection covers the view, but not the configured model identity. |
| `ready` | Every selected artifact is sealed and all its eligible occurrences are ready. |

Counts are explicitly `null` until selected enumeration is complete, not
silently zero. An absent projection cannot make an empty selection vacuously
ready. Historical lookup considers only the indexed latest fully completed
projection for the same snapshot; partial older model histories are not scanned
to infer coverage. Unknown history remains `pending`. Invalid configuration and
request failures remain explicit errors, distinct from missing configuration.

**Projection ready is not similarity-search ready.** PostgreSQL similarity
search remains unsupported; the status exposes `similarityAvailable: false`
and Explorer says so. This endpoint neither starts enrichment nor changes core
generation. The legacy file-mode/global cache behavior is unchanged and is not
used to certify historical snapshot coverage.

Retention removes owned projection metadata/vectors with snapshot membership
or snapshot pruning. Retained snapshots keep their own immutable vectors.
Functional status tests are not a latency or reference-envelope SLO claim.
