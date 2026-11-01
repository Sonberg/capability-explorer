# GOAL

Build a deterministic, offline declared integration-contract catalog for
capability-explorer. Discover OpenAPI HTTP interfaces and AsyncAPI messaging
interfaces in selected repositories; preserve original evidence and explicit
coverage limits; expose them through read-only catalog APIs and the shared LikeC4
canvas, including all-repository viewpoints. Declared contracts are distinct from
observed code, reviewed bindings and runtime traffic.

# ASSUMPTIONS

This is the next proposed substantial package, not implemented functionality.
Start implementation only after the owner merges the current security/corpus/
binding package into `stage-1-contract-and-fixtures`. Record its actual PR and
merge SHA when starting. Planning may proceed meanwhile; do not silently stack
unmerged code or import main. Never merge or enable auto-merge yourself.

Contracts are the best next independent product increment: they extend the
cross-repository picture and MADR-0013 without overlapping the owner's chat and
constructor work in issue #25. BPMN/DMN implementation still requires the owner's
scope decision in #12. Image/security remediation remains visible in #24 and
unavailable private repositories remain not assessed in #23; neither is silently
declared complete by this package.

Use a versioned declared-interface catalog alongside ce.v1. Existing ce.v1 keys
and relation semantics cannot faithfully represent all these declarations; do not
invent keys, disguise contract operations as code functions, or silently extend
the schema. The catalog shares snapshot/source identity and the existing import,
query and evidence boundaries; it is not a second authoritative graph store.

Implement a documented bounded profile: JSON/YAML discovery, declared HTTP
operations/servers, messaging channels/messages and application-relative
send/receive, local references and source navigation. Explicitly diagnose excluded
features such as callbacks, webhooks, replies, traits or bindings unless their
semantics are actually implemented and tested. Payload schemas remain opaque,
with their dialect/schemaFormat retained. Defer compatibility analysis, automatic
code-to-contract bindings, protocol-binding interpretation and runtime inference.

# STACK

Use the existing .NET/TypeScript architecture, sandbox, PostgreSQL/read models,
source viewer, LikeC4 renderer, test harnesses and local act CI. System.Text.Json
is the JSON foundation. Evaluate the maintained stable
[YamlDotNet](https://github.com/aaubry/YamlDotNet) parser for YAML source locations,
duplicate-key handling, limits, license and current advisories before pinning an
exact dependency; no unsafe object deserialization or repository code execution.

Primary specifications: [OpenAPI 3.2.1](https://spec.openapis.org/oas/v3.2.1.html),
also recognize 3.2.0; [AsyncAPI 3.1.0](https://www.asyncapi.com/docs/reference/specification/v3.1.0).
Follow MADR-0013 and retain dialect distinctions. Supporting a bounded extraction
profile is not a claim of complete specification validation or conformance.

Use SPARC for phase gates; the Brain search returned the curated
`sparc/capability-cards.md#sparc` card, SHA
`e301c166d2280228ac4228e3a3d67aeaae00a9751e43e0b7b25da20844b1b62b`,
receipt `2b32760c0e82`. This grounds the methodology only. Native workers execute;
Ruflo records coordination/checkpoints. No new vector engine or orchestration
service is needed for document parsing.

# PHASES

Commands marked **NEW** below are planned deliverables, not existing gates. Build
them as meaningful checks with nonzero test cases; absent implementations fail,
and a command printing success is never evidence. Run build/test tooling through
the repository sandbox. Existing CI and fixture contracts remain mandatory.

1. **Specification.** Inspect HANDOFF, MADRs, claims, issue #25 and current stage.
   Publish a precise support matrix, identity/provenance contract, adversarial
   inputs, ownership and compatibility plan. Define byte/depth/node/reference
   budgets and diagnostics before parser implementation. Rubric: scope and
   fixtures 40, provenance/security 35, ownership/compatibility 25.
   Gate: **NEW** `make contract-plan-check`, validating concrete profile and
   fixture manifests rather than prose presence alone.
2. **Pseudocode.** Design discovery, version dispatch, normalization, bounded
   reference resolution, evidence projection and canonical serialization. Reject
   duplicate JSON/YAML keys, unsafe tags, traversal and escaping symlinks; reject
   or explicitly bound aliases. Resolve only permitted local files and JSON
   Pointers; no network fetching, helper execution or schema evaluation. Test
   cycles, missing references and budget exhaustion as explicit incomplete states.
   Rubric: resolver/state design 40, failure bounds 40, determinism 20.
   Gate: **NEW** `make test-integration-contracts` with resolver/parser positive
   and negative cases implemented before production integration.
3. **Architecture.** Freeze typed versioned catalog/schema, shared source identity,
   validation and read-only DTOs. Reuse source-view authorization rather than
   accepting arbitrary paths. Root integrates CLI/navigation/manifests/locks;
   obtain independent architecture and security review. Document any new MADR
   needed for catalog lifecycle and compatibility. Rubric: typed catalog 40,
   existing infrastructure integration 35, independent review 25.
   Gates: `make build`, **NEW** `make test-integration-contracts` and **NEW**
   `make evaluate-integration-contracts` with separate contract goldens.
4. **Refinement.** Implement the profile end to end: offline extraction, validated
   import/query, evidence navigation and shared LikeC4 declared HTTP/messaging
   viewpoints. Support selected and all repositories, source visibility,
   diagnostics and existing theme/grouping conventions. Never merge channels
   because names match or invent deployments from declared servers. Treat all
   descriptions/examples as untrusted text, never executable HTML or instructions.
   Rubric: supported facts/UI 45, failure/security behavior 35, independent review
   20. Gates: contract tests/evaluation, **NEW**
   `make verify-integration-contract-determinism`, and existing ce.v1 regression
   and fixture gates unchanged. Repeat exact inputs twice and in a relocated
   checkout; require identical canonical bytes. Compare JSON/YAML semantic
   projections separately, since different source bytes have different provenance.
5. **Completion.** Demonstrate the actual application with evidence navigation,
   two repository contexts, all-repository views, malformed contracts and coverage
   gaps. Use controlled pinned fixtures; private Glufs access is not a prerequisite.
   Update HANDOFF, CHANGELOG and MADRs with compatibility implications. Freshly
   integrate stage, commit, push through the ordinary pre-push act gate once for
   the final candidate, run `make pr-check`, create an owner-review PR targeting
   stage and request Sonberg review. Report hosted checks separately. Apply
   MADR-0014 cleanup after preserving evidence and handing off active previews.
   Rubric: exact-source CI 40, browser evidence 30, docs/owner-review handoff 30.
   Gates: **NEW** `make test-integration-contracts-browser` using the opt-in browser
   harness; ordinary push hook and `make pr-check`; **NEW**
   `make integration-contracts-delivery-check` validating real source-bound receipts.

# STANDING RULES

Maintain a phase table: phase, status, source identity, checks, evidence paths,
deductions, score and next action. Print evidence and deductions before every
score. Require at least 95/100 per phase and overall; a failing mandatory gate
cannot be averaged away. Refine/regrade at most five times per phase, then report
remaining failures honestly. READY requires a verified running interface, not
only generated files. Do not claim a complete security audit from scoped checks.

Use Tigon32 for this operator's GitHub operations, verifying account before use.
Coordinate exact scopes through issue #3 and active claims. Root is integration
owner; use at most three native workers in isolated worktrees, with disjoint
parser, read-model and UI scopes after interfaces are frozen. Read-only reviewers
may share a checkout; never permit two writers in one worktree. Do not touch the
owner's uncommitted work or issue #25 scope without an acknowledged handoff.

Preserve existing fixture sources, revisions, goldens and thresholds. Add a
separate controlled corpus for this feature. Stable IDs derive from documented
source/revision/document/operation identities; titles and operationIds alone do
not establish identity across repositories. Evidence includes raw document hash,
source span and reference chain. Missing/unsupported inputs remain unknown or
incomplete. Assume immutable inputs during a run; do not claim race-proof host
filesystem isolation from ordinary path checks.

Follow MADR-0011: project dependency/security regressions remain mandatory; known
image findings and unavailable private corpus stay disclosed. Strict audit tools
remain fail-closed. Draft review is not a security certification or release. No
merges, auto-merge, force pushes, production publication or unauthorized deletion.

# COST GUARDRAILS

Use deterministic tools for discovery/parsing/validation; no inference is needed
in production. Route mechanical inventory to the smallest capable available native
worker, and use stronger review for security and architecture. Record actual
model/tool identities, elapsed time and results; unknown prices/savings remain
unknown. No external paid inference, source uploads or secret-bearing logs.
If paid work becomes necessary, stop before spending; anything above $1 or 20
paid calls requires explicit approval. Default budget is zero paid calls.

Reuse pinned image/dependency caches. Ordinary CI must not acquire new browser,
model or service images unless required; browser validation stays opt-in. Run
focused checks while developing and the full act gate through the final push,
avoiding an immediately duplicated full local run. Inspect current tool presence;
do not pretend route-cheap or loop-checkpoint scripts exist. Use existing Ruflo
memory only with exact-key readback, and disclose hook/readback failures rather
than claiming operational learning or savings from registration.

# DONE CRITERIA

All five phases and overall score are at least 95/100 with verifiable evidence.
The supported profile, unsupported states and identity boundaries are documented.
Controlled adversarial fixtures prove bounded offline behavior; old goldens are
unchanged; repeated/relocated runs are deterministic. Read-only APIs and shared
LikeC4 viewpoints work in the actual application with source navigation, grouped
repositories and explicit coverage. No inferred equivalence or runtime claims.

Executable final checks, with NEW commands implemented as specified above:

```sh
make contract-plan-check
make build
make test-integration-contracts
make evaluate-integration-contracts
make verify-integration-contract-determinism
make test-integration-contracts-browser
git fetch origin stage-1-contract-and-fixtures
git merge origin/stage-1-contract-and-fixtures
# Resolve, verify and commit all intended changes before this ordinary gated push.
git push -u origin <feature-branch>
make pr-check
make integration-contracts-delivery-check
```

Create the PR only with explicit base stage and owner review; preserve disclosed
findings, exact source/base/tool identities, actual hosted-check status and
remaining limitations. Record the PR and recovery instructions before eligible
worktree cleanup. Implementation awaits approval of this plan and the dependency
merge; this document alone authorizes neither feature execution nor release.
