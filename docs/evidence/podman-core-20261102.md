# Operator-run core runtime evidence, 2026-11-02

Code candidate: `e9ad854c67a9b4856792fb665d2c25f95a8da5d6`.
Host: macOS arm64. This is reported diagnostic evidence, not independent
attestation, release authority or whole-project qualification.

## Actual sandbox probes

| Observed backend | Client/server | Server platform | Rootless | Context SHA-256 |
|---|---|---|---|---|
| Docker | 29.8.2 / 29.8.2 | linux/arm64 | Not measured | `0ec8bca9fcf814a9732b648510fc9ca787f3976e120541ba579eb48f98bccb09` |
| Podman machine | 6.1.4 / 6.1.4 | linux/arm64 | true | `da7b8aa120c845edcb53e7ed875a5963406a283a419f32021c9b1e67bd11bb97` |

Both `node scripts/test-container-engine-live.mjs` invocations returned
`passed:true`. The Podman invocation explicitly selected its owned machine.
Both proved UID 502, EROFS source/root writes, zero effective capabilities,
no-new-privileges, no engine socket, unchanged source bytes and caller-owned
cache writes. The owned local canary was reachable online and unreachable
offline, with its host hit counter unchanged during the negative test.

The common relevant-source manifest hash is
`a66fd457b5ac76e1876976e241e2e82dd5316c7ae3252b13ca2720527ebbe667`.
It covers the adapter, doctor, sandbox, pinned images and live-probe script:

| Input | SHA-256 |
|---|---|
| `scripts/lib/container-engine.sh` | `c2a744588d901b5f8d20d706f651b9577974b2f5af335f679ea65704e508ba55` |
| `scripts/container-engine-doctor` | `c1de6d15c8dc251f2d554805dcc1eae9fcac91711b7a0128342119d81c74186b` |
| `scripts/sandbox` | `b0c033bd5a20fcec7c0a68d495c36d22e71d77e2d86ab479419f4216a894135a` |
| `config/runtime/images.env` | `1128bce98fa3305188df4d75809c643c75ae72a85b518dc8bbcdd4b3cb4af4cd` |
| `scripts/test-container-engine-live.mjs` | `91d3216c8c2f88d7a0dcfdc8f504847f60d816424cb296c6f4cacf1ec0b8a9e4` |

## Limited extractor parity

Offline `scripts/extract-fixtures.sh ts` under each engine produced the same
three JSONL files and exactly equal bytes, including ce.v1 headers:

| Fixture | SHA-256 |
|---|---|
| pricing-node/v1-base | `b919c3e17c24e9f11d1d7f88d0194e8106479192c4086977a6022f792714e1ea` |
| pricing-node/v2-move | `af4431143253550dd2618a4b1ae2bc4d94eb1d5973b0f0a80861ccd4877bb5e1` |
| web-ts/v1-base | `da81163be32b4dfedc039854872bc2f25671f7463fabcd1f82b01ac2fea3844c` |

## Lockfile scanner controls

Pinned Trivy 0.76.1 image digest:
`790fd66d1697395d8693d1acaa1ea702406a20f109b6fc7bac8db623eca6c06c`.
Database updated `2026-11-02T07:17:05.526948404Z`, database SHA-256
`9cf3518dfc47bd04da93073cbc893e428f8608fe57b2b04ad4893e82cf25c58d`.
Actual Podman project scan exited 0 with no detected dependency vulnerabilities.
An isolated synthetic lodash 4.17.20 lockfile exited 1 with findings.
All severities/dev dependencies remained enabled. A public corporate CA was
explicitly supplied read-only; certificate/hostname verification stayed enabled.

## Not established

No Linux live, nested Podman act/.NET Testcontainers, full dev/analysis/audit,
other-language parity or large-corpus proof. Context was observed before/after
each diagnostic, not atomically locked across subprocesses. Scanner success is
not complete source/image security assurance. Raw private logs and receipts are
preserved separately; the dedicated validation machine was stopped afterward.
