# Every build, install and test step runs inside scripts/sandbox (disposable container, non-root,
# read-only root fs, no host env). Steps that do not need the network run with --offline.
# Behind a TLS-intercepting proxy: export SANDBOX_EXTRA_CA_FILE=/path/to/ca.pem

SANDBOX := scripts/sandbox
DOTNET  := $(SANDBOX) dotnet
NODE    := $(SANDBOX) node
OFFLINE := $(SANDBOX) --offline

.PHONY: help ci test fixtures fixtures-lock fixtures-compile golden golden-check \
        restore ui build test-ts test-contracts test-evaluation test-extractors test-explorer test-store evaluate evaluate-golden \
        extract-cs evaluate-cs extract-ts evaluate-ts extract-sql evaluate-sql analyze explore ollama clean local-check install-git-hooks actions-audit test-local-ci security-check

help:
	@echo "make local-check     run the full GitHub CI workflow locally through nektos/gh-act before pushing"
	@echo "make install-git-hooks  install this repository pre-push gate (preserves existing hooks)"
	@echo "make actions-audit   audit workflow dependencies with Wayne Ashley Berry gh-act (separate tool)"
	@echo "make ci              everything CI runs: fixture compile checks + all tests"
	@echo "make test            golden check + TS tests + .NET contract tests + store tests (Testcontainers)"
	@echo "make fixtures        rebuild fixture git repos (fixtures/.repos) and verify revisions.lock.json"
	@echo "make fixtures-lock   rebuild fixture repos and rewrite revisions.lock.json (after editing fixtures/src)"
	@echo "make golden          regenerate fixtures/expected from fixtures/golden/facts"
	@echo "make fixtures-compile  check every fixture revision compiles (or fails) as intended"
	@echo "make explore [ARTIFACTS=dir] [EVALUATION=report.json] [CAPABILITIES=file] [SOURCES=/abs/repo,...] [PORT=5080]"
	@echo "     browse the model at http://127.0.0.1:5080"
	@echo "     private GitHub repos: GITHUB_TOKEN, or 'gh auth login'; GITHUB_AUTH=git also tries the git credential helper"
	@echo "     optional LLM: make ollama (local, no key), the Settings page (gear icon), or INTERPRETATION=foundry FOUNDRY_ENDPOINT=... FOUNDRY_DEPLOYMENT=..."
	@echo "     Cmd/Ctrl+K: command menu; with CE_DATABASE and an embedding model also similar functionality (vector search)"
	@echo "make ollama [OLLAMA_EMBED=nomic-embed-text] [OLLAMA_CHAT=qwen2.5:7b|none]  start a local model server, pull the models, save them as the settings"
	@echo "                   INTERPRETATION=recorded RECORDINGS=dir  replays stored responses, no network model"
	@echo "make evaluate ACTUAL=out/ [REPO=id,id]  score extractor output against fixtures/expected (report: out/evaluation.json)"
	@echo "make analyze CAPABILITY=id  check out, extract and store artifacts for one capability (picked up live by explore)"
	@echo "     GITHUB_AUTH=auto|git|none  LOCKED=1 (analyse the last check)  PACKAGES=0 (skip nuget.org downloads)"
	@echo "     private NuGet feeds: nuget.config in the repo, credentials via %VAR% + NUGET_PASS_ENV=VAR,VAR (only the packages step sees them)"
	@echo "     CE_DATABASE='Host=...;Database=...;...' also imports into PostgreSQL (off otherwise); BINDINGS=bindings.json binds @unbound hints"
	@echo "     with CE_DATABASE and a model (make ollama, the Settings page, or CE_MODEL_ENDPOINT + CE_EMBEDDING_MODEL) it also embeds for similarity search"
	@echo "     PostgreSQL needs pgvector: docker run -d -p 5432:5432 -e POSTGRES_PASSWORD=ce pgvector/pgvector:pg17"
	@echo "make evaluate-cs     run the C# extractor over every fixture revision and gate it on the csharp golden units"
	@echo "make evaluate-ts     same for the TypeScript/JavaScript extractor"
	@echo "make evaluate-sql    same for the SQL extractor (T-SQL projects, PostgreSQL migrations)"

local-check:
	scripts/local-check

install-git-hooks:
	scripts/install-git-hooks

actions-audit:
	scripts/actions-audit audit

test-local-ci:
	$(OFFLINE) node bash tests/local-ci.test.sh

security-check:
	scripts/security-check

ci: security-check fixtures-compile test

test: test-local-ci golden-check test-ts test-contracts test-evaluation test-extractors evaluate-golden evaluate-cs evaluate-ts evaluate-sql test-explorer test-store

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
ui:
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
	GITHUB_AUTH=$(GITHUB_AUTH) BINDINGS=$(BINDINGS) scripts/analyze $(CAPABILITY) $(CAPABILITIES)

# Read-only UI over artifacts. The container publishes the port on 127.0.0.1 only.
# Analysed artifacts come after the golden set: for the same repository, commit and unit, the later one wins.
ARTIFACTS ?= fixtures/expected,.sandbox-cache/artifacts
CAPABILITIES ?= capabilities.json
GITHUB_AUTH ?= auto
PORT ?= 5080
# Human-maintained service map that binds @unbound hints (see schema/README.md). Used by explore and by the optional import in analyze.
BINDINGS ?= $(wildcard bindings.json)
# Extra local directories to mount read-only (local repositories in $(CAPABILITIES) are mounted automatically):
# SOURCES=/abs/path,/abs/other — e.g. SOURCES=$$HOME/Code to add repositories from the UI without restarting.
explore: fixtures build
	@echo "Capability Explorer: http://127.0.0.1:$(PORT)"
	@# GitHub credentials for private capability repositories are resolved on the host and forwarded
	@# only into this container (see scripts/github-token). The token is never printed.
	@# Local capability repositories (absolute or ~/ paths in $(CAPABILITIES)) are mounted read-only, and the
	@# host's home is forwarded so ~ means the same thing inside the container.
	GITHUB_TOKEN="$$(scripts/github-token $(GITHUB_AUTH))" CE_HOST_HOME="$$HOME" \
	SANDBOX_PASS_ENV="$${SANDBOX_PASS_ENV:+$$SANDBOX_PASS_ENV,}GITHUB_TOKEN,CE_HOST_HOME,CE_DATABASE,CE_FOUNDRY_API_KEY" \
	SANDBOX_SOURCES="$$(s="$(SOURCES)"; c="$$(scripts/capability-sources $(CAPABILITIES))"; echo "$${s}$${s:+$${c:+,}}$${c}")" \
	$(SANDBOX) --port $(PORT) dotnet dotnet platform/src/CapabilityExplorer.Explorer/bin/Debug/net10.0/ce-explorer.dll \
	  --artifacts $(ARTIFACTS) --repos fixtures/.repos $(if $(EVALUATION),--evaluation $(EVALUATION)) \
	  --capabilities $(CAPABILITIES) --workspace .sandbox-cache/sources $(if $(BINDINGS),--bindings $(BINDINGS)) \
	  $(if $(INTERPRETATION),--interpretation:provider $(INTERPRETATION) --interpretation:cacheDirectory .sandbox-cache/interpretations) \
	  $(if $(FOUNDRY_ENDPOINT),--interpretation:endpoint $(FOUNDRY_ENDPOINT)) \
	  $(if $(FOUNDRY_DEPLOYMENT),--interpretation:deployment $(FOUNDRY_DEPLOYMENT)) \
	  $(if $(RECORDINGS),--interpretation:recordings $(RECORDINGS)) \
	  $(if $(RECORD_TO),--interpretation:recordTo $(RECORD_TO))

# Needs the Docker socket so Testcontainers can start PostgreSQL (see scripts/sandbox --docker).
test-store: build
	$(SANDBOX) --docker dotnet sh -c 'cd platform && dotnet test --no-build tests/CapabilityExplorer.Store.Tests'

# Local models for explanations and similarity search: no key, nothing leaves the machine. Listens on the host's
# 127.0.0.1:11434 only; the sandbox reaches it as host.docker.internal. The native Ollama app on the same port works too
# (and uses the GPU, which Docker on macOS cannot). OLLAMA_CHAT=none skips the chat model (a few GB).
# Saves .sandbox-cache/model.json unless settings were already saved (from the Settings page or an earlier run).
OLLAMA_IMAGE ?= ollama/ollama:0.40.5
OLLAMA_EMBED ?= nomic-embed-text
OLLAMA_CHAT ?= qwen2.5:7b
ollama:
	@curl -fsS http://127.0.0.1:11434/api/version >/dev/null 2>&1 || docker start ce-ollama >/dev/null 2>&1 || \
	  docker run -d --name ce-ollama -p 127.0.0.1:11434:11434 -v ce-ollama:/root/.ollama $(OLLAMA_IMAGE) >/dev/null
	@for i in $$(seq 30); do curl -fsS http://127.0.0.1:11434/api/version >/dev/null 2>&1 && break; sleep 1; done
	curl -fsS http://127.0.0.1:11434/api/pull -d '{"model":"$(OLLAMA_EMBED)","stream":false}' >/dev/null
	[ "$(OLLAMA_CHAT)" = none ] || curl -fsS http://127.0.0.1:11434/api/pull -d '{"model":"$(OLLAMA_CHAT)","stream":false}' >/dev/null
	@if [ -f .sandbox-cache/model.json ]; then echo "kept existing .sandbox-cache/model.json (change it on the Settings page)"; else \
	  chat='"$(OLLAMA_CHAT)"'; [ "$(OLLAMA_CHAT)" = none ] && chat=null; \
	  mkdir -p .sandbox-cache && (umask 077 && printf '{\n  "endpoint": "http://host.docker.internal:11434/v1",\n  "chatModel": %s,\n  "embeddingModel": "%s"\n}\n' \
	    "$$chat" '$(OLLAMA_EMBED)' > .sandbox-cache/model.json) && \
	  echo "saved .sandbox-cache/model.json: a running explorer picks it up on restart (or Save on the Settings page)"; fi

clean:
	rm -rf fixtures/.repos fixtures/.work extractors/ts/node_modules $(UI)/node_modules platform/src/CapabilityExplorer.Explorer/wwwroot
	find platform -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
