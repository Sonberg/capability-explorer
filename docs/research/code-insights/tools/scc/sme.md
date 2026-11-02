# scc inventory desk SME

Agent: `/root/issue28_queries`. Source inspection only; no install/trial.
Root-transcribed summary of the independently submitted brief; the judge must
inspect sources rather than treat this transcription as measured proof.

Baseline main `1e1122b62587eda7f3e2817c1619ba29ce292923`: `scripts/analyze`
orchestrates C#/TS/SQL semantic extraction. `scripts/mapping-replay-lib.mjs`
already enumerates relative paths/content hashes and rejects symlinks. File
enumeration alone is therefore insufficient marginal value.

Named gap: languages and physical source volumes outside implemented extractors,
including files not semantically analyzed. This does not prove capabilities,
integrations, parser coverage or business semantics.

Source proposal: stable v4.1.0, commit
`c651b07a7d3aa6e97a476380eef0f478a53719a3`, rather than the earlier beta pin.
The release API reports Linux arm64 archive size 6,294,379 bytes and digest
`6e0d2a1f8d3540ba7df185477dec40bb7340f1b214bfd303147de5cad2bd7b8b`.
These are upstream metadata, not downloaded-byte verification or authenticated
build provenance. The annotated tag was reported unsigned.

Proposed oracle: 24–32 tiny files including C#/TS/SQL, Python/Go, infrastructure,
shebang, ambiguous/uppercase extensions, CRLF/no final newline, comment delimiters
in strings, generated/binary/excluded/duplicate files and an escaping symlink.
Require exact manifest reconciliation, correct simple language/count cases,
explicit unknowns, and at least two predefined improvements beyond path/hash plus
extension classification (e.g. shebang language and comment-aware counts).
Repeat fresh runs under changed working directory and relocated mounts; retain
raw output and do not normalize away substantive differences.

Suggestion: bounded inventory-only trial after prerequisites; not introduction.
Use per-file JSON, no ambient config/argument files, complexity, COCOMO, MCP or
history mode. Keep physical duplicate paths. Existing implementation remains
fallback if useful coverage does not improve. cloc is an alternative, not oracle.
Unknown: executable integrity, dependency security/licenses, accuracy and cost.

Primary references inspected:

- https://api.github.com/repos/boyter/scc/releases/latest
- https://api.github.com/repos/boyter/scc/git/tags/f9ab68bf05ec0d82a6129738aa2fcc148313e472
- https://github.com/boyter/scc/blob/c651b07a7d3aa6e97a476380eef0f478a53719a3/config.go
- https://github.com/boyter/scc/blob/c651b07a7d3aa6e97a476380eef0f478a53719a3/main.go
- https://github.com/boyter/scc/blob/c651b07a7d3aa6e97a476380eef0f478a53719a3/processor/formatters_json.go
- https://github.com/boyter/scc/blob/c651b07a7d3aa6e97a476380eef0f478a53719a3/LICENSE

Failed lookup: pinned `flags.go` returned 404; definitions found in `config.go`.
