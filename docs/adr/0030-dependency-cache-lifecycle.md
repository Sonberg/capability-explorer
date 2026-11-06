# Govern dependency caches across framework and toolchain upgrades

* Status: proposed
* Date: 2026-11-03
* Decision owner: repository contributors
* Coordination: [issue #3](https://github.com/Sonberg/capability-explorer/issues/3)
* Related: [MADR-0003](0003-local-ci-before-push.md), [MADR-0028](0028-portable-decision-ledger.md), [MADR-0029](0029-frugal-ci-and-executable-parity.md)

## Context and problem statement

Framework upgrades can leave obsolete downloads, installed dependencies,
compiler outputs, SDKs and container layers behind. Invalidating a cache key
prevents inappropriate reuse; it does not necessarily remove old bytes.
An upgrade must therefore review both correctness and storage lifecycle.

Not every old package is unused. Another workspace, a transitive dependency,
an active branch, a rollback version or an offline workflow may still need it.
Shared machine caches are not owned by whichever project last wrote to them.
Uncertain ownership is not permission to delete.

## Decision drivers

Bound storage growth without destroying active work, leaking private package
metadata, weakening locked installs or turning every CI run into a cold build.
Make upgrade housekeeping explicit, measurable and portable across workspaces.

## Considered options

1. Clear all npm/NuGet/container caches on every upgrade: rejected; expensive
   and unsafe for shared environments.
2. Keep everything forever and change cache keys only: rejected; storage grows
   even when correctness invalidation works.
3. Inventory, classify, verify and retire scoped obsolete generations:
   selected for review, subject to ownership and reproducibility evidence.

## Decision outcome

Treat download caches, installed dependency trees, generated build outputs
and container images/build caches as separate resource classes with separate
validation and deletion rules. Cache identity must include the relevant
manifest/lock, toolchain/runtime, OS/architecture and installation policy.
Changing identity does not authorize removal of another identity's data.

Use project/workspace-owned download-cache directories where practical.
Do not silently migrate or purge a user's global `~/.npm`, NuGet cache or
OrbStack resources. Shared caches require explicit owner coordination; record
the inspected builder/context/path and exact selection before any cleanup.
Do not infer ownership or inactivity from a PID, filename, age or image tag.

For framework, package-manager, SDK or runtime upgrades:

1. Record the before/after locked dependency and toolchain identities, the
   affected cache/output locations, their owners and measured sizes.
2. Classify data as required by a known current/transitive dependency closure,
   retained for rollback/offline use, provably obsolete within the owned
   scope, or unknown/shared. Protect the first two; retain the last until its
   owner resolves it. A single project's lockfile cannot prove global disuse.
3. Perform the ecosystem's supported integrity checks. Recreate installed
   outputs under the new locked inputs rather than trusting their presence.
   Never hand-edit opaque content-addressed package-cache internals.
4. Propose exact obsolete paths/keys/record IDs, expected reclaimable space,
   ownership proof, active-use exclusions and refetch/rebuild consequences.
   Recheck eligibility immediately before deletion. Shared, destructive or
   uncertain selections require explicit approval; no broad purge fallback.
5. Apply approved scoped cleanup, preserve application volumes/source and
   current required data, then measure actual space reclaimed. Report partial
   cleanup and failures honestly; nominal reclaimable size is not a guarantee.
6. Verify locked restore/install and affected build/tests, then the ordinary
   full local/hosted gate when code or runtime behavior changes. Distinguish a
   valid warm cache, cache miss/refetch, corrupted-cache recovery/failure and
   offline unavailability. Never translate cleanup failure into a test pass.

Each cache owner must declare reviewed byte/age retention budgets and any
rollback/offline retention requirements before enabling automatic retirement.
Absent a budget or ownership proof, automation inventories and reports only.
Retention of obsolete owned generations must be bounded by that policy;
active leases, required dependencies and explicit retention override age-based
eligibility. Review budgets at dependency/framework upgrades and reported disk
pressure, not by adding an always-running paid maintenance daemon.

## npm-specific qualifications

[npm's cache contract](https://docs.npmjs.com/cli/v11/commands/npm-cache)
describes a content-addressed cache that verifies data on insertion/extraction.
`npm cache verify` verifies integrity and garbage-collects internally unneeded
data; it is **not** a workspace dependency-reachability audit. npm documents
that this command is unaware of workspaces and that its cache can grow as new
packages are installed. Old indexed versions may remain useful elsewhere.

Prefer supported verification and scoped lifecycle controls over
`npm cache clean --force`. Cache verification can change cache contents, so it
must respect the same ownership boundary. Do not promise that verification
removes every package absent from this repository's current lockfile.
`npm ci` still recreates the installed tree and enforces manifest/lock
consistency. Download-cache reuse is not installed-output validation.

## Governance, automation and validation

Upgrade PR evidence should include an explicit cache-lifecycle disposition:
no obsolete owned data found, exact cleanup performed with receipt, or retained
data with reason/owner. A reviewed retention policy is not proof of completed
cleanup, and a merged upgrade does not automatically accept this MADR.
Maintain this record's index, diagram and portable source receipts together.

Any future cleanup automation must test exact scope/ownership boundaries,
shared/transitive/rollback exclusions, active-use races, symlink/path escape,
interrupted deletion, refetch failure, and honest measured outcomes. It must
default to a read-only plan and require separate authorization to apply it.
This proposal does not claim that such automation or a project-wide cache
reachability checker is implemented.

## Recurring dependency hygiene

This section answers [issue #109](https://github.com/Sonberg/capability-explorer/issues/109).
It extends the upgrade lifecycle above to unused package declarations. It is
proposed together with this record. It authorizes no package removal, prune,
cache cleanup, new dependency, detector, daemon or automation. Resource
objectives, measurement and the optimization stop rule come from
[MADR-0038](0038-balanced-resource-use.md) and are not repeated here.

### Ecosystems and ownership

At each review, inventory the ecosystems in the checkout. Currently these are
npm with `package-lock.json` (for example the TypeScript extractor and the
Vite Explorer client), NuGet with central versions in
`platform/Directory.Packages.props` and per-project `packages.lock.json`, the
.NET SDK pin in `platform/global.json`, and container images. Classify each
entry as direct, development, optional, peer, transitive, centrally managed,
platform-specific or tool. The integration owner of each manifest and lockfile,
as [AGENTS.md](../../AGENTS.md) requires, owns its review.

### Cadence and triggers

Run a lightweight review once per calendar quarter, as a proposed default for
owner confirmation. Also review when a feature or module is removed, a
dependency is added or upgraded, a framework or toolchain is upgraded, or a
contributor reports install-time or disk pressure. A review with no candidates
records "no change" briefly. This cadence is separate from the backlog-ranking
review after every five merged PRs; it does not use or change that cadence.
Contributors start a review manually as an ordinary issue or PR. No scheduled
job or always-running service performs it.

### Evidence for a removal candidate

A detector report or an import count is a candidate list, not proof. Before
proposing removal, examine and record each relevant path:

* static and dynamic imports, `require`, reflection and assembly loading;
* plugins and framework discovery, such as Vite configuration plugins, MSBuild
  analyzers, source generators and test adapters;
* generated code and the inputs of its generator;
* package scripts, Make targets, CI workflows, sandbox commands and tools
  invoked through `npx` or `dotnet`;
* type-only packages and peer requirements of retained packages;
* optional and platform-specific packages for each supported OS and
  architecture;
* container, deployment, runtime, test and fixture paths.

If a path cannot be examined, the package stays. Record a justified exception
with its reason, owner and review trigger.

### Declarations and caches are separate

Removing a declaration and evicting a cache entry are different operations.
Removing a declaration never makes its cached artifact disposable; another
checkout, project, platform, rollback or offline build can still need it.
Evicting a cache never changes a declaration. Cache entries follow the
lifecycle above: exact owned scope, recorded selection and explicit approval
for shared, destructive or uncertain cleanup.

### Removal changes, validation and rollback

Keep each removal change small and reversible: one ecosystem or cohesive group,
without unrelated upgrades. Use the package manager so that manifest and
lockfile change consistently. For npm, run `npm uninstall <package>` in the
owning package directory, then `npm ci`. For NuGet, remove the
`PackageReference`; remove the central `PackageVersion` only when no project
references it; regenerate the affected lockfiles with an ordinary
`dotnet restore`, then verify with `dotnet restore --locked-mode`. Do not hand-edit transitive
lockfile entries or remove required tooling.

Validate the affected build and tests, and run the relevant real runtime paths,
such as the CLI command, Vite build or extractor that used the package. List
optional and platform-specific paths as tested or untested; untested is not
success. Manifest and lockfile changes then need the ordinary full local and
hosted gate. Measure install, build or disk effects only when material,
following MADR-0038.

If validation fails, do not remove the package. If a missed path breaks after
merge, revert the manifest and lockfile change together first, then record an
exception or a corrected candidate. Each review reports candidates, examined
evidence, exceptions, approved removals, validation commands and results,
untested paths and the cache disposition. A reviewer of the affected area
approves each removal; cache cleanup keeps the separate approval above.

## Consequences

Upgrades include storage hygiene rather than leaving unbounded historical
artifacts indefinitely. Conservative ownership checks may retain some data
until another workspace's owner responds. Removing a valid but obsolete cache
can slow the next rebuild and cannot guarantee offline availability; those
tradeoffs must appear in the cleanup receipt rather than being hidden.
