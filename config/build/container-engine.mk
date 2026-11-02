# Partial profile: unsupported goals must not silently execute Docker.
.DEFAULT_GOAL := help
CE_CONTAINER_ENGINE ?= docker
ifneq ($(CE_CONTAINER_ENGINE),docker)
ifneq ($(CE_CONTAINER_ENGINE),podman)
$(error CE_CONTAINER_ENGINE must be docker or podman)
endif
endif
ifeq ($(CE_CONTAINER_ENGINE),podman)
CE_CORE_GOALS := help container-engine-doctor test-container-engine-core security-check
ifneq ($(filter-out $(CE_CORE_GOALS),$(MAKECMDGOALS)),)
$(error Requested Make goal is not qualified for the partial Podman core profile; no Docker fallback is permitted)
endif
endif

.PHONY: container-engine-doctor test-container-engine-core
container-engine-doctor:
	scripts/container-engine-doctor

# Unit fixtures need no engine. Live engine proof is recorded separately.
test-container-engine-core:
ifeq ($(EXECUTION),sandbox)
	$(OFFLINE) node node --test tests/container-engine-core.test.mjs
else
	node --test tests/container-engine-core.test.mjs
endif
