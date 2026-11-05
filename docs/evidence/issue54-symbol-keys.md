# Issue 54: Bounded C# Symbol Identities

Date: 2026-11-04. Tracking: [GitHub issue 54](https://github.com/Sonberg/capability-explorer/issues/54).

## Cause And Contract

`SymbolKeys.Of` returned the complete scoped Roslyn documentation-comment ID
without checking the ce.v1 limit. The preserved real Web API Domain artifact
contains 244 overlength records, including a 2,332-character method key for
a wide domain method and corresponding edge references. Exact private source
identities are retained in the local qualification copy under
`.sandbox-cache/integration-audit-evidence/issue54-private-source-qualification.md`.
This is a wide method signature, not evidence of a `ToDisplayString` fallback.

Keys of at most 2,048 characters are unchanged. Longer keys retain the C# scheme,
repository/external scope and assembly, replacing the final segment with
`sha256:` and the full lowercase SHA-256 of the UTF-8 original scoped key.
The repository-wide key registry rejects any observed digest collision rather
than merging identities. No truncation or schema-limit increase is used.
The schema grammar is documented in [the contract guide](../../schema/README.md).
Compacted member grouping and source lookup use explicit `DECLARES` edges.

## Executable Scenarios

| Given / When / Then | Existing runner selector | Result |
| --- | --- | --- |
| Given a long constructor signature, when extracted, then keys are bounded, references agree, output validates and repeated extraction is deterministic | `Given_a_long_constructor_signature_when_extracted_then_keys_are_bounded_consistent_and_deterministic` | Red: 2,878-character key; same scenario green after fix |
| Given long constructor overloads, when extracted, then distinct full-digest identities retain distinct call targets | `Given_long_overloads_when_extracted_then_each_identity_and_call_remains_distinct` | Passed |
| Given a compacted member with explicit declaration ownership, when grouped, then its declaring component and source remain available | `Given_a_compacted_member_when_grouped_then_its_declaring_type_and_source_are_preserved` | Passed with corrected fixture; not claimed as unchanged red/green evidence |

Commands run from `platform`:

```sh
dotnet test --no-restore tests/CapabilityExplorer.Extractors.Tests/CapabilityExplorer.Extractors.Tests.csproj --filter FullyQualifiedName~Given_a_long_constructor_signature_when_extracted_then_keys_are_bounded_consistent_and_deterministic --nologo
dotnet test --no-restore tests/CapabilityExplorer.Extractors.Tests/CapabilityExplorer.Extractors.Tests.csproj --filter FullyQualifiedName~ConstructorTests --nologo
dotnet test --no-restore tests/CapabilityExplorer.Extractors.Tests/CapabilityExplorer.Extractors.Tests.csproj --nologo
dotnet test --no-restore tests/CapabilityExplorer.Explorer.Tests/CapabilityExplorer.Explorer.Tests.csproj --filter FullyQualifiedName~ArchitectureApiTests --nologo
```

The corrected primary regression failed with exit 1 on the intended length
assertion before production changes, then passed with exit 0. Constructor
regressions passed 9/9; the complete extractor suite passed 215/215; architecture
regressions passed 4/4. The editor test runner discovered no tests, so these
results come from the existing VSTest/xUnit runner. An earlier compilation error
using `ValidationReport.Errors` was corrected to `Problems` and is not red evidence.
The consumer fixture initially lacked `DECLARES`, then needed its terminal
`complete` record kept last. These fixture corrections are disclosed rather than
presenting its initial failures as an unchanged behavior-driven red/green pair.

Immutable local snapshots under `.sandbox-cache/integration-audit-evidence`:

| Snapshot | SHA-256 |
| --- | --- |
| `symbol-keys-red-source-corrected.tgz` | `c338615f36d885bb20a156d8991be11cedb8c4a03bd3c6109c962acbd2a02453` |
| `symbol-keys-green-source.tgz` | `0c211e5f8c2fb4fe90fdd5402e98397cdb39dc4cb3bc80595db12337e6cf1059` |

Red production file SHA-256:
`674ca51c18d6457221cca6057f99461107dba9e0e5fd11e9b55ab082847b8cd2`.
Green production file SHA-256:
`effa9559a6d8a741c0edb3bb6b077c0bc6949824bac33ea381c1419cc143a6a5`.
The red snapshot's test file SHA-256 is
`03a0f568ef22c87baaa57d9bfa6a26e93af94b556ba669c53e6227b583414844`;
the green test file adds the overload case without changing the primary scenario.

## Isolated Real-Source Qualification

The exact failing private repository and source revision are retained in the
local qualification copy, not reproduced in this versioned note.
Both isolated invocations used `scripts/sandbox --offline dotnet dotnet
platform/src/CapabilityExplorer.Extractors.CSharp/bin/Debug/net10.0/ce-cs.dll
extract`, the frozen Podman runtime snapshot, explicit read-only source mounts,
the exact repository/commit arguments and separate outputs. The package-enabled
invocation also used the failed attempt's preserved package directory and
repository-specific lock. Neither invocation published anything.

Outputs `asq-packages` and `asq-no-packages` under the evidence directory each
contain seven schema-valid artifacts; both commands returned exit 0. Domain
coverage remains partial: 43 diagnostics with packages, 341 without packages.
These are schema-valid extraction results, not full semantic coverage claims.
The isolated checks preceded the final repository-wide collision-registry
sharing change; the final extractor suite covers that change, and the managed
retry below uses the final runtime. No independent resolution defect remained
in the isolated validated outputs.

## Reconciliation And Retry

The owner requested "fix error and try again". Under exclusive worker and
metadata locks, the exact old run/attempt was verified pre-publication with
no resolved manifest or effects, no exact-label containers, no host file owners
and no host process references. The full run was moved intact to
`.sandbox-cache/requests/reconciled/ad053e3a5a7646b68881a5438f70f80f/evidence`.
The original journal remains `unknown`; it was not rewritten as stopped or done.
Inventory and post-move verification cover 20,540 entries and 998,767,221 bytes,
including modes and link targets. Canonical artifact inventory was unchanged.
The archive contains the operator decision, inventories and verification receipt.

The exact private invocation is retained locally. Its parameterized form, from
the project root, is:

```sh
CE_CONTAINER_ENGINE=podman CE_DATABASE= PACKAGES=1 GITHUB_AUTH=git BINDINGS=bindings.json scripts/analyze <capability-id> capabilities.json
```

The first managed retry passed extraction and encountered a separate
cross-filesystem publication failure. That failure was independently qualified,
fixed and owner-reconciled as recorded in
[publication evidence](analysis-publication-storage.md). A subsequent managed
refresh completed and confirmed local artifact publication with packages enabled.
The integration audit then discovered a separate source-citation bug; its
corrective managed refresh also completed and is tracked in local evidence. No authorization to retire a
future unknown run is inferred. Full Docker-only CI was not executed.