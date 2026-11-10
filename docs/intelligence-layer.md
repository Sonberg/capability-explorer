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

## Chat over analyzed code (current implementation)

The file-backed Explorer offers optional `POST /api/chat` through the configured
chat model and Microsoft Agent Framework. The model **proposes an answer**; it
does not write code facts, review findings or approve connections. The panel
labels answers with sources as **AI-written, unreviewed**. Chat responses are
returned to the caller, not stored as reviewed interpretation proposals. The
proposal storage and validation requirements above are not guarantees supplied
by the chat endpoint.

The request contains `messages`, optional `focus`, and optional `view` revision
selection and `cap` scope. The server accepts up to ten user/assistant turns,
ending with a user question of 1–1,000 characters after trimming. A missing chat
model returns 409; an invalid conversation returns 400; a model failure reported
as unavailable returns 503. Focus describes what is on screen; it does not widen
the selected code view.

### Retrieval and citations

Each answer has `answer`, `sources`, `retrieval`, `model` and `steps`. Up-front
retrieval and subsequent tool lookups share numbered items. The model is
instructed to cite them as `[1]`, `[2]`, and so on. Each source records its number,
kind, key, name, repository, first retrieval origin (`found`) and a text preview
capped at 600 characters plus an ellipsis. Reusing an item preserves its number
and first origin. `steps` lists tool names and arguments in invocation order;
`retrieval` reports retriever activity or semantic-search unavailability, not a
proof that each retriever contributed evidence.

Retrieval uses the selected analyzed revisions and capability scope:

- Up front: the focused symbol, optional embedding neighbours, keyword matches,
  HTTP integrations and shared resources, vendor flows, graph paths and glossary
  concepts. There are at most 32 up-front items and 40 items for the whole answer.
- Tools: `search_symbols`, `get_symbol`, `get_connected_code`, `find_path` and
  `list_integrations`. `semantic_search` is offered only when the legacy
  similarity store and embedding model are configured. Without them, names and
  graph retrieval remain available and the response reports why semantic search
  is off. Snapshot projection readiness does not enable PostgreSQL similarity
  search; see the projection status section below.
- Paths: one shortest chain, walking relations in either direction, at most six
  hops and a 20,000-visited-node search budget. Declaration edges are skipped;
  undeclared external and heavily shared symbols are not traversed as bridges.
  Shared channel and SQL resource keys can be bridges. Ties use ordinal symbol
  keys, then relation labels, with outgoing relations before incoming relations
  before HTTP candidate links. This is deterministic ordering, not relevance
  ranking; alternatives are not exhaustively returned. HTTP route-only links
  are explicitly candidates, not confirmed service identity.
- Flows: bounded entry-to-vendor chains assembled from HTTP endpoint matches and
  graph relations. A route-only entry keeps its candidate label. No flow is
  added when a matching vendor or connected entry chain is absent.
- Connected code: incoming and outgoing declared neighbours, excluding
  declaration edges. The tool clamps depth to one or two steps, returns at most
  eight neighbours, deduplicates cycles and respects the shared item limit.

Tools return explicit missing-symbol or no-chain messages. Missing paths mean
**not observed within these bounds**, not that interaction is impossible.
Context and tool results are bounded rather than exhaustive: tool text is
capped at 6,000 characters plus an ellipsis. Available source snippets are read
at analyzed commits, with a shared budget of 24 snippets, each at most 25 lines
and 900 characters plus an ellipsis. Source previews can be shorter than the
context used by the model.

### Trust and current limits

Repository names, comments, strings and source snippets are **untrusted data**,
not instructions. The system prompt tells the model not to follow instructions
inside retrieved items, to cite evidence, to preserve route-match uncertainty
and to say when an answer was not observed. These instructions are not an
injection-proof boundary or a deterministic check of answer correctness.
The endpoint does not validate every generated citation or claim against the
retrieved items. Review the cited sources before relying on an answer.

The agent allows up to five tool-invocation iterations per request. If a model
call fails as unavailable while tools are offered, chat retries without tools
using the same retrieved context; this fallback is not proof of model tool
support. Responses are non-streaming; the model adapter's streaming method is
not implemented. Conversation history is supplied by the client and is not
durably stored by this endpoint. No supported-model quality, latency, throughput
or real-corpus accuracy is established by synthetic retrieval tests.

`ChatToolRetrievalTests` exercises the existing tool functions and context
builders with sealed synthetic artifacts, without a model or private inputs.
It covers path citations and bounds, candidate-labelled flows, missing and
disconnected cases, connected-code depth/cycles/item limits, and selected-view
isolation. `ChatApiTests` separately covers the scripted model/tool round trip.
Neither establishes live-model correctness or browser acceptance for issue #25.

### Optional current-client tab history

Chat is **memory-only by default**. In the Ask panel, explicitly enable
**Remember chat in this tab** to retain one conversation in `sessionStorage`.
The visible storage summary shows the limits; expand it for the full privacy
terms. This is not encrypted storage. Browser session restore or duplicated
tabs may keep a copy, so closing a tab does not guarantee deletion.

The versioned record is bound to the analyzed snapshot, capability, effective
repository revisions and navigation view. A record from a different scope is
not restored and is removed with a notice when that scope opens successfully.
Opening or restoring chat never sends a model request. Questions pin the same
effective revisions rather than replaying evidence under a mutable default.

Only the most recent **20 whole user/assistant turns**, including sources,
lookups and retrieval metadata, are saved, up to **512 KiB of serialized UTF-8**.
Older turns remain in memory until closing or reloading. No turn or source text
is truncated to fit: an oversized or failed save disables remembering and
attempts to remove the previous record while leaving the conversation usable.
Malformed, unsupported and invalid stored data is rejected with a notice.
Storage and removal failures are visible; failed deletion is not reported as
successful and the old record might return after reload.

**Clear chat** aborts the pending request, clears the transcript and draft,
turns remembering off and removes the retained record when storage permits.
Turning remembering off alone keeps the current in-memory conversation.
Scope changes and closing the panel abort pending requests; late replies
cannot repopulate cleared or different-scope history. The API still receives
only the last ten messages, and the endpoint does not durably store them.

`chat-history.test.ts` exercises validation, bounds and expected storage failures.
`chat-panel.browser.ts` uses the actual built current client with synthetic
transport to check consent, reload, clear, scoped requests, cancellation,
citations and compact/desktop layouts. This is not live-model, real-corpus,
browser-session deletion or native-tooltip-pixel qualification.

### Ollama context and native macOS setup

Configure context on the **Ollama server or model**, not in Explorer's endpoint
field. Retrieved graph facts, source snippets, tool results and conversation
history all consume context. A larger window also consumes more memory; 16,384
tokens below is an example, not a guarantee that every request fits or that a
particular model supports the tools, hardware or desired response time.

For the native macOS app, set its environment and then quit and reopen Ollama:

```sh
launchctl setenv OLLAMA_CONTEXT_LENGTH 16384
```

This configures the app's launch environment, not an already running process,
and is not a reboot-persistent setting. For a terminal-managed server instead,
set `OLLAMA_CONTEXT_LENGTH=16384 ollama serve` before it starts; do not launch a
second server on the app's existing port. Native macOS Ollama supports Metal GPU
acceleration. A Linux Ollama container on macOS does not provide that native
Metal path.

For a persistent, model-specific configuration, save a local Modelfile, for
example `.sandbox-cache/Chat.Modelfile`, containing:

```text
FROM qwen2.5:7b
PARAMETER num_ctx 16384
```

Run `ollama create ce-chat-16k -f .sandbox-cache/Chat.Modelfile` against the server
you intend to use, then select `ce-chat-16k` as the chat model in Explorer's
Settings. The alias stores the parameter in that server's model data, so keep
that data across restarts. This does not establish model quality or supported
hardware, and creating/pulling a model requires its download and disk budget.

`make ollama` first reuses a responding host server, otherwise starts the
existing `ce-ollama` container or creates one. It does not configure context or
replace a container's environment. Exporting `OLLAMA_CONTEXT_LENGTH` only for
`make ollama` therefore does not configure the existing server or its container.
The target also pulls its selected models: use Settings for a locally created
alias rather than passing that unpublished alias as `OLLAMA_CHAT`. Existing
`.sandbox-cache/model.json` settings are preserved; selecting a different alias
requires an explicit Settings change.

Use the native server's reachable `/v1` URL for the selected execution profile:
normally `http://127.0.0.1:11434/v1` for native Explorer and
`http://host.docker.internal:11434/v1` for sandbox Explorer. Keep access local;
do not expose an unauthenticated model server just to enlarge its context.
These are configuration instructions, not a live-model or post-reboot
qualification receipt.

Upstream references: [server context and app environment](https://github.com/ollama/ollama/blob/main/docs/faq.mdx),
[model parameters](https://github.com/ollama/ollama/blob/main/docs/modelfile.mdx)
and [Metal support](https://github.com/ollama/ollama/blob/main/docs/gpu.mdx).

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
