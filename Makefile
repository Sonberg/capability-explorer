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
