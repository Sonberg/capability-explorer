# Repository discovery policy — pseudocode

Status: proposed policy; a reference description for bounded research only.
This is not a scanner, language registry implementation or proof about all layouts.
It consumes a frozen source snapshot and a pinned offline registry/profile.

## Adapter and compatibility procedure

The following describes a future trust boundary, not a production runner. `ce.v1`
remains the current closed artifact vocabulary; the proposed adapter exchange is
an internal, versioned draft and cannot make an old validator accept a new
language. Producer qualification, semantic accuracy and rollout approval remain
separate gates.

```
adapt(request, producer, consumerProfile, frozenSnapshot):
  A01 bind repository revision, unitKey, source/config digests and selected closed profile
  A02 verify producer/toolchain name, version and executable digest; negotiate only declared features
  A03 require an already provisioned trusted toolchain in offline credential-free isolation; never run repository scripts
  A04 reject unknown profile/capability or unavailable toolchain as unsupported, not empty success
  A05 validate every source span against frozen bytes; preserve source coordinate encoding and provenance
  A06 preserve resolved/static-target/candidate/unresolved state and evidence; never promote weaker resolution
  A07 compute file/fact coverage, omitted paths, diagnostics and complete/partial/failed status; keep telemetry separate
  A08 canonicalize ordered facts/evidence without operational timestamps; emit only the new draft envelope
  A09 translate only facts representable by the selected closed consumer profile; malformed or unknown old-consumer inputs fail closed
  A10 qualify against an independently authored gold inventory/link/span set; report measured precision, recall and false/missing links
  A11 compare producer evidence and recorded demand before a bounded, reversible pilot; otherwise stop without language selection
```

Producer profiles are sealed and selected by a versioned declaration; they are
not discovered by running repository package scripts. `A10` is a future
measurement procedure: absent a representative gold corpus, it reports
`not-measured`, not a score. `A11` requires observed demand, named ownership,
offline reproducibility, rollback, safety review and an explicit stop condition.
No registry update, schema expansion or downstream consumer release is implied.

## Stable unit identity

For each accepted unit, compute:

```
unitKey = "unit:v1/" + posixPath(ownerDirectory) + "#" + declaredUnitId
```

`ownerDirectory` is normalized relative to the immutable repository root; the
repository root itself is `"."`. `declaredUnitId` is non-empty, normalized and
unique within that owner. The key excludes host paths, repository display names,
commit time, marker enumeration order and tool runtime. The source revision and
marker digest are separate provenance fields; a changed marker can change the
unit contract without changing the owner path key.

## Precedence and ownership

1. Snapshot containment, path normalization, symlink rejection, nested-repository
   boundaries and hard byte/file/depth limits are non-overridable safety rules.
2. A pinned explicit repository declaration selects approved marker profiles,
   units and allowed extensions. Unknown language/capability values are errors.
3. Explicit excludes take precedence over includes for a path. A user include can
   override an advisory test/vendor/generated category only when the declaration
   says so; it cannot reopen a hard safety boundary.
4. A nested declared unit owns paths below its root unless that unit explicitly
   declares the same file as shared input. Same-owner overlapping globs are
   deduplicated by normalized path. Equal-depth cross-unit overlap is a conflict,
   not an implicit winner.
5. Project/workspace references are links, not implicit authority to scan. A
   missing target is retained as `missing-dependency` and makes the result
   `partial`; it is not silently discarded.
6. Without an explicit declaration, recognized markers may propose units only
   through the pinned profile registry. Multiple conflicting markers or a
   marker-free repository with plausible source files produce `ambiguous`, not
   successful empty discovery.

## Deterministic bounded procedure

```
discover(snapshot, registry, declaration, budgets):
  P01 validate snapshot identity and authorized root boundary
  P02 validate pinned registry, declaration, profile and budget ceilings
  P03 collect directory entries without following symlinks; stop at hard/nested-repository boundaries
  P04 visit entries in normalized POSIX byte order; reject depth/file/candidate/byte budget exhaustion
  P05 read only bounded recognized markers; never evaluate imports, macros or scripts
  P06 parse and normalize owner, unit identity, language, capabilities and references
  P07 reject unknowns, malformed markers, invalid paths and conflicting owner identities
  P08 apply exclusions/includes, nearest ownership and same-owner deduplication
  P09 reject cross-owner overlap; retain missing project references and explicit coverage loss
  P10 sort all output; return canonical inventory separately from operational telemetry
  P11 return explicit complete/partial/ambiguous/unsupported/failed status; never serialize timestamps into inventory bytes
```

## Budgets, categories and failures

Budgets are explicit positive ceilings for depth, candidate markers, files per
unit, total files and aggregate marker/source bytes. The exact values are profile
inputs to be benchmarked before any pilot; this research sets no invented
production defaults. Exceeding any hard ceiling stops the run with
`limit-exceeded`; it does not truncate to a successful partial inventory.

Nested repositories are hard boundaries. A separately authorized snapshot may
analyze one as its own root; an outer snapshot never follows through its boundary.
Default generated/vendor/test exclusions are profile policy, not universal facts:
explicit declarations can include tests or advisory-excluded paths but must report
the decision and resulting coverage. A declaration cannot claim generated-source
classification the marker did not provide. No source path is resolved through a
symlink.

Statuses are distinct: `complete` means no required source/reference was omitted;
`partial` means a declared optional/linked input is missing and the omitted path
is explicit; `ambiguous` means a human declaration is required; `unsupported`
means the registry lacks a required capability; `failed` means malformed input,
conflict, safety violation or hard budget failure. Only `complete` and explicitly
accepted `partial` inventories contain ordered units; consumers must not treat
either as equivalent quality.

## Complexity

Let `D` be visited directories, `F` candidate files, `M` recognized markers,
`R` project references, and `B` bytes read from bounded markers/config inputs.
Traversal and normalization are `O(D + F + B)`. Sorting paths/units is
`O((F + M + R) log(F + M + R))`; reference linking is `O(R)` with a hash index.
Memory is `O(F + M + R)` and input reads are bounded by the aggregate byte budget.
The complexity assumes a snapshot filesystem with bounded directory-entry reads;
it does not bound a compiler's semantic analysis and makes no performance claim
for a future frontend.

## Failure examples

| Input | Result |
|---|---|
| `../outside`, absolute include, or symlinked candidate | `failed/path-escape`; no inventory |
| Unknown registry language or unadvertised feature | `unsupported`; no success |
| Two same-depth owners claim one file | `failed/ownership-conflict` |
| Marker-free source tree or incompatible markers | `ambiguous`; require explicit declaration |
| File, depth, candidate or byte ceiling exceeded | `failed/limit-exceeded`; never silently truncate |
| Referenced project absent from frozen snapshot | `partial/missing-dependency` with link and coverage deficit |
| Two globs within one unit include the same file | One ordered path, with stable deduplication |
| Test/generated/vendor include/exclude disagreement | Apply explicit precedence; preserve decision as diagnostic |
| Optional language server or compiler profile unavailable offline | `unsupported` or `failed`; never fetch/install or downgrade to syntax-only success |

## Conformance boundary

Fixture inventories are authored independently from the reference algorithm.
The oracle checks policy cases and canonical ordering only. It does not call an
extractor, parse real compiler projects, prove reference resolution, establish
precision/recall, validate source spans or certify every repository layout.
