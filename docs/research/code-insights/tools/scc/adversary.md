# scc inventory desk adversary

Agent: `/root/issue28_ruvector`. Independent source review, then one bounded
identity-reconciliation addendum. Root-transcribed summary; no install/trial.

The original registered `44b15f` source identifies itself as 4.2.0 beta. Stable
`c651b07a7d3aa6e97a476380eef0f478a53719a3` declares 4.1.0; a subsequent independent
source check resolved the proposal's source selection. No downloaded executable
has been verified, and the beta review supplies no stable-runtime qualification.

Stable source retains `.sccconfig`/environment configuration discovery. Explicit
`--config` can remain effective despite `--no-config`; argument-file expansion
also requires a controlled wrapper. Config-derived output flags are restricted;
this review does not allege arbitrary config code execution. Freeze exclusions,
remapping, effective argv, scrubbed environment and every file disposition.
Unknown extensions/default exclusions must not manufacture apparent completeness.

Stable `go.mod` requires Go 1.26.4; Dockerfile fixes amd64 with mutable base tags.
Native platform, artifact digest, dependency closure and preparation cost need
their own evidence. Do not benchmark emulation versus native without disclosure.

Require read-only sealed export, no source symlink following, offline extraction,
bounded output and fixed worker/resource limits. Negative cases include escaping
links, loops/FIFOs, oversized files/lines, unknown extensions and hostile config
sentinels. Zero exit is not containment or completeness proof.

Complexity is an estimate, unsuitable for direct cross-language comparison;
COCOMO/LOCOMO are not measured staffing/delivery costs. More output is not more
true architectural evidence. MIT source license does not qualify dependencies.

Suggestion: conditional bounded inventory trial only after artifact verification,
source-free preparation, adequate oracle and foundation merge. Otherwise defer;
no permission/budget expansion and no introduction approval.

Stable primary sources independently checked in the addendum:

- https://github.com/boyter/scc/blob/c651b07a7d3aa6e97a476380eef0f478a53719a3/processor/processor.go
- https://github.com/boyter/scc/releases/tag/v4.1.0
- https://github.com/boyter/scc/blob/c651b07a7d3aa6e97a476380eef0f478a53719a3/config.go
- https://github.com/boyter/scc/blob/c651b07a7d3aa6e97a476380eef0f478a53719a3/main.go
- https://github.com/boyter/scc/blob/c651b07a7d3aa6e97a476380eef0f478a53719a3/go.mod
- https://github.com/boyter/scc/blob/c651b07a7d3aa6e97a476380eef0f478a53719a3/Dockerfile
- https://github.com/boyter/scc/blob/c651b07a7d3aa6e97a476380eef0f478a53719a3/processor/file.go
- https://github.com/boyter/scc/blob/c651b07a7d3aa6e97a476380eef0f478a53719a3/LICENSE
