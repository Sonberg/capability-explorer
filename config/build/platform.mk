# Draft contract planning checks are cheap and reuse the existing Node sandbox.
.PHONY: platform-plan-check platform-contract-check test-platform-plan
ifeq ($(EXECUTION),sandbox)
ci: test-platform-plan
endif

platform-plan-check:
ifeq ($(PHASE),)
	scripts/sandbox --offline node node scripts/check-platform-contract-plan.mjs foundation
else ifeq ($(PHASE),foundation)
	scripts/sandbox --offline node node scripts/check-platform-contract-plan.mjs foundation
else ifeq ($(PHASE),specification)
	scripts/sandbox --offline node node scripts/check-platform-contract-plan.mjs specification
else ifeq ($(PHASE),pseudocode)
	scripts/sandbox --offline node node scripts/check-platform-contract-plan.mjs pseudocode
else ifeq ($(PHASE),architecture)
	scripts/sandbox --offline node node scripts/check-platform-contract-plan.mjs architecture
else
	$(error Unsupported platform plan phase; global runtime completion is not implemented)
endif

test-platform-plan:
	scripts/sandbox --offline node node --test tests/platform-plan.test.mjs
	scripts/sandbox --offline node node scripts/check-platform-contract-plan.mjs foundation

# Shape conformance uses the existing Contracts.Tests dependency graph. This is
# not a central provider/consumer integration test or an OpenAPI certification.
platform-contract-check: build
ifeq ($(EXECUTION),sandbox)
	scripts/sandbox --offline dotnet dotnet test --no-build platform/tests/CapabilityExplorer.Contracts.Tests --filter FullyQualifiedName~PlatformExchangeContractTests
else
	dotnet test --no-build platform/tests/CapabilityExplorer.Contracts.Tests --filter FullyQualifiedName~PlatformExchangeContractTests
endif
