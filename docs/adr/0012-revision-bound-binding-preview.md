# Revision-bound binding preview and explicit export

* Status: applied in this workstream; proposed for owner review
* Date: 2026-11-01
* Related: MADR-0002/0008/0010/0011; issues #4/#6

## Context and decision drivers

The existing service-map binder accepts human mappings but the Explorer lacks a
review flow. Matching names, routes and message labels do not prove a deployed
connection. Reviews must be invalidated when artifacts, selected revisions or
existing bindings change, including changed artifact bytes under the same revision.

## Considered options

1. Infer and persist mappings automatically from matching names.
2. Preview explicit choices and export a source-bound proposal for separate adoption.
3. Continue editing binding JSON without a review interface.

## Decision

Choose option 2 and reuse Binder and BindingFile. The new catalog groups actual
unbound edge hints and exposes repository, full revision, unit, artifact hash and
evidence IDs. A fingerprint includes selected artifact identities/content and the
existing binding-file hash. The server recalculates it for preview and export;
stale requests fail with 409. Invalid/ambiguous revision selections and load errors
fail rather than silently broadening scope.

Users choose destinations and explicitly review entries. Endpoint declarations
can establish target presence but do not discover or prove the human connection.
Named external resources remain explicitly unverified human assertions. Unknown
HTTP destinations and conflicts block export. Suggestions contain declared HTTP
service references only, not invented database instances derived from table names.

The exported version-1 binding document preserves unrelated existing mappings and
includes provenance metadata. Existing BindingFile reads it; future adoption must
separately verify that provenance against current inputs. Export does not write
files on the server, mutate PostgreSQL or automatically adopt anything. Browser
editing/reloading invalidates prior review/export state. Revision selection clears
the draft; async responses from earlier selections cannot replace current results.

API handlers use the API-wide local guard and require same-origin JSON plus a
dedicated header for preview/export. This is browser request protection, not user
authentication. Input count, destination length and note length are bounded;
downloads use JSON and contain no generated executable configuration.

## Consequences and verification

Adds read-only catalog/preview/export endpoints and a navigation item. No schema
rewrite, database migration, model call or automatic service equivalence. Additive
functionality is minor-release material under MADR-0007; no release is assigned.

`make test-binding-preview` covers exact-content staleness, revision scope, target
validation, conflicts, review requirements, export-reader compatibility and UI
state invalidation. `make test-binding-preview-browser` exercises the actual served
UI/download. These targets are acceptance requirements, not an assertion they
passed on every future source state. Full CI and audit remain separate gates.
