# Repository insight tools: research preflight and proposed trials

Date: 2026-11-02 Europe/Stockholm. Source baseline: merged main `1e1122b`.
Scope: refreshed desk evidence and an execution plan. No candidate was installed,
benchmarked or adopted. Runtime evidence below concerns Ollama separately.
Recommendations and the complete prompt/register are published in
[issue #31](https://github.com/Sonberg/capability-explorer/issues/31).

## Start from the existing research

[The existing report](code-analysis-options.md) already compares 17 modes, including
all six user-supplied tools and the existing implementation. Its 14 reference
groups, November 1 scores and older source baseline are retained as historical
assessment rather than silently represented as new measurements.

The [machine-readable register](code-insights-research-register.json) preserves
those scores, recomputes their arithmetic, records six refreshed upstream source
pins and adds claim-level planning references. GitHub metadata establishes a
source identity and declared license, not an installed binary, complete dependency
license review or permission to redistribute it.

The [complete Brain Prompt](../plans/code-insights-research-brain-prompt.md)
defines the future research, trial protocol, scoring and owner decisions.
Its [council protocol](../plans/code-insights-tool-council-protocol.md) adds the
unattended package order, independent SME/adversary/advisor/judge roles for each
tool/mode, separate gates and one introduction PR per accepted tool. A tool-free
foundation comes first; later dependent PRs wait for owner merge. All tool council
verdicts and runtime trials remain pending. Review of this plan is not tool approval.

## Recommendations for the research issue

| Role | Candidate direction | Why it is worth testing | Decision boundary |
|---|---|---|---|
| Canonical facts | Retain Roslyn, TypeScript checker and bounded SQL baseline | Already matches source/evidence/ce.v1 contracts | Current fixture success does not establish complete estate-wide accuracy |
| Inventory | Trial scc; compare cloc only if needed | Language/size/complexity inventory could expose analysis coverage gaps cheaply | Complexity and effort estimates are metrics, not business capabilities or measured delivery cost |
| Context packaging | Select one of Repomix/code2prompt for the first trial | May prepare bounded cited context and expose token/retention tradeoffs | Packed text is not a semantic graph; configuration and compression require explicit controls |
| Additional structural coverage | Compare Graphify code-only with Ctags, Tree-sitter, SCIP and ast-grep against a named gap | Broader syntax/index coverage may complement existing languages and reference resolution | Count only correct additional evidence; no global winner across different roles |
| Security/dataflow | Evaluate CodeQL/Joern separately alongside existing Semgrep/Trivy/Gitleaks | Different engines may address different security questions | Rules, taint/dataflow, dependencies and image scans are distinct scopes |
| Interpretation/navigation | Study Understand-Anything and mixed Graphify workflows separately | Useful navigation/domain-proposal ideas | Model output, deterministic derivation and reviewed assertions must remain distinct |

These recommendations propose experiment candidates, not approved trials or dependencies. No implementation
issue should be justified solely by a desk score or an upstream performance claim.
An implementation recommendation needs a concrete missing insight, incremental
correct output, provenance, failure behavior, cost and the smallest compatible adapter.

## Refreshed primary-source observations

Repomix documents execution risks in configuration and processors. The trial must
use reviewed operator-owned configuration and an isolated source export; a secret
scanner is not permission to upload private code.
[Primary security documentation](https://repomix.com/guide/security).

Universal Ctags documents JSON Lines output and its build dependency. This supports
evaluating declaration inventory, not assuming compiler-resolved call semantics.
[Primary JSON format documentation](https://docs.ctags.io/en/latest/man/ctags-json-output.5.html).

Graphify's source has advanced from the existing report's `f5155ba` to `32e5efe`.
The newly pinned README describes code-only parsing and a disabled-backend mode,
while other modes use models. Its derived-edge terminology must not be equated
automatically with generative inference. The earlier numerical score is not a
review of all changes in this newer revision.
[Pinned upstream README](https://github.com/Graphify-Labs/graphify/blob/32e5efe935e6844ef39a5e33409e4efe848f2172/README.md).

Understand-Anything's observed source pin remains `4e6d329`. Its documented hybrid
workflow separates structural parsing from model summaries/domain mapping. Review
the existing report's pinned isolation/schema findings before any execution;
do not adopt the full workflow from its UI screenshots.
[Pinned source](https://github.com/Egonex-AI/Understand-Anything/tree/4e6d3299017a7432da15957874992837a315c5b4).

Knolli is retained as a discovery reference. Competitive claims are verified
against primary sources rather than used as the scoring authority.
[Discovery article](https://www.knolli.ai/post/graphify-alternatives).

## Scoring and evidence

Retain D25/F20/S20/I15/C10/M10 for continuity; justify changes before reranking.
Use separate role rankings, evidence confidence, unknowns and overriding safety/
provenance gates. Historical scores remain available in the existing report and
register. Tiny score differences do not settle selection. The new work must
measure incremental useful insight and run weight-sensitivity analysis, not
produce another untested leaderboard.

The proposed research gate scripts do not yet exist. The future executor must
implement them, prove negative cases, and obtain real trial/decision evidence.
This planning pass validates the register arithmetic, required candidate coverage,
source pins, references and the prompt's phase sections only. It is not a passed
experiment, a completed research study or a full CI claim.

## Ollama retry: separate operational evidence

After the operator's temporary whitelist, `nomic-embed-text` downloaded and a real
local request returned two finite 768-dimensional embedding vectors, HTTP 200.
No large chat model or external paid model was used. This proves local embedding
operation, not the quality or relevance of code-analysis insights.

The post-merge sandbox development rerun subsequently produced six demand-pricing
artifacts, then stopped because both configured Glufs repositories returned 404
under Tigon32. Weekly-sales, database import/embedding and port 5124 startup were
not reached. No complete shared-analysis receipt exists; issue #23 remains the
access blocker. Existing synthetic previews on 5116/5118 were preserved.
