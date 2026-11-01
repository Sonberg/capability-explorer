# Code-analysis options: deterministic evidence and model interpretation

Research date: 2026-11-01. Status: desk assessment for owner review, not adoption.
Existing source inspected at `3698e04`; subsequent security fixes do not establish
alternative-tool performance. No candidate was installed or executed. No private
source was uploaded. Glufs acceptance remains unavailable, tracked in issue #23.

## Recommendation

Keep Roslyn, the TypeScript checker and the bounded SQL extractor as canonical
fact producers. Evaluate Graphify code-only and Tree-sitter for additional language
coverage, SCIP for importing prebuilt symbol indexes, and scc for inventory.
Trial Repomix only for bounded, redacted context packaging; code2prompt is a
credible alternative to compare, not a second mandatory dependency. Security
query tools complement extraction. Understand-Anything offers useful navigation
ideas but its defaults conflict with this project's isolation and evidence rules.

## Separate the responsibilities

1. Deterministic extraction: syntax, compiler resolution, contract parsing and
   explicit rules with source spans and uncertainty. Repeatability does not prove
   completeness or correctness; compiler inference can still be deterministic.
2. Context preparation: selection, packing, compression and token counting. A
   packed prompt is not a semantic graph; compression can remove essential logic.
3. Model inference: explanations, domain suggestions, embeddings and ranking.
   Record model, prompt/input hash, parameters and cost when measurable. Mark output
   as unreviewed proposals; similarity never establishes identity or integration.
4. Human assertions: explicit reviewed bindings with revision and reviewer.
5. Security/metrics: vulnerability findings, sink inventories and size estimates,
   which are not architecture facts or business capabilities.

These categories can coexist in one tool. Classify each output and execution
mode, not merely the product name. Never merge inferred relationships into
canonical facts without the existing evidence/adoption rules.

## Weighted selection rubric

Weights: D determinism/provenance 25%; F semantic/language fit 20%; S safe offline
workflow 20%; I integration fit 15%; C operational cost 10%; M maintenance/license
fit 10%. Each rating is 0–5: 0 conflicts/absent, 1 weak, 2 substantial gaps,
3 workable with adaptation, 4 strong fit, 5 directly established for its scope.
Total = sum(weight × rating / 5). Scores are reasoned prioritization judgments,
**not measured accuracy, speed, security grades or vendor quality rankings**.
Cost scores estimate dependency/runtime burden; no token savings were measured.
Unknown runtime behavior remains an experiment, regardless of score.

Compare within role: a high inventory score does not make a counter a better
semantic extractor. Hard gates below override every total.

| Option / evaluated mode | Role | D | F | S | I | C | M | /100 | Evidence / rationale |
|---|---|---:|---:|---:|---:|---:|---:|---:|---|
| Existing Roslyn + TS + ce.v1 | semantic baseline | 5 | 4 | 4 | 5 | 4 | 3 | 86 | R01; strongest current contract fit; static build/config gaps and maintenance remain |
| Existing bounded SQL parser | SQL facts | 5 | 3 | 5 | 5 | 5 | 2 | 86 | R01; cheap/offline, incomplete dialects and dynamic SQL |
| Tree-sitter adapter | syntax inventory | 3 | 3 | 5 | 3 | 5 | 4 | 74 | R08; broad syntax, semantic/domain adapter needed |
| Universal Ctags JSON | declaration inventory | 3 | 2 | 5 | 3 | 5 | 4 | 70 | R03; cheap scoped tags, not resolved call semantics; GPL distribution review |
| SCIP prebuilt-index import | symbol navigation | 4 | 4 | 4 | 3 | 3 | 4 | 75 | R09; require sealed source/index match; indexer execution is a separate gate |
| Graphify code-only | supplementary graph | 3 | 4 | 4 | 3 | 4 | 4 | 72 | R06; explicit uncertainty, new runtime/adaptation and benchmark unknowns |
| Joern | dataflow/security complement | 3 | 4 | 4 | 2 | 2 | 3 | 63 | R11; graph/query overhead; C# maturity limitation |
| CodeQL no-build where supported | security/dataflow complement | 4 | 4 | 3 | 2 | 2 | 3 | 64 | R10; licensing/coverage and query cost need verification |
| Repomix trusted-config local mode | context packaging | 3 | 2 | 3 | 4 | 4 | 4 | 63 | R02; useful selection/security checks; executable config/processors need isolation |
| code2prompt local mode | context packaging | 3 | 2 | 3 | 3 | 4 | 4 | 60 | R04; templates/token accounting; traversal and output parity untested |
| scc local JSON | metrics/inventory | 4 | 2 | 5 | 4 | 5 | 4 | 78 | R05; low-cost inventory, complexity/cost estimates not business semantics |
| cloc local output | metrics/inventory | 4 | 2 | 5 | 3 | 4 | 4 | 73 | R12; alternative counter; no need to adopt both |
| ast-grep local rules | syntax query | 3 | 3 | 5 | 3 | 5 | 4 | 74 | R13; structural matching, not compiler-bound whole-program resolution |
| Understand-Anything full workflow | mixed syntax + LLM graph | 2 | 3 | 2 | 2 | 2 | 4 | 48 | R07; missing mandatory edge evidence, worktree redirect and model cost |
| Existing ExplanationService | model explanation | 2 | 3 | 3 | 5 | 2 | 3 | 59 | R01; cited IDs checked, free-text truth not proved; provider egress/cost |
| Existing embeddings | model retrieval | 2 | 2 | 3 | 4 | 2 | 3 | 52 | R01; ranking only, no binding proof; provider/version dependency |
| Existing Trivy/Gitleaks/Semgrep gate | security analysis | 4 | 3 | 4 | 5 | 4 | 4 | 79 | R01; complementary scopes; completion must be evidenced, sink rules not taint analysis |

Small score differences are not decisive. D/S gates and observed extra correct
facts dominate; changing weights requires recording the rationale and reranking.

## Material findings

Repomix packs context and uses Tree-sitter compression. Its documentation explicitly
warns that local JS/TS configuration and processors can execute code; remote config
is ignored unless trusted. Use operator-owned inert configuration and a sealed
export, no target processors/global configuration, no web upload. Secretlint is
defense in depth, not permission to disclose source. Compression is unsuitable as
the only evidence for calculation semantics. [R02]

code2prompt provides local context generation, token accounting and templates;
the current README also describes an optional entity-map feature. Do not classify
it as LLM inference merely because the output is a prompt. scc counts code and
estimates complexity; COCOMO/LOCOMO estimates are not measured project cost. [R04–05]

Understand-Anything revision `4e6d3299017a7432da15957874992837a315c5b4`
redirects worktree output to the main checkout unless
`UNDERSTAND_NO_WORKTREE_REDIRECT=1`. Its schema does not require per-edge evidence,
and repair defaults can supply `depends_on` or normalize database/db to table.
Those behaviors must not be imported as observed facts. MIT; use navigation/tour
ideas as inspiration rather than run the stock workflow. [R07]

Graphify revision `f5155badddaa3286c78a51653791131e9e2e4d29`, branch `v8`,
package 0.9.100, separates EXTRACTED/INFERRED/AMBIGUOUS; deterministic resolution
also uses INFERRED. Candidate mode is `--code-only`, `GRAPHIFY_BACKEND=none`, no
credentials, network disabled, explicit source root. Review graph direction and
IDs rather than assuming interchange equivalence. Python/parser dependencies add
cost; avoid `[all]`. Apache-2.0 with MIT-covered historical portions in NOTICE.
The Knolli discovery article's no-MCP claim is contradicted by current Graphify's
`graphify-mcp` entry point. [R06, R14]

SCIP describes navigation, not business architecture. The scip-typescript quickstart
installs analyzed dependencies, so prefer owner-produced sealed indexes first.
CodeQL supports C# build-mode `none`; do not claim it always requires a build.
Missing generated/dependency code and licensing remain considerations. Joern's
documented C# maturity is Medium-Low. [R09–11]

## Existing implementation evidence

- `platform/src/CapabilityExplorer.Extractors.CSharp/CSharpExtractor.cs`,
  `ProjectModel.cs`, `ReferenceSelection.cs`: Roslyn semantic models with selected
  references, not complete MSBuild evaluation.
- `extractors/ts/src/extract/{extractor,project,calls,next,http-clients}.ts`:
  checker plus bounded framework rules, no arbitrary target configuration execution.
- `extractors/ts/src/sql/`: bounded lexer/parser and calculation IR; unknown dynamic SQL.
- `schema/v1/record.schema.json`, `platform/src/CapabilityExplorer.Evaluation/Evaluator.cs`:
  evidence and coverage contracts; fixed-fixture success is not estate-wide accuracy.
- `platform/src/CapabilityExplorer.Binding/Binder.cs`: explicit human maps.
- `platform/src/CapabilityExplorer.Interpretation/ExplanationService.cs`,
  `OpenAiEmbeddingModel.cs`, `platform/src/CapabilityExplorer.Store/EmbeddingStore.cs`:
  explanations and similarity kept separate from source facts.
- `scripts/security-audit-lib.mjs`, `config/security/{audit-scope.json,rule-coverage.json}`:
  security evidence gate; new in this branch, full integrated audit still pending.

## Experiment and adoption gates

Use the same sealed synthetic corpus and exact tool/dependency/configuration hashes
for baseline and candidates. Never install/run analyzed projects. Disable network,
ambient config, plugins, credentials, clipboard and telemetry; constrain files,
CPU, memory and output. Any required external model is a separate opt-in trial.

Measure two-run canonical byte equality, source-span validity, correct additional
declarations/references, false links, unsupported cases, wall time, peak memory,
disk and packed tokens. Include duplicate names across repositories, unresolved
dependencies, partial classes, SQL dialect gaps and hostile config/symlink fixtures.
Do not sum duplicate facts from several tools as increased coverage. Record
conflicts and which producer made each claim. Private-corpus results remain blocked
by #23; synthetic success cannot substitute for them.

First compare scc inventory and one context packer; then Graphify code-only versus
Tree-sitter/SCIP additions on missing coverage. Keep CodeQL/Joern in a separate
security/dataflow experiment. No candidate enters ordinary CI until it demonstrates
useful incremental evidence without weakening provenance or security. Pin versions,
licenses, dependency footprint and feature coverage in the resulting implementation
MADR. No mandatory online service or continuous model spend is proposed here.

## Reference log

All references accessed 2026-11-01. R01 is source inspection; others are upstream
documentation/source claims unless noted. No benchmark claims were reproduced.
Unpinned documentation is research evidence only; adoption requires immutable pins.

| ID | Reference | Use / limitations |
|---|---|---|
| R01 | Local source paths above, baseline `3698e04` | Implemented behavior and known limits, not independent real-corpus proof |
| R02 | [Repomix](https://repomix.com/), [security](https://repomix.com/guide/security), [compression](https://repomix.com/guide/code-compress) | Packing, config execution, lossy compression; docs advertise MIT |
| R03 | [Ctags repository](https://github.com/universal-ctags/ctags), [JSON format](https://docs.ctags.io/en/latest/man/ctags-json-output.5.html), [manual](https://docs.ctags.io/en/latest/man/ctags.1.html) | Tags, limitations, GPL-2.0; initial `/output-json.html` URL inaccessible, corrected to manual |
| R04 | [code2prompt README](https://raw.githubusercontent.com/mufeedvh/code2prompt/main/README.md), [repository/license](https://github.com/mufeedvh/code2prompt) | Local context tooling, MIT; no local test |
| R05 | [scc README](https://raw.githubusercontent.com/boyter/scc/master/README.md) | Metrics, local container mode, MIT; upstream speed claims unverified |
| R06 | [Graphify pinned README](https://github.com/Graphify-Labs/graphify/blob/f5155badddaa3286c78a51653791131e9e2e4d29/README.md), [architecture](https://github.com/Graphify-Labs/graphify/blob/f5155badddaa3286c78a51653791131e9e2e4d29/ARCHITECTURE.md), [manifest](https://github.com/Graphify-Labs/graphify/blob/f5155badddaa3286c78a51653791131e9e2e4d29/pyproject.toml), [NOTICE](https://github.com/Graphify-Labs/graphify/blob/f5155badddaa3286c78a51653791131e9e2e4d29/NOTICE) | Mixed pipeline, protocol and dependencies; no execution |
| R07 | [Understand-Anything README](https://github.com/Egonex-AI/Understand-Anything/blob/4e6d3299017a7432da15957874992837a315c5b4/README.md), [skill](https://github.com/Egonex-AI/Understand-Anything/blob/4e6d3299017a7432da15957874992837a315c5b4/understand-anything-plugin/skills/understand/SKILL.md), [schema](https://github.com/Egonex-AI/Understand-Anything/blob/4e6d3299017a7432da15957874992837a315c5b4/understand-anything-plugin/packages/core/src/schema.ts) | Isolation and graph-evidence gaps; no execution |
| R08 | [Tree-sitter](https://tree-sitter.github.io/tree-sitter/) | Syntax parsing; grammar/license review per chosen parser still needed |
| R09 | [SCIP](https://github.com/scip-code/scip), [TS indexer](https://raw.githubusercontent.com/sourcegraph/scip-typescript/main/README.md), [dotnet indexer](https://github.com/sourcegraph/scip-dotnet) | Protocol versus indexer side effects; individual dependency/license review pending |
| R10 | [CodeQL CLI](https://docs.github.com/en/code-security/concepts/code-scanning/codeql/codeql-cli), [build modes](https://docs.github.com/en/code-security/how-tos/find-and-fix-code-vulnerabilities/manage-your-configuration/codeql-for-compiled-languages) | No-build coverage; private-source licensing eligibility not established |
| R11 | [Joern](https://docs.joern.io/) | Property graphs/dataflow, language maturity; full license/dependency audit pending |
| R12 | [cloc](https://github.com/AlDanial/cloc) | Alternative source counter; not benchmarked |
| R13 | [ast-grep](https://github.com/ast-grep/ast-grep) | Structural search/lint; not benchmarked |
| R14 | [Knolli alternatives](https://www.knolli.ai/post/graphify-alternatives) | Discovery only; technical recommendations checked against primary sources |
