# Draft contract planning checks are cheap and reuse the existing Node sandbox.
.PHONY: platform-plan-check platform-contract-check test-platform-plan
ifeq ($(EXECUTION),sandbox)
ci: test-platform-plan
endif

.PHONY: test-frontend-contracts
ifeq ($(EXECUTION),sandbox)
ci: test-frontend-contracts
endif

test-frontend-contracts:
	scripts/sandbox --offline node node scripts/research/check-frontend-contracts.mjs all

.PHONY: test-domain-object-contracts
ifeq ($(EXECUTION),sandbox)
ci: test-domain-object-contracts
endif

test-domain-object-contracts:
	scripts/sandbox --offline node node scripts/research/check-domain-object-contracts.mjs all

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
platform-contract-check:
ifeq ($(EXECUTION),sandbox)
	scripts/sandbox dotnet sh -c 'cd platform && dotnet restore --locked-mode tests/CapabilityExplorer.Contracts.Tests'
	scripts/sandbox --offline dotnet sh -c 'cd platform && dotnet test --no-restore tests/CapabilityExplorer.Contracts.Tests --filter FullyQualifiedName~PlatformExchangeContractTests'
else
	cd platform && dotnet restore --locked-mode tests/CapabilityExplorer.Contracts.Tests
	cd platform && dotnet test --no-restore tests/CapabilityExplorer.Contracts.Tests --filter FullyQualifiedName~PlatformExchangeContractTests
endif
