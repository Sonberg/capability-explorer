# Small deterministic contract checks run in ordinary CI; large experiments remain opt-in.
.PHONY: test-issue28-contract issue28-gate issue28-acceptance issue28-foundation-check
test: test-issue28-contract
test-issue28-contract:
	scripts/sandbox --offline node node --test tests/issue28-contract.test.mjs scripts/benchmarks/issue28-tools.test.mjs

issue28-gate:
ifeq ($(PHASE),specification)
	scripts/sandbox --offline node node scripts/issue28-gate.mjs specification
else ifeq ($(PHASE),pseudocode)
	scripts/sandbox --docker dotnet sh -c 'cd platform && dotnet test --no-build tests/CapabilityExplorer.Store.Tests --filter FullyQualifiedName~Snapshot'
else ifeq ($(PHASE),architecture)
	scripts/sandbox --docker dotnet sh -c 'cd platform && dotnet test --no-build tests/CapabilityExplorer.Store.Tests --filter FullyQualifiedName~PostgresExplorer'
else ifeq ($(PHASE),refinement)
	$(MAKE) EXECUTION=sandbox issue28-foundation-check
else
	scripts/sandbox --offline node node scripts/issue28-gate.mjs completion
endif

issue28-foundation-check: test-issue28-contract
	$(MAKE) EXECUTION=sandbox test-contracts test-store test-explorer golden-check

# Intentionally fails while endpoint families in the contract remain unmigrated.
issue28-acceptance:
	scripts/sandbox --offline node node scripts/issue28-gate.mjs completion
