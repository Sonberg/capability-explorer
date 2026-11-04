# Partial profile: unsupported goals must not silently execute Docker.
.DEFAULT_GOAL := help
CE_CONTAINER_ENGINE ?= auto
ifeq ($(CE_CONTAINER_ENGINE),auto)
CE_SELECTED_CONTAINER_ENGINE := $(shell if command -v docker >/dev/null 2>&1; then printf docker; elif command -v podman >/dev/null 2>&1; then printf podman; else printf none; fi)
ifeq ($(CE_SELECTED_CONTAINER_ENGINE),none)
ifneq ($(filter-out help container-engine-doctor,$(MAKECMDGOALS)),)
$(error automatic engine selection found neither Docker nor Podman in PATH)
endif
endif
else ifeq ($(CE_CONTAINER_ENGINE),docker)
CE_SELECTED_CONTAINER_ENGINE := docker
else ifeq ($(CE_CONTAINER_ENGINE),podman)
CE_SELECTED_CONTAINER_ENGINE := podman
else
$(error CE_CONTAINER_ENGINE must be auto, docker or podman)
endif
export CE_CONTAINER_ENGINE
ifeq ($(CE_SELECTED_CONTAINER_ENGINE),podman)
CE_CORE_GOALS := help container-engine-doctor test-container-engine-core security-check
ifeq ($(EXECUTION),native)
CE_CORE_GOALS += dev dev-data explore postgres ollama dev-down analyze analyze-all analyze-worker
endif
ifneq ($(filter-out $(CE_CORE_GOALS),$(MAKECMDGOALS)),)
$(error Requested Make goal is not qualified for the selected Podman profile; no Docker fallback is permitted)
endif
endif

.PHONY: container-engine-doctor test-container-engine-core
container-engine-doctor:
	scripts/container-engine-doctor

# Unit fixtures need no engine. Live engine proof is recorded separately.
ifeq ($(EXECUTION),sandbox)
test-container-engine-core: likec4-deps
	$(OFFLINE) node node spikes/likec4/node_modules/typescript/bin/tsc --noEmit --strict --target ES2023 --module preserve --moduleResolution bundler --types node --typeRoots spikes/likec4/node_modules/@types scripts/lib/analysis-runtime.ts tests/analysis-runtime-shell.test.ts tests/container-engine-core.test.mts tests/native-analysis-runtime.live.ts
	$(OFFLINE) node node --experimental-strip-types --test tests/container-engine-core.test.mts tests/analysis-runtime-shell.test.ts
else
test-container-engine-core: restore-likec4-native
	node spikes/likec4/node_modules/typescript/bin/tsc --noEmit --strict --target ES2023 --module preserve --moduleResolution bundler --types node --typeRoots spikes/likec4/node_modules/@types scripts/lib/analysis-runtime.ts tests/analysis-runtime-shell.test.ts tests/container-engine-core.test.mts tests/native-analysis-runtime.live.ts
	node --experimental-strip-types --test tests/container-engine-core.test.mts tests/analysis-runtime-shell.test.ts
endif
