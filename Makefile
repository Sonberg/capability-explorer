# Every build, install and test step runs inside scripts/sandbox (disposable container, non-root,
# read-only root fs, no host env). Steps that do not need the network run with --offline.
# Behind a TLS-intercepting proxy: export SANDBOX_EXTRA_CA_FILE=/path/to/ca.pem

SANDBOX := scripts/sandbox
DOTNET  := $(SANDBOX) dotnet
NODE    := $(SANDBOX) node
OFFLINE := $(SANDBOX) --offline

.PHONY: help ci test fixtures fixtures-lock fixtures-compile golden golden-check \
        restore build test-ts test-contracts test-store clean

help:
	@echo "make ci              everything CI runs: fixture compile checks + all tests"
	@echo "make test            golden check + TS tests + .NET contract tests + store tests (Testcontainers)"
	@echo "make fixtures        rebuild fixture git repos (fixtures/.repos) and verify revisions.lock.json"
	@echo "make fixtures-lock   rebuild fixture repos and rewrite revisions.lock.json (after editing fixtures/src)"
	@echo "make golden          regenerate fixtures/expected from fixtures/golden/facts"
	@echo "make fixtures-compile  check every fixture revision compiles (or fails) as intended"

ci: fixtures-compile test

test: golden-check test-ts test-contracts test-store

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

build: restore
	$(OFFLINE) dotnet sh -c 'cd platform && dotnet build --no-restore -nologo CapabilityExplorer.slnx'

test-ts: restore
	$(OFFLINE) node sh -c 'cd extractors/ts && npm run -s typecheck && npm test'

test-contracts: fixtures build
	$(OFFLINE) dotnet sh -c 'cd platform && dotnet test --no-build tests/CapabilityExplorer.Contracts.Tests'

# Needs the Docker socket so Testcontainers can start PostgreSQL (see scripts/sandbox --docker).
test-store: build
	$(SANDBOX) --docker dotnet sh -c 'cd platform && dotnet test --no-build tests/CapabilityExplorer.Store.Tests'

clean:
	rm -rf fixtures/.repos fixtures/.work extractors/ts/node_modules
	find platform -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
