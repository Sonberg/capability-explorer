# Analysis Publication Across Filesystems

Date: 2026-11-04. Scope: the interrupted local publication encountered while
refreshing source evidence for the integration audit. PostgreSQL publication
was not requested by these runs.

## Root Cause And Fix

The case-sensitive journal volume and canonical artifacts have different device
IDs. The previous implementation attempted to rename the old canonical output
into a `previous-*` directory under journal staging, then move journal staging
to canonical storage. The first cross-device rename failed before replacement.

Publication now copies only the immutable manifest-bound files into
`.sandbox-cache/artifact-publication/<run>/<attempt>/<repo>/<sha>/prepared`,
rechecks those bytes and original staging, then records its publication intent.
Existing canonical output is renamed to the same artifact-side cache's
`previous` directory and the prepared directory is renamed into canonical
storage. Original journal staging and previous canonical evidence are retained.
The publication cache remains outside the recursive canonical artifact scanner.
The publication cache and canonical artifacts must share a filesystem; the
journal may be on a different local filesystem. No arbitrary mount profile is
qualified by this change.

## Executable Qualification

The existing fixture was reused by a Docker-free `LocalPublicationTests` class
in [AnalysisExecutionTests](../../platform/tests/CapabilityExplorer.Store.Tests/AnalysisExecutionTests.cs).

Scenario: given existing artifacts, when publishing, then original staging is
preserved, canonical bytes match the bound artifact, old output remains in the
artifact-side rollback location, and publication effects are confirmed.

```sh
cd platform
dotnet test --no-restore tests/CapabilityExplorer.Store.Tests/CapabilityExplorer.Store.Tests.csproj --filter FullyQualifiedName~LocalPublicationTests --nologo
```

Observed red: `Publication must retain the original journal staging directory.`
The same scenario passed after the publication change. It also passed with an
isolated fixture root whose `.sandbox-cache/requests` used the existing disposable
case-sensitive APFS test image. Actual device IDs were 16777244 for the test
journal and 16777230 for artifact storage. The image was detached afterward;
the actual workspace journal mount was not changed. The fixture and outputs are
retained under `.sandbox-cache/integration-audit-evidence/publication-volume`.

## Exact Interrupted Run Recovery

The owner explicitly approved auditing and reconciling the new ASQ publication
run `6e1ea7f48cf14645b0979d57e47473c0`, attempt
`84c6f90f548e4c7a89a98f61f2bd5643`, before retrying. Under exclusive worker and
metadata locks, the audit verified all 73 staged artifact hashes against the
resolved manifest, absence of exact-label containers and host owners, and the
single pending first local destination intent. All 78 canonical files and all
117 canonical inventory entries matched the preserved pre-retry inventory,
including modes. No rollback directory or PostgreSQL intent existed.

The complete unknown run was archived intact under
`.sandbox-cache/requests/reconciled/6e1ea7f48cf14645b0979d57e47473c0/evidence`.
Verification covered 20,712 entries and 1,158,428,771 bytes, modes and link
targets. Original state and pending effects remain unchanged. The archive
contains the operator decision, inventories and post-move verification. No
canonical repair, success reconstruction or database write was performed.

Fresh weekly-sales, demand-pricing and Kraken managed runs subsequently completed
and confirmed local publication with this implementation. Full Docker-only CI
and database publication qualification were not run here.

## Unsupported Squid Repository

The later Squid run `4a1051e0188240359fbadbe9613b6e3d`, attempt
`4ebccdd22ac94d898d5ff57dd09db199`, produced eight valid native artifacts but
failed the existing all-repositories-must-have-an-artifact publication guard.
`apd-squid-quantify-cicd@d32f9ca1ecae0562a3bc34c4a880e509c85827c4` contains
infrastructure/configuration and migration tooling rather than a supported native
unit. The owner chose **Exclude CI/CD from native analysis**, not weakening the
publication guard or inventing a native artifact.

After explicit owner approval, an exact-run audit verified the immutable
manifest and all eight staged artifacts, zero publication effects, no publication
preparation directory and no remaining owners. The entire unknown run was
archived unchanged under its own `requests/reconciled/<run>/evidence`, with
22,153 entries and 577,751,274 bytes verified, plus modes/link targets and an
unchanged canonical inventory. The exclusion and exact revision remain recorded
in its decision receipt. Squid's local capability definition now contains its
three supported repositories. The excluded repository remains an explicit audit
limitation, not an analyzed or validated native product. The remaining serial
refresh was restarted with APT, both shared repositories, ASQ and Squid.