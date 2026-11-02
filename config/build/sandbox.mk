# Every build, install and test step runs inside scripts/sandbox (disposable container, non-root,
# read-only root fs, no host env). Steps that do not need the network run with --offline.
# Behind a TLS-intercepting proxy: export SANDBOX_EXTRA_CA_FILE=/path/to/ca.pem

SANDBOX := scripts/sandbox
DOTNET  := $(SANDBOX) dotnet
NODE    := $(SANDBOX) node
OFFLINE := $(SANDBOX) --offline
include config/runtime/images.env
include config/build/container-engine.mk

.PHONY: help ci test fixtures fixtures-lock fixtures-compile golden golden-check \
        restore ui build test-ts test-contracts test-evaluation test-extractors test-explorer test-store evaluate evaluate-golden \
		extract-cs evaluate-cs extract-ts evaluate-ts extract-sql evaluate-sql analyze analyze-worker explore ollama postgres dev dev-data dev-down clean local-check pr-check install-git-hooks actions-audit test-local-ci test-mapping-replay test-package-feeds security-check

help:
	@echo "make local-check     run the full GitHub CI workflow locally through nektos/gh-act before pushing"
	@echo "make install-git-hooks  install this repository pre-push gate (preserves existing hooks)"
	@echo "make actions-audit   audit workflow dependencies with Wayne Ashley Berry gh-act (separate tool)"
	@echo "make dev             local environment: PostgreSQL (pgvector) + optional Ollama, import artifacts, then explore"
	@echo "     DEV_OLLAMA=0 skips Ollama; an Ollama startup or pull failure is non-fatal"
	@echo "     PG_PORT=5433 OLLAMA_CHAT=none (skip the chat model) and every explore option; make dev-down stops the containers"
	@echo "make dev-data        only the import + embed step of make dev (e.g. while the explorer runs)"
	@echo "make analyze-worker  runs the explorer's Analyze code requests on the host (make dev starts one; CE_DATABASE='...' to store)"
	@echo "make ci              everything CI runs: fixture compile checks + all tests"
	@echo "make test            golden check + TS tests + .NET contract tests + store tests (Testcontainers)"
	@echo "make fixtures        rebuild fixture git repos (fixtures/.repos) and verify revisions.lock.json"
	@echo "make fixtures-lock   rebuild fixture repos and rewrite revisions.lock.json (after editing fixtures/src)"
	@echo "make golden          regenerate fixtures/expected from fixtures/golden/facts"
	@echo "make fixtures-compile  check every fixture revision compiles (or fails) as intended"
	@echo "make explore [ARTIFACTS=dir] [EVALUATION=report.json] [CAPABILITIES=file] [SOURCES=/abs/repo,...] [PORT=5080]"
	@echo "     browse the model at http://127.0.0.1:5080"
	@echo "     private source checks and acquisition credentials belong to make analyze-worker, not Explorer"
	@echo "     optional LLM: make ollama (local, no key), the Settings page (gear icon), or INTERPRETATION=foundry FOUNDRY_ENDPOINT=... FOUNDRY_DEPLOYMENT=..."
	@echo "     Cmd/Ctrl+K: command menu; with CE_DATABASE and an embedding model also similar functionality (vector search)"
	@echo "make ollama [OLLAMA_EMBED=nomic-embed-text] [OLLAMA_CHAT=qwen2.5:7b|none]  start a local model server, pull the models, save them as the settings"
	@echo "                   INTERPRETATION=recorded RECORDINGS=dir  replays stored responses, no network model"
	@echo "make evaluate ACTUAL=out/ [REPO=id,id]  score extractor output against fixtures/expected (report: out/evaluation.json)"
	@echo "make analyze CAPABILITY=id  check out, extract and store artifacts for one capability (picked up live by explore)"
	@echo "     GITHUB_AUTH=auto|git|none  LOCKED=1 (analyse the last check)  PACKAGES=0 (skip nuget.org downloads)"
	@echo "     private NuGet/npm feeds: configure on the host; Explorer Settings shows only the public catalogue"
	@echo "     CE_DATABASE='Host=...;Database=...;...' also imports into PostgreSQL (off otherwise); BINDINGS=bindings.json binds @unbound hints"
	@echo "     with CE_DATABASE and a model (make ollama, the Settings page, or CE_MODEL_ENDPOINT + CE_EMBEDDING_MODEL) it also embeds for similarity search"
	@echo "     PostgreSQL needs pgvector: make postgres starts one and prints its CE_DATABASE"
	@echo "make evaluate-cs     run the C# extractor over every fixture revision and gate it on the csharp golden units"
	@echo "make evaluate-ts     same for the TypeScript/JavaScript extractor"
	@echo "make evaluate-sql    same for the SQL extractor (T-SQL projects, PostgreSQL migrations)"

local-check:
	scripts/local-check

pr-check:
	scripts/pr-check

install-git-hooks:
	scripts/install-git-hooks

actions-audit:
	scripts/actions-audit audit

test-local-ci:
	$(OFFLINE) node bash tests/local-ci.test.sh

test-mapping-replay:
	$(OFFLINE) node node --test tests/mapping-replay.test.mjs

test-package-feeds:
	$(OFFLINE) node node --test tests/package-feeds.test.mjs tests/explorer-environment.test.mjs

security-check:
	scripts/security-check

SECURITY_MANIFEST ?= .sandbox-cache/security-audit/manifest.json
.PHONY: security-plan-check security-baseline security-audit security-verify test-security-gates test-security-regressions test-corpus-replay
security-plan-check:
	scripts/security-audit plan-check

security-baseline:
	scripts/security-audit baseline $(SECURITY_MANIFEST)

security-audit:
	scripts/security-collect run .sandbox-cache/security-audit

security-verify:
	scripts/security-audit verify $(SECURITY_MANIFEST)

test-security-gates:
	$(OFFLINE) node node --test tests/security-audit.test.mjs tests/security-collect.test.mjs

test-corpus-replay:
	$(OFFLINE) node node --test tests/corpus-replay.test.mjs

test-security-regressions: build
	$(OFFLINE) dotnet sh -c 'cd platform && dotnet test --no-build tests/CapabilityExplorer.Capabilities.Tests && dotnet test --no-build tests/CapabilityExplorer.Interpretation.Tests && dotnet test --no-build tests/CapabilityExplorer.Extractors.Tests && dotnet test --no-build tests/CapabilityExplorer.Explorer.Tests'

ci: test-container-engine-core security-check fixtures-compile test

test: test-postmerge-analysis test-local-ci test-mapping-replay test-package-feeds test-security-gates test-corpus-replay test-binding-preview golden-check test-ts test-contracts test-evaluation test-extractors evaluate-golden evaluate-cs evaluate-ts evaluate-sql test-explorer test-store

.PHONY: test-binding-preview test-binding-preview-browser
test-binding-preview: build
	$(OFFLINE) dotnet dotnet test --no-build platform/tests/CapabilityExplorer.Explorer.Tests --filter FullyQualifiedName~BindingPreviewTests
	$(OFFLINE) node node --experimental-strip-types --test tests/binding-preview.test.mjs
	$(OFFLINE) node node --experimental-transform-types --test tests/request.test.mjs

BINDING_PREVIEW_URL ?= http://localhost:5108
test-binding-preview-browser:
	SANDBOX_NODE_IMAGE=$(BROWSER_IMAGE) $(SANDBOX) node node tests/binding-preview.browser.mjs $(BINDING_PREVIEW_URL)

fixtures:
	$(OFFLINE) node scripts/build-fixtures.sh

fixtures-lock:
	$(OFFLINE) node scripts/build-fixtures.sh --update

fixtures-compile: fixtures
	$(DOTNET) scripts/compile-fixtures.sh restore-dotnet
	$(NODE) scripts/compile-fixtures.sh restore-node
	$(OFFLINE) dotnet scripts/compile-fixtures.sh build-dotnet
	$(OFFLINE) node scripts/compile-fixtures.sh build-node

golden: fixtures
	$(OFFLINE) node node fixtures/golden/build.mjs

golden-check: fixtures
	$(OFFLINE) node node fixtures/golden/build.mjs --check

# Network is only used here; everything after restore runs offline.
restore:
	$(DOTNET) sh -c 'cd platform && dotnet restore --locked-mode CapabilityExplorer.slnx'
	$(NODE) sh -c 'cd extractors/ts && npm ci'

# Explorer UI: React + Tailwind (Vite). Dependencies are installed with network, the build runs offline.
UI := platform/src/CapabilityExplorer.Explorer/client
ui: likec4-deps
	$(NODE) sh -c 'cd $(UI) && npm ci'
	$(OFFLINE) node sh -c 'cd $(UI) && npm run -s build'

build: restore ui
	$(OFFLINE) dotnet sh -c 'cd platform && dotnet build --no-restore -nologo CapabilityExplorer.slnx'

test-ts: restore
	$(OFFLINE) node sh -c 'cd extractors/ts && npm run -s typecheck && npm test'

test-contracts: fixtures build
	$(OFFLINE) dotnet sh -c 'cd platform && dotnet test --no-build tests/CapabilityExplorer.Contracts.Tests'

test-evaluation: fixtures build
	$(OFFLINE) dotnet sh -c 'cd platform && dotnet test --no-build tests/CapabilityExplorer.Evaluation.Tests'

CE := dotnet platform/src/CapabilityExplorer.Cli/bin/Debug/net10.0/ce.dll

test-extractors: build
	$(OFFLINE) dotnet sh -c 'cd platform && dotnet test --no-build tests/CapabilityExplorer.Extractors.Tests'

# Self-check of the scorer: the golden set scored against itself must pass every threshold.
evaluate-golden: fixtures build
	$(OFFLINE) dotnet $(CE) evaluate --expected fixtures/expected --actual fixtures/expected \
	  --config fixtures/evaluation.json --repos fixtures/.repos

# Score real extractor output: make evaluate ACTUAL=out/ [REPO=forecast-cs,pricing-node]
evaluate: fixtures build
	@test -n "$(ACTUAL)" || { echo "usage: make evaluate ACTUAL=<dir with .jsonl artifacts>"; exit 2; }
	$(OFFLINE) dotnet $(CE) evaluate --expected fixtures/expected --actual $(ACTUAL) \
	  --config fixtures/evaluation.json --repos fixtures/.repos --json $(ACTUAL)/evaluation.json \
	  $(if $(REPO),--repository $(REPO))

# C# extractor (Roslyn, no MSBuild/restore of the target): offline, over `git archive` of each locked revision.
EXTRACTED := .sandbox-cache/extracted
extract-cs: fixtures build fixtures-compile
	rm -rf $(EXTRACTED)/cs
	$(OFFLINE) dotnet bash scripts/extract-fixtures.sh cs $(EXTRACTED)/cs

evaluate-cs: extract-cs
	$(OFFLINE) dotnet $(CE) evaluate --expected fixtures/expected --actual $(EXTRACTED)/cs \
	  --config fixtures/evaluation.csharp.json --repos fixtures/.repos --language csharp --json $(EXTRACTED)/cs/evaluation.json

# TypeScript/JavaScript extractor (compiler API; the analysed packages are never installed).
extract-ts: fixtures restore
	rm -rf $(EXTRACTED)/ts
	$(OFFLINE) node bash scripts/extract-fixtures.sh ts $(EXTRACTED)/ts

evaluate-ts: extract-ts build
	$(OFFLINE) dotnet $(CE) evaluate --expected fixtures/expected --actual $(EXTRACTED)/ts \
	  --config fixtures/evaluation.extractors.ts.json --repos fixtures/.repos --language typescript,javascript --json $(EXTRACTED)/ts/evaluation.json

# SQL extractor (T-SQL database projects and PostgreSQL migrations): read as text, no database, nothing executed.
extract-sql: fixtures restore
	rm -rf $(EXTRACTED)/sql
	$(OFFLINE) node bash scripts/extract-fixtures.sh sql $(EXTRACTED)/sql

evaluate-sql: extract-sql build
	$(OFFLINE) dotnet $(CE) evaluate --expected fixtures/expected --actual $(EXTRACTED)/sql \
	  --config fixtures/evaluation.extractors.json --repos fixtures/.repos --language sql --json $(EXTRACTED)/sql/evaluation.json

test-explorer: fixtures build
	$(OFFLINE) dotnet sh -c 'cd platform && dotnet test --no-build tests/CapabilityExplorer.Explorer.Tests && dotnet test --no-build tests/CapabilityExplorer.Interpretation.Tests && dotnet test --no-build tests/CapabilityExplorer.Capabilities.Tests'

# Analyse a capability's repositories: export (network + token) -> packages (nuget.org) -> extract (offline).
analyze: build
	@test -n "$(CAPABILITY)" || { echo "usage: make analyze CAPABILITY=<id>  (see $(CAPABILITIES))"; exit 2; }
	GITHUB_AUTH=$(GITHUB_AUTH) BINDINGS=$(BINDINGS) INCLUDE_TESTS=$(INCLUDE_TESTS) scripts/analyze $(CAPABILITY) $(CAPABILITIES)

# Read-only UI over artifacts. The container publishes the port on 127.0.0.1 only.
# Analysed artifacts come after the golden set: for the same repository, commit and unit, the later one wins.
ARTIFACTS ?= fixtures/expected,.sandbox-cache/artifacts
CAPABILITIES ?= capabilities.json
GITHUB_AUTH ?= git
ANALYZE_JOBS ?= 2
export ANALYZE_JOBS
PORT ?= 5080
EXPLORER_SOURCE ?= files
# Human-maintained service map that binds @unbound hints (see schema/README.md). Used by explore and by the optional import in analyze.
BINDINGS ?= $(wildcard bindings.json)
# Extra local directories to mount read-only (local repositories in $(CAPABILITIES) are mounted automatically):
# SOURCES=/abs/path,/abs/other — e.g. SOURCES=$$HOME/Code to add repositories from the UI without restarting.
explore: fixtures build
	@echo "Capability Explorer: http://127.0.0.1:$(PORT)"
	@# Only the trusted host worker receives acquisition credentials.
	CE_HOST_HOME="$$HOME" \
	SANDBOX_PASS_ENV="CE_HOST_HOME,CE_DATABASE,CE_FOUNDRY_API_KEY" \
	SANDBOX_SOURCES="$$(s="$(SOURCES)"; c="$$(scripts/capability-sources $(CAPABILITIES))"; echo "$${s}$${s:+$${c:+,}}$${c}")" \
	$(SANDBOX) --feed-catalog --port $(PORT) dotnet dotnet platform/src/CapabilityExplorer.Explorer/bin/Debug/net10.0/ce-explorer.dll \
	  --source $(EXPLORER_SOURCE) --artifacts $(ARTIFACTS) --repos fixtures/.repos $(if $(EVALUATION),--evaluation $(EVALUATION)) \
	  --capabilities $(CAPABILITIES) --workspace .sandbox-cache/sources $(if $(BINDINGS),--bindings $(BINDINGS)) \
	  $(if $(INTERPRETATION),--interpretation:provider $(INTERPRETATION) --interpretation:cacheDirectory .sandbox-cache/interpretations) \
	  $(if $(FOUNDRY_ENDPOINT),--interpretation:endpoint $(FOUNDRY_ENDPOINT)) \
	  $(if $(FOUNDRY_DEPLOYMENT),--interpretation:deployment $(FOUNDRY_DEPLOYMENT)) \
	  $(if $(RECORDINGS),--interpretation:recordings $(RECORDINGS)) \
	  $(if $(RECORD_TO),--interpretation:recordTo $(RECORD_TO))

# Needs the Docker socket so Testcontainers can start PostgreSQL (see scripts/sandbox --docker).
test-store: build
	# The SDK image has a Docker socket for Testcontainers, but no host Docker CLI.
	# Only the new HostDocker qualification probe is native-only; existing tests remain gated.
	$(SANDBOX) --docker dotnet sh -c 'cd platform && dotnet test --no-build tests/CapabilityExplorer.Store.Tests --filter "Profile!=HostDocker"'

# Local models for explanations and similarity search: no key, nothing leaves the machine. Listens on the host's
# 127.0.0.1:11434 only; the sandbox reaches it as host.docker.internal. The native Ollama app on the same port works too
# (and uses the GPU, which Docker on macOS cannot). OLLAMA_CHAT=none skips the chat model (a few GB).
# Saves .sandbox-cache/model.json unless settings were already saved (from the Settings page or an earlier run).
OLLAMA_EMBED ?= nomic-embed-text
OLLAMA_CHAT ?= qwen2.5:7b
ollama:
	@curl -fsS http://127.0.0.1:11434/api/version >/dev/null 2>&1 || $(CE_CONTAINER_ENGINE) start ce-ollama >/dev/null 2>&1 || \
	  $(CE_CONTAINER_ENGINE) run -d --name ce-ollama -p 127.0.0.1:11434:11434 -v ce-ollama:/root/.ollama $(OLLAMA_IMAGE) >/dev/null
	@if [ -n "$${SANDBOX_EXTRA_CA_FILE:-}" ] && [ -n "$$($(CE_CONTAINER_ENGINE) ps -aq --filter name=ce-ollama)" ]; then bash scripts/ollama-ca $(CE_CONTAINER_ENGINE) ce-ollama "$$SANDBOX_EXTRA_CA_FILE"; fi
	@for i in $$(seq 30); do curl -fsS http://127.0.0.1:11434/api/version >/dev/null 2>&1 && break; sleep 1; done
	curl -fsS http://127.0.0.1:11434/api/pull -d '{"model":"$(OLLAMA_EMBED)","stream":false}' >/dev/null || { echo "Ollama model pull failed; inspect $(CE_CONTAINER_ENGINE) logs ce-ollama for TLS or network-policy details" >&2; exit 1; }
	[ "$(OLLAMA_CHAT)" = none ] || curl -fsS http://127.0.0.1:11434/api/pull -d '{"model":"$(OLLAMA_CHAT)","stream":false}' >/dev/null || { echo "Ollama model pull failed; inspect $(CE_CONTAINER_ENGINE) logs ce-ollama for TLS or network-policy details" >&2; exit 1; }
	@if [ -f .sandbox-cache/model.json ]; then echo "kept existing .sandbox-cache/model.json (change it on the Settings page)"; else \
	  chat='"$(OLLAMA_CHAT)"'; [ "$(OLLAMA_CHAT)" = none ] && chat=null; \
	  mkdir -p .sandbox-cache && (umask 077 && printf '{\n  "endpoint": "http://host.docker.internal:11434/v1",\n  "chatModel": %s,\n  "embeddingModel": "%s"\n}\n' \
	    "$$chat" '$(OLLAMA_EMBED)' > .sandbox-cache/model.json) && \
	  echo "saved .sandbox-cache/model.json: a running explorer picks it up on restart (or Save on the Settings page)"; fi

# Local PostgreSQL with pgvector, on the host's 127.0.0.1 only (dev credentials, never a shared database). The sandbox
# reaches it as host.docker.internal. Data survives restarts in the ce-postgres volume (docker volume rm ce-postgres resets).
# ponytail: on Linux, host.docker.internal is the bridge gateway, which a 127.0.0.1-only port does not answer; set SANDBOX_NETWORK=host there.
PG_PORT ?= 5433
DEV_DATABASE = Host=host.docker.internal;Port=$(PG_PORT);Database=ce;Username=ce;Password=ce
# Analysis ownership/publication now runs on the host, while Explorer/import clients stay containerized.
DEV_WORKER_DATABASE ?= Host=127.0.0.1;Port=$(PG_PORT);Database=ce;Username=ce;Password=ce
comma := ,
postgres:
	@$(CE_CONTAINER_ENGINE) start ce-postgres >/dev/null 2>&1 || $(CE_CONTAINER_ENGINE) run -d --name ce-postgres -p 127.0.0.1:$(PG_PORT):5432 \
	  -e POSTGRES_USER=ce -e POSTGRES_PASSWORD=ce -e POSTGRES_DB=ce -v ce-postgres:/var/lib/postgresql/data $(PG_IMAGE) >/dev/null
	@# Over TCP: during first-time init the server only listens on its socket, so a socket check is ready too early.
	@for i in $$(seq 60); do $(CE_CONTAINER_ENGINE) exec ce-postgres pg_isready -h 127.0.0.1 -U ce -d ce >/dev/null 2>&1 && break; sleep 1; done
	@$(CE_CONTAINER_ENGINE) exec ce-postgres pg_isready -h 127.0.0.1 -U ce -d ce >/dev/null || { echo "PostgreSQL did not start: $(CE_CONTAINER_ENGINE) logs ce-postgres"; exit 1; }
	@echo "PostgreSQL ready: CE_DATABASE='$(DEV_DATABASE)'"

# Everything for local development. Imports what explore shows ($(ARTIFACTS)) and embeds it (unchanged artifacts and
# symbols are skipped, so a second run is quick), then runs the explorer with the database. Analyse more with
# make analyze CAPABILITY=<id> CE_DATABASE='...' (printed above) while it runs; the explorer reloads by itself.
# The explorer's Analyze code button queues a request that the analyze worker, started next to it, runs on the host.
dev: dev-data
	@CE_DATABASE='$(DEV_WORKER_DATABASE)' GITHUB_AUTH=$(GITHUB_AUTH) BINDINGS=$(BINDINGS) scripts/analyze-worker $(CAPABILITIES) </dev/null & \
	  trap "kill $$! 2>/dev/null" EXIT; \
	  $(MAKE) --no-print-directory explore CE_DATABASE='$(DEV_DATABASE)'

analyze-worker: build
	GITHUB_AUTH=$(GITHUB_AUTH) BINDINGS=$(BINDINGS) scripts/analyze-worker $(CAPABILITIES)

DEV_REANALYZE ?= 0
DEV_OLLAMA ?= 1
dev-data: build postgres
	@if [ "$(DEV_OLLAMA)" = 1 ]; then $(MAKE) --no-print-directory ollama || echo "-- Ollama unavailable; continuing without model services"; else echo "-- Ollama skipped (DEV_OLLAMA=0)"; fi
	@if [ "$(DEV_REANALYZE)" = 1 ]; then GITHUB_AUTH=$(GITHUB_AUTH) BINDINGS=$(BINDINGS) INCLUDE_TESTS=$(INCLUDE_TESTS) scripts/analyze-all $(CAPABILITIES); fi
	@mkdir -p .sandbox-cache && find $(subst $(comma), ,$(ARTIFACTS)) -name '*.jsonl' 2>/dev/null | sort > .sandbox-cache/dev-artifacts.txt
	@echo "== import $$(wc -l < .sandbox-cache/dev-artifacts.txt | tr -d ' ') artifact(s) and embed"
	@CE_DATABASE='$(DEV_DATABASE)' SANDBOX_PASS_ENV=CE_DATABASE SANDBOX_SOURCES="" $(DOTNET) bash -c '\
	  ce=platform/src/CapabilityExplorer.Cli/bin/Debug/net10.0/ce.dll; \
	  dotnet $$ce migrate; \
	  xargs dotnet $$ce import < .sandbox-cache/dev-artifacts.txt | grep -v "^unchanged" || true; \
	  dotnet $$ce embed || echo "-- embeddings not updated (see above); similarity search uses what is stored"'

dev-down:
	-$(CE_CONTAINER_ENGINE) stop ce-postgres ce-ollama

clean:
	rm -rf fixtures/.repos fixtures/.work extractors/ts/node_modules $(UI)/node_modules platform/src/CapabilityExplorer.Explorer/wwwroot
	find platform -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +

# Shared LikeC4 renderer and standalone fixture regression harness.
.PHONY: likec4-deps likec4-dist likec4-check likec4-preview
ci: likec4-check
likec4-deps:
	$(NODE) sh -c 'cd spikes/likec4 && npm ci'

likec4-dist: likec4-deps
	$(OFFLINE) node sh -c 'cd spikes/likec4 && npm run build'

likec4-check: likec4-dist
	$(OFFLINE) node sh -c 'cd spikes/likec4 && npm test && node --experimental-strip-types scripts/check-model.mjs'

likec4-preview: likec4-check
	$(SANDBOX) --port 5091 node sh -c 'cd spikes/likec4 && npm run preview -- --host 0.0.0.0 --port 5091 --strictPort'

# Optional visual checks against running ports 5090/5091; not an extra hosted-CI image.
.PHONY: likec4-browser-check
likec4-browser-check:
	SANDBOX_NODE_IMAGE=$(BROWSER_IMAGE) $(SANDBOX) node node --experimental-strip-types spikes/likec4/test/browser.mjs

.PHONY: likec4-interaction-check
likec4-interaction-check:
	SANDBOX_NODE_IMAGE=$(BROWSER_IMAGE) $(SANDBOX) node node spikes/likec4/test/embedded-routing.mjs

# Opt-in diagnostics against the candidate preview; reuse the existing browser image.
.PHONY: likec4-trace-check
likec4-trace-check:
	SANDBOX_NODE_IMAGE=$(BROWSER_IMAGE) $(SANDBOX) node sh -c 'cd spikes/likec4 && node test/routing-trace-browser.mjs && node test/immediate-layout-browser.mjs && node test/routing-lifecycle.mjs'

# Live model invariants run without a browser or additional service image.
.PHONY: live-architecture-check live-architecture-browser-check
live-architecture-check: likec4-deps
	$(OFFLINE) node sh -c 'cd spikes/likec4 && node --experimental-strip-types --test test/live-model*.test.mjs'

# Use an already running isolated explorer; network/credentials remain explicit.
LIVE_ARCHITECTURE_URL ?= http://host.docker.internal:5096
live-architecture-browser-check:
	CE_LIVE_ARCHITECTURE_BASE_URL='$(LIVE_ARCHITECTURE_URL)' SANDBOX_PASS_ENV=CE_LIVE_ARCHITECTURE_BASE_URL SANDBOX_NODE_IMAGE=$(BROWSER_IMAGE) $(SANDBOX) node node scripts/test-live-architecture.mjs

# Opt-in source-bound comparison; requires separately running baseline/candidate.
.PHONY: live-architecture-benchmark
live-architecture-benchmark:
	SANDBOX_PASS_ENV=CE_LIVE_BENCHMARK_BASELINE_URL,CE_LIVE_BENCHMARK_CANDIDATE_URL,CE_LIVE_BENCHMARK_SAMPLES SANDBOX_NODE_IMAGE=$(BROWSER_IMAGE) $(SANDBOX) node node scripts/benchmark-live-architecture.mjs

.PHONY: analyze-all
analyze-all: build
	GITHUB_AUTH=$(GITHUB_AUTH) BINDINGS=$(BINDINGS) INCLUDE_TESTS=$(INCLUDE_TESTS) scripts/analyze-all $(CAPABILITIES)

.PHONY: test-postmerge-analysis
test-postmerge-analysis:
	$(OFFLINE) node node --test tests/postmerge-analysis.test.mjs

# Tool-free research evidence gates: no candidate install, image or model call.
.PHONY: test-research-foundation
ci: test-research-foundation
test-research-foundation:
	$(OFFLINE) node node --test tests/research-foundation.test.mjs
	$(OFFLINE) node node scripts/research/check-code-insights.mjs foundation
