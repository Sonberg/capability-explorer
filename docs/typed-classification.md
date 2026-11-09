# Bounded local typed classification: issue #105

**Decision: defer remote replacement.** This implementation supplies a
dependency-free numeric classification pattern and a synthetic qualification
command in the existing .NET runtime. It is not a Laya/Jev adapter, a new service,
an approved business rule, or evidence of probability calibration.

## Inventory and ownership

Public sources inspected on 2026-11-09:

| Product/source | Pinned source | Observed contract and limitation |
|---|---|---|
| Laya Python 0.4.30 | [NandhaKishorM/laya at b8a0f099](https://github.com/NandhaKishorM/laya/blob/b8a0f0991d95a953411f2e16f91bddc2ffdaf79a/pyproject.toml) | Text/state plus `choice`, `score`, `noul` questions. Not a fixed numeric vector contract. |
| Laya HTTP | [HTTP contract at the same revision](https://github.com/NandhaKishorM/laya/blob/b8a0f0991d95a953411f2e16f91bddc2ffdaf79a/docs/http-api.md) | `/v1/systemone`; a strict Jev mode changes response fields. Upstream compatibility claims are not independently qualified here. |
| Laya Node 0.2.0 | [receptron/laya at 60d5ee6e](https://github.com/receptron/laya/blob/60d5ee6ef8e731646e8ca9ac0139d25fcf4fa295/package.json) | ONNX text decisions, not this numeric matrix pattern. Not installed. |
| Laya .NET | [JakubK/LayaDotNet at 1f7a2bd5](https://github.com/JakubK/LayaDotNet/blob/1f7a2bd51cfd15d0be4dd4a2494a68d20bd59045/README.md) | Text/questions with CPU/GPU models and downloads. No deployed version or local call site verified. |
| Official Jev Python SDK 0.7.5 | [typesafe-ai/typesafe-sdk-python at f36e6854](https://github.com/typesafe-ai/typesafe-sdk-python/blob/f36e68549ceca3191e5791e5f99ea1baa1a258b5/README.md) | `system_one(state, questions)` with `choice`, `score`, `noul` and explicit optional/null semantics; requires external credentials. No package installed, endpoint invoked or production contract qualified. |

The source inventory found no Laya/Jev call site in this repository's merged
source. The older unpublished synthetic prototype's reported commit was not
available in this repository object database; neither recovery nor integration
is inferred. Its original claim and paths remain protected. The new lane uses
`Interpretation/BoundedClassification/`, acknowledged on
[#105](https://github.com/Sonberg/capability-explorer/issues/105#issuecomment-6740347634).

The existing Interpretation provider remains optional and disabled by default.
The classifier does not replace embeddings, text interpretation, feature
extraction, native adapters, semantic matching, or reviewed business meaning.
It emits no native `ce.v1` evidence and makes no database or Explorer changes.
The selected owner-controlled experiment is the synthetic three-axis fixture;
there is no qualified product-replacement use case yet.

## Versioned local contract

`ClassificationArtifact<TLabel>` accepts a strongly typed enum taxonomy. Its
ordered class names must be distinct exact named enum values, not numeric aliases.
`ClassificationResult<TLabel>` distinguishes `Decision`, `Abstain`,
`ValidationFailure`, and `Unavailable`. Only `Decision` has a selected class.
Returned scores are in artifact class order and are **normalized scores**.
Logits are internal; calibrated probabilities are not supported or claimed.

Offline artifact JSON schema `ce.classification-artifact.v1` requires every
field shown in [the synthetic artifact](../fixtures/classification/synthetic-artifact.v1.json):
taxonomy, ordered features/units, `identity-f64-v1` preprocessing, inclusive
minimum/maximum domain values, ordered classes, row-major weights, biases,
minimum score and minimum margin. No training or inferred feature preparation
occurs. Application owners must predeclare meaningful features, units and domain.

Limits are 128 KiB/artifact, 1..128 features and 2..32 classes; input JSON
is at most 16 KiB with depth 8. Artifacts also have depth 8. Every property is
required and non-null; unknown, duplicate, malformed and unsupported members
fail explicitly. There is no truncation or hidden compatibility fallback.

Input JSON schema `ce.classification-input.v1` has exactly `schema`,
`artifactId`, and `features`. The in-process `Classify` overload accepts the
equivalent pre-parsed schema, artifact identity and flat double span. It checks
shape before scanning values, so oversized vectors cannot trigger scoring.
Missing/corrupt/mismatched artifacts never reach readiness.

`ArtifactId` is lowercase SHA-256 of the exact accepted UTF-8 artifact bytes.
The caller must supply the expected immutable hash before loading. This is an
integrity check, not approval or authenticity. Exact bytes are the canonical
exchange for this profile: reordering JSON properties or changing whitespace
changes identity. Readers do not silently recanonicalize or reinterpret artifacts.
The version, feature/class order, units, preprocessing, domain, all parameters
and threshold policy are therefore bound together. No calibration artifact exists.

## Numeric behavior and updates

The scorer uses bounded scalar `double` dot products, finite intermediate checks
and stable softmax shifted by the maximum logit. NaN, infinity and overflow
fail explicitly without a selected class. Underflow to zero in nonwinning
classes is permitted; invalid/zero denominators or nonfinite normalized output
are numerical failures, not arbitrary winners.

The highest normalized score wins, with the first artifact class breaking an
exact tie. Scores or margins strictly below their thresholds abstain; equality
passes. A tie at a zero margin threshold selects the first class. Inclusive
feature domains produce out-of-distribution abstention when exceeded; this is
a declared bounds policy, not a learned detector of all distribution shifts.

Supported execution is .NET 10 scalar binary64 CPU arithmetic. Fixtures use
absolute score tolerance `1e-12`. Runtime, architecture, assembly and artifact
identities are retained by the experiment. No GPU/ONNX provider, universal
bitwise cross-platform determinism, hard real-time bound or worst-case execution
time is claimed.

`ClassificationSlot<TLabel>` takes one immutable active snapshot per decision.
Load/validate before explicit `TryActivate`; expected-current identity provides
optimistic update fencing. Invalid/stale updates leave the previous active
artifact unchanged. Explicit rollback retains and restores the previous
verified artifact. The slot is not a policy authority: the caller must obtain
separate compatibility, quality and promotion authorization before activation.
There is no timer, downloader, remote fallback, auto-training or self-promotion.

## Frozen synthetic oracle and experiment

[The corpus](../fixtures/classification/synthetic-corpus.v1.json) was frozen
before candidate integration. It independently specifies three dominant axes,
low-score ambiguity, low-margin ambiguity, out-of-domain abstention, negative
features and inclusive domain boundaries. It is not a representative held-out
product corpus and cannot establish semantic accuracy or calibration.

The CLI runs actual input validation, an atomic artifact snapshot, scoring and
typed mapping. File reads, JSON parsing, feature preparation, queues, transport,
downstream integration, artifact load and warmup are excluded from warmed
decision timing and explicitly reported separately or unknown.

```sh
scripts/sandbox --offline dotnet dotnet build platform/src/CapabilityExplorer.Cli --no-restore
scripts/sandbox --offline dotnet dotnet platform/src/CapabilityExplorer.Cli/bin/Debug/net10.0/ce.dll \
  classification-benchmark \
  --artifact fixtures/classification/synthetic-artifact.v1.json \
  --sha256 a48a617509060866ee2da35e0eaadc51f9685640b9f9841139dc4f1079a6ab6b \
  --corpus fixtures/classification/synthetic-corpus.v1.json \
  --source <exact-clean-source-commit> --samples 20000 --concurrency 1 \
  --json .sandbox-cache/classification-single.json
```

Repeat with concurrency 4 and a new report path for the declared concurrent
workload. Samples are bounded to 1,000..100,000; concurrency is 1..4.
Warmup is 5,000 decisions. The load is closed-loop saturated workers, not a
rate-controlled arrival process. Timer scheduling and GC affect observed tails.
Raw ticks/outcomes/reasons, frequency, nearest-rank p50/p95/p99, throughput,
thread allocations, process CPU, GC collections and before/after working set
are retained. Before/after working set is not peak memory; peak is unknown.
The source revision is caller-declared, while loaded assembly hashes are
measured; the delivery owner must bind these to a verified clean build receipt.
Reports never overwrite an existing file.

The target is warmed p99 strictly below 1 ms with no invalid/unavailable
outputs, **only for the declared synthetic workload and measured host**.
An opt-in run failing the target returns failure and still retains measurements.
Normal tests cover correctness, not an unreliable mandatory CI latency target.

No remote/reference implementation was executed. Reference latency, hosting,
network cost, maintenance cost, representative accuracy, calibration and
enterprise savings remain unknown. No comparison multiplier is inferred from
the component benchmark. Existing #43/#63 end-to-end objectives remain separate.

## Acceptance remaining before adoption

Issue #105 remains open for an actual selected product contract/version and
owner, strict producer/consumer compatibility, a reviewed representative
reference corpus, semantic parity, per-class quality/abstention and any required
calibration, ambiguous/safety-sensitive errors, paired baseline/candidate
end-to-end and operating-cost measurements, and separately authorized adoption.
Remote removal is not applicable without a verified remote path.
Do not turn synthetic oracle agreement into those approvals.

Resource impact: no new package, model, service or background runtime.
Artifacts and result dimensions are bounded. Local heavy validation is serialized
and uses existing pinned sandbox images; preserve the live preview and disk
reserve. Retained raw receipts have a named delivery owner; no automatic cache,
source, worktree or recovery deletion is authorized.
