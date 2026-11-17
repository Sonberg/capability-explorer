# Trusted native development is preserved; CI and explicit agent validation use hardened sandbox recipes.
EXECUTION ?= $(if $(CI),sandbox,native)
ifeq ($(EXECUTION),sandbox)
include config/build/sandbox.mk
else ifeq ($(EXECUTION),native)
include config/build/native.mk
else
$(error EXECUTION must be native or sandbox)
endif
include config/build/issue28.mk
include config/build/platform.mk

# Independent dist is linked into output/publish wwwroot by Explorer's project.
# Never stage it in source wwwroot: the legacy Vite build erases that directory.
PROCESS_LANDSCAPE_UI := platform/src/CapabilityExplorer.Explorer/client-next
.PHONY: check-process-landscape-ui restore-process-landscape-ui ui-process-landscape frontend-process-landscape
check-process-landscape-ui:
	@test -f $(PROCESS_LANDSCAPE_UI)/package.json -a -f $(PROCESS_LANDSCAPE_UI)/package-lock.json || { echo "Missing client-next sources/lockfile; integrate the process landscape UI before building." >&2; exit 1; }

ifeq ($(EXECUTION),sandbox)
ui: ui-process-landscape
restore-process-landscape-ui: check-process-landscape-ui
	$(NODE) sh -c 'cd $(PROCESS_LANDSCAPE_UI) && npm ci --ignore-scripts'
ui-process-landscape: restore-process-landscape-ui
	$(OFFLINE) node sh -c 'cd $(PROCESS_LANDSCAPE_UI) && npm run -s build && test -f dist/index.html'
frontend-process-landscape:
	@echo "Use EXECUTION=native for the process landscape Vite development server." >&2; exit 1
else
restore-process-landscape-ui: check-process-landscape-ui
	cd $(PROCESS_LANDSCAPE_UI) && $(NPM) ci --ignore-scripts
ui-process-landscape: restore-process-landscape-ui
	cd $(PROCESS_LANDSCAPE_UI) && $(NPM) run -s build
	@test -f $(PROCESS_LANDSCAPE_UI)/dist/index.html || { echo "client-next build did not produce dist/index.html." >&2; exit 1; }
frontend-process-landscape: restore-process-landscape-ui
	cd $(PROCESS_LANDSCAPE_UI) && $(NPM) run dev -- --host 127.0.0.1
endif
