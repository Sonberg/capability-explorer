include config/runtime/images.env
include config/build/container-engine.mk
CE_ENGINE_COMMAND := scripts/container-engine
# Trusted project development runs on the host for fast IDE, debugger and hot-reload loops.
# Only capability analysis uses scripts/sandbox; analysed repositories are untrusted input.
DOTNET := dotnet
NODE   := node
NPM    := npm
NODE_PLATFORM := $(shell $(NODE) -p "process.platform + '-' + process.arch + '-abi' + process.versions.modules" 2>/dev/null || echo unavailable)

.PHONY: help ci test fixtures fixtures-lock fixtures-compile golden golden-check \
        restore restore-dotnet restore-ts restore-ui ui frontend backend build test-ts test-contracts test-evaluation test-extractors test-explorer test-store evaluate evaluate-golden \
		analyze-all extract-cs evaluate-cs extract-ts evaluate-ts extract-sql evaluate-sql analyze analyze-worker explore ollama postgres dev dev-data dev-down clean local-check pr-check install-git-hooks actions-audit test-local-ci test-package-feeds security-check

help:
	@echo "make local-check     run the full GitHub CI workflow locally through nektos/gh-act before pushing"
	@echo "make install-git-hooks  install this repository pre-push gate (preserves existing hooks)"
	@echo "make actions-audit   audit workflow dependencies with Wayne Ashley Berry gh-act (separate tool)"
	@echo "make dev             local environment: PostgreSQL (pgvector) + optional Ollama, import artifacts, then explore"
	@echo "     CE_CONTAINER_ENGINE=auto prefers Docker, then Podman; set docker or podman to pin"
	@echo "     DEV_OLLAMA=0 skips Ollama; an Ollama startup or pull failure is non-fatal"
	@echo "make frontend        run the Vite frontend dev server on the host (hot reload)"
	@echo "make backend         run the Explorer API on the host at http://127.0.0.1:$(PORT)"
	@echo "     PG_PORT=5433 OLLAMA_CHAT=none (skip the chat model) and every explore option; make dev-down stops the containers"
	@echo "make dev-data        only the import + embed step of make dev (e.g. while the explorer runs)"
	@echo "make analyze-worker  runs the explorer's Analyze code requests on the host (make dev starts one; CE_DATABASE='...' to store)"
	@echo "make ci              everything CI runs: fixture compile checks + all tests"
	@echo "make test            golden check + TS tests + .NET contract tests + store tests (Testcontainers)"
	@echo "make fixtures        rebuild fixture git repos (fixtures/.repos) and verify revisions.lock.json"
	@echo "make fixtures-lock   rebuild fixture repos and rewrite revisions.lock.json (after editing fixtures/src)"
	@echo "make golden          regenerate fixtures/expected from fixtures/golden/facts"
	@echo "make fixtures-compile  check every fixture revision compiles (or fails) as intended"
	@echo "make explore [ARTIFACTS=dir] [EVALUATION=report.json] [CAPABILITIES=file] [PORT=5080]"
	@echo "     browse the model at http://127.0.0.1:5080"
	@echo "     private source checks and acquisition credentials belong to make analyze-worker, not Explorer"
	@echo "     optional LLM: make ollama (local, no key), the Settings page (gear icon), or INTERPRETATION=foundry FOUNDRY_ENDPOINT=... FOUNDRY_DEPLOYMENT=..."
	@echo "     Cmd/Ctrl+K: command menu; with CE_DATABASE and an embedding model also similar functionality (vector search)"
	@echo "make ollama [OLLAMA_EMBED=nomic-embed-text] [OLLAMA_CHAT=qwen2.5:7b|none]  start a local model server, pull the models, save them as the settings"
	@echo "                   INTERPRETATION=recorded RECORDINGS=dir  replays stored responses, no network model"
	@echo "make evaluate ACTUAL=out/ [REPO=id,id]  score extractor output against fixtures/expected (report: out/evaluation.json)"
	@echo "make analyze CAPABILITY=id  check out, extract and store artifacts for one capability (picked up live by explore)"
	@echo "     GITHUB_AUTH=auto|git|none  LOCKED=1 (analyse the last check)  PACKAGES=0 (skip nuget.org downloads)"
	@echo "     INCLUDE_TESTS=1 includes test projects/files; tests are excluded from the production graph by default"
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
	bash tests/local-ci.test.sh

security-check:
	scripts/security-check

ci:
	$(MAKE) EXECUTION=sandbox ci

test: test-local-ci test-package-feeds golden-check test-ts test-contracts test-evaluation test-extractors evaluate-golden evaluate-cs evaluate-ts evaluate-sql test-explorer test-store test-capability-ingestion-browser

.PHONY: test-capability-ingestion-browser
test-capability-ingestion-browser: restore-ui
	node spikes/likec4/node_modules/typescript/bin/tsc --noEmit -p platform/src/CapabilityExplorer.Explorer/client/tsconfig.json --types vite/client
	node spikes/likec4/node_modules/typescript/bin/tsc --noEmit --ignoreConfig --strict --target ES2022 --module ESNext --moduleResolution Bundler --esModuleInterop --allowImportingTsExtensions --skipLibCheck --types node --typeRoots spikes/likec4/node_modules/@types tests/capability-ingestion.browser.ts
	node --experimental-strip-types --test tests/capability-ingestion.browser.ts

test-package-feeds:
	node --test tests/package-feeds.test.mjs tests/explorer-environment.test.mjs

fixtures:
	scripts/build-fixtures.sh

fixtures-lock:
	scripts/build-fixtures.sh --update

fixtures-compile: fixtures
	scripts/compile-fixtures.sh restore-dotnet
	scripts/compile-fixtures.sh restore-node
	scripts/compile-fixtures.sh build-dotnet
	scripts/compile-fixtures.sh build-node

golden: fixtures
	$(NODE) fixtures/golden/build.mjs

golden-check: fixtures
	$(NODE) fixtures/golden/build.mjs --check

# Host-native dependency restore. Locked inputs keep CI and developer machines aligned.
restore: restore-dotnet restore-ts restore-ui

DOTNET_RESTORE_INPUTS := $(shell find platform -type f \( -name '*.csproj' -o -name '*.props' -o -name '*.targets' -o -name 'packages.lock.json' -o -name '*.slnx' -o -name 'global.json' -o -name 'NuGet.Config' \) -print)
DOTNET_RESTORE_STAMP := .sandbox-cache/stamps/dotnet-restore
$(DOTNET_RESTORE_STAMP): $(DOTNET_RESTORE_INPUTS)
	cd platform && $(DOTNET) restore --locked-mode CapabilityExplorer.slnx
	@mkdir -p $(dir $(DOTNET_RESTORE_STAMP)) && touch $(DOTNET_RESTORE_STAMP)

restore-dotnet: $(DOTNET_RESTORE_STAMP)

TS_DEPS_STAMP := extractors/ts/node_modules/.ce-host-$(NODE_PLATFORM)
$(TS_DEPS_STAMP): extractors/ts/package-lock.json
	cd extractors/ts && $(NPM) ci --ignore-scripts
	@touch $(TS_DEPS_STAMP)

restore-ts: $(TS_DEPS_STAMP)

# Explorer UI: React + Tailwind (Vite), installed and built directly on the host.
UI := platform/src/CapabilityExplorer.Explorer/client
UI_DEPS_STAMP := $(UI)/node_modules/.ce-host-$(NODE_PLATFORM)
$(UI_DEPS_STAMP): $(UI)/package-lock.json
	cd $(UI) && $(NPM) ci --ignore-scripts
	@touch $(UI_DEPS_STAMP)

restore-ui: $(UI_DEPS_STAMP) restore-likec4-native

ui: restore-ui
	cd $(UI) && $(NPM) run -s build

frontend: restore-ui
	cd $(UI) && $(NPM) run dev -- --host 127.0.0.1

build: restore ui
	cd platform && $(DOTNET) build --no-restore -nologo CapabilityExplorer.slnx

test-ts: restore
	cd extractors/ts && $(NPM) run -s typecheck && $(NPM) test

test-contracts: fixtures build
	cd platform && $(DOTNET) test --no-build tests/CapabilityExplorer.Contracts.Tests

test-evaluation: fixtures build
	cd platform && $(DOTNET) test --no-build tests/CapabilityExplorer.Evaluation.Tests

CE := platform/src/CapabilityExplorer.Cli/bin/Debug/net10.0/ce.dll

test-extractors: build
	cd platform && $(DOTNET) test --no-build tests/CapabilityExplorer.Extractors.Tests

# Self-check of the scorer: the golden set scored against itself must pass every threshold.
evaluate-golden: fixtures build
	$(DOTNET) $(CE) evaluate --expected fixtures/expected --actual fixtures/expected \
	  --config fixtures/evaluation.json --repos fixtures/.repos

# Score real extractor output: make evaluate ACTUAL=out/ [REPO=forecast-cs,pricing-node]
evaluate: fixtures build
	@test -n "$(ACTUAL)" || { echo "usage: make evaluate ACTUAL=<dir with .jsonl artifacts>"; exit 2; }
	$(DOTNET) $(CE) evaluate --expected fixtures/expected --actual $(ACTUAL) \
	  --config fixtures/evaluation.json --repos fixtures/.repos --json $(ACTUAL)/evaluation.json \
	  $(if $(REPO),--repository $(REPO))

# C# extractor (Roslyn, no MSBuild/restore of the target): offline, over `git archive` of each locked revision.
EXTRACTED := .sandbox-cache/extracted
extract-cs: fixtures build fixtures-compile
	rm -rf $(EXTRACTED)/cs
	bash scripts/extract-fixtures.sh cs $(EXTRACTED)/cs

evaluate-cs: extract-cs
	$(DOTNET) $(CE) evaluate --expected fixtures/expected --actual $(EXTRACTED)/cs \
	  --config fixtures/evaluation.csharp.json --repos fixtures/.repos --language csharp --json $(EXTRACTED)/cs/evaluation.json

# TypeScript/JavaScript extractor (compiler API; the analysed packages are never installed).
extract-ts: fixtures restore
	rm -rf $(EXTRACTED)/ts
	bash scripts/extract-fixtures.sh ts $(EXTRACTED)/ts

evaluate-ts: extract-ts build
	$(DOTNET) $(CE) evaluate --expected fixtures/expected --actual $(EXTRACTED)/ts \
	  --config fixtures/evaluation.extractors.ts.json --repos fixtures/.repos --language typescript,javascript --json $(EXTRACTED)/ts/evaluation.json

# SQL extractor (T-SQL database projects and PostgreSQL migrations): read as text, no database, nothing executed.
extract-sql: fixtures restore
	rm -rf $(EXTRACTED)/sql
	bash scripts/extract-fixtures.sh sql $(EXTRACTED)/sql

evaluate-sql: extract-sql build
	$(DOTNET) $(CE) evaluate --expected fixtures/expected --actual $(EXTRACTED)/sql \
	  --config fixtures/evaluation.extractors.json --repos fixtures/.repos --language sql --json $(EXTRACTED)/sql/evaluation.json

test-explorer: fixtures build
	cd platform && $(DOTNET) test --no-build tests/CapabilityExplorer.Explorer.Tests && $(DOTNET) test --no-build tests/CapabilityExplorer.Interpretation.Tests && $(DOTNET) test --no-build tests/CapabilityExplorer.Capabilities.Tests

# Analyse a capability's repositories: export (network + token) -> packages (nuget.org) -> extract (offline).
analyze: build
	@test -n "$(CAPABILITY)" || { echo "usage: make analyze CAPABILITY=<id>  (see $(CAPABILITIES))"; exit 2; }
	GITHUB_AUTH=$(GITHUB_AUTH) BINDINGS=$(BINDINGS) INCLUDE_TESTS=$(INCLUDE_TESTS) scripts/analyze $(CAPABILITY) $(CAPABILITIES)

# Every capability in $(CAPABILITIES), one after another; stops at the first failure.
analyze-all: build
	GITHUB_AUTH=$(GITHUB_AUTH) BINDINGS=$(BINDINGS) INCLUDE_TESTS=$(INCLUDE_TESTS) scripts/analyze-all $(CAPABILITIES)

# Read-only host-native UI/API over artifacts.
# Analysed artifacts come after the golden set: for the same repository, commit and unit, the later one wins.
ARTIFACTS ?= fixtures/expected,.sandbox-cache/artifacts
CAPABILITIES ?= capabilities.json
GITHUB_AUTH ?= git
ANALYZE_JOBS ?= 1
export ANALYZE_JOBS
PORT ?= 5080
EXPLORER_SOURCE ?= files
# Human-maintained service map that binds @unbound hints (see schema/README.md). Used by explore and by the optional import in analyze.
BINDINGS ?= $(wildcard bindings.json)
explore: fixtures build
	@echo "Capability Explorer: http://127.0.0.1:$(PORT)"
	ASPNETCORE_URLS="http://127.0.0.1:$(PORT)" $(NODE) scripts/explorer-host.mjs platform/src/CapabilityExplorer.Explorer/bin/Debug/net10.0/ce-explorer.dll \
	  --source $(EXPLORER_SOURCE) --artifacts $(ARTIFACTS) --repos fixtures/.repos $(if $(EVALUATION),--evaluation $(EVALUATION)) \
	  --capabilities $(CAPABILITIES) --workspace .sandbox-cache/sources $(if $(BINDINGS),--bindings $(BINDINGS)) \
	  $(if $(INTERPRETATION),--interpretation:provider $(INTERPRETATION) --interpretation:cacheDirectory .sandbox-cache/interpretations) \
	  $(if $(FOUNDRY_ENDPOINT),--interpretation:endpoint $(FOUNDRY_ENDPOINT)) \
	  $(if $(FOUNDRY_DEPLOYMENT),--interpretation:deployment $(FOUNDRY_DEPLOYMENT)) \
	  $(if $(RECORDINGS),--interpretation:recordings $(RECORDINGS)) \
	  $(if $(RECORD_TO),--interpretation:recordTo $(RECORD_TO))

backend: explore

# Testcontainers talks to the developer's Docker daemon directly.
test-store: build
	cd platform && $(DOTNET) test --no-build tests/CapabilityExplorer.Store.Tests

# Local models for explanations and similarity search: no key, nothing leaves the machine. Listens on the host's
# 127.0.0.1:11434 only. The native Ollama app on the same port works too
# (and uses the GPU, which Docker on macOS cannot). OLLAMA_CHAT=none skips the chat model (a few GB).
# Saves .sandbox-cache/model.json unless settings were already saved (from the Settings page or an earlier run).
OLLAMA_IMAGE ?= docker.io/ollama/ollama:0.40.5
OLLAMA_EMBED ?= nomic-embed-text
OLLAMA_CHAT ?= qwen2.5:7b
ollama:
	@curl -fsS http://127.0.0.1:11434/api/version >/dev/null 2>&1 || $(CE_ENGINE_COMMAND) start ce-ollama >/dev/null 2>&1 || \
	  $(CE_ENGINE_COMMAND) run -d --name ce-ollama -p 127.0.0.1:11434:11434 -v ce-ollama:/root/.ollama $(OLLAMA_IMAGE) >/dev/null
	@if [ -n "$${SANDBOX_EXTRA_CA_FILE:-}" ] && [ -n "$$($(CE_ENGINE_COMMAND) ps -aq --filter name=ce-ollama)" ]; then bash scripts/ollama-ca $(CE_ENGINE_COMMAND) ce-ollama "$$SANDBOX_EXTRA_CA_FILE"; fi
	@for i in $$(seq 30); do curl -fsS http://127.0.0.1:11434/api/version >/dev/null 2>&1 && break; sleep 1; done
	curl -fsS http://127.0.0.1:11434/api/pull -d '{"model":"$(OLLAMA_EMBED)","stream":false}' >/dev/null || { echo "Ollama model pull failed; inspect $(CE_ENGINE_COMMAND) logs ce-ollama for TLS or network-policy details" >&2; exit 1; }
	[ "$(OLLAMA_CHAT)" = none ] || curl -fsS http://127.0.0.1:11434/api/pull -d '{"model":"$(OLLAMA_CHAT)","stream":false}' >/dev/null || { echo "Ollama model pull failed; inspect $(CE_ENGINE_COMMAND) logs ce-ollama for TLS or network-policy details" >&2; exit 1; }
	@if [ -f .sandbox-cache/model.json ]; then echo "kept existing .sandbox-cache/model.json (change it on the Settings page)"; else \
	  chat='"$(OLLAMA_CHAT)"'; [ "$(OLLAMA_CHAT)" = none ] && chat=null; \
	  mkdir -p .sandbox-cache && (umask 077 && printf '{\n  "endpoint": "http://127.0.0.1:11434/v1",\n  "chatModel": %s,\n  "embeddingModel": "%s"\n}\n' \
	    "$$chat" '$(OLLAMA_EMBED)' > .sandbox-cache/model.json) && \
	  echo "saved .sandbox-cache/model.json: a running explorer picks it up on restart (or Save on the Settings page)"; fi

# Local PostgreSQL with pgvector, on the host's 127.0.0.1 only (dev credentials, never a shared database).
# Data survives restarts in the ce-postgres volume (docker volume rm ce-postgres resets).
PG_IMAGE ?= docker.io/pgvector/pgvector:0.8.6-pg17
PG_PORT ?= 5433
DEV_DATABASE = Host=127.0.0.1;Port=$(PG_PORT);Database=ce;Username=ce;Password=ce
comma := ,
postgres:
	@$(CE_ENGINE_COMMAND) start ce-postgres >/dev/null 2>&1 || $(CE_ENGINE_COMMAND) run -d --name ce-postgres -p 127.0.0.1:$(PG_PORT):5432 \
	  -e POSTGRES_USER=ce -e POSTGRES_PASSWORD=ce -e POSTGRES_DB=ce -v ce-postgres:/var/lib/postgresql/data $(PG_IMAGE) >/dev/null
	@# Over TCP: during first-time init the server only listens on its socket, so a socket check is ready too early.
	@for i in $$(seq 60); do $(CE_ENGINE_COMMAND) exec ce-postgres pg_isready -h 127.0.0.1 -U ce -d ce >/dev/null 2>&1 && break; sleep 1; done
	@$(CE_ENGINE_COMMAND) exec ce-postgres pg_isready -h 127.0.0.1 -U ce -d ce >/dev/null || { echo "PostgreSQL did not start: $(CE_ENGINE_COMMAND) logs ce-postgres"; exit 1; }
	@echo "PostgreSQL ready: CE_DATABASE='$(DEV_DATABASE)'"

# Everything for local development. Imports what explore shows ($(ARTIFACTS)) and embeds it (unchanged artifacts and
# symbols are skipped, so a second run is quick), then runs the explorer with the database. Analyse more with
# make analyze CAPABILITY=<id> CE_DATABASE='...' (printed above) while it runs; the explorer reloads by itself.
# The explorer's Analyze code button queues a request that the analyze worker, started next to it, runs on the host.
dev: dev-data
	@CE_DATABASE='$(DEV_DATABASE)' GITHUB_AUTH=$(GITHUB_AUTH) BINDINGS=$(BINDINGS) scripts/analyze-worker $(CAPABILITIES) </dev/null & \
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
	@CE_DATABASE='$(DEV_DATABASE)' bash -c '\
	  ce=platform/src/CapabilityExplorer.Cli/bin/Debug/net10.0/ce.dll; \
	  dotnet $$ce migrate; \
	  xargs dotnet $$ce import < .sandbox-cache/dev-artifacts.txt | grep -v "^unchanged" || true; \
	  dotnet $$ce embed || echo "-- embeddings not updated (see above); similarity search uses what is stored"'

dev-down:
	-$(CE_ENGINE_COMMAND) stop ce-postgres ce-ollama

clean:
	rm -rf fixtures/.repos fixtures/.work extractors/ts/node_modules $(UI)/node_modules platform/src/CapabilityExplorer.Explorer/wwwroot
	find platform -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +

.PHONY: restore-likec4-native
restore-likec4-native:
	cd spikes/likec4 && $(NPM) ci --ignore-scripts
