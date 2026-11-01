#!/usr/bin/env bash
# Runs the extractors over every locked fixture revision and writes artifacts to <out>/<repo>/<tag>/.
# The source tree comes from `git archive` of the exact commit: nothing is checked out or built.
#   scripts/extract-fixtures.sh <cs|ts|sql> <out>
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
lang="$1"; out="$2"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
# Materialize the inventory first: process-substitution failures otherwise leave
# the loop empty while the script exits successfully. Only Bash and sed are needed.
sed -n 's/^ *"\([a-z0-9-]*\)": {$/\1/p; s/^ *"\([a-z0-9.-]*\)": "\([0-9a-f]\{40\}\)",\{0,1\}$/\1\t\2/p' "$root/fixtures/revisions.lock.json" > "$work/lock-lines"
current_repo=""
while IFS=$'\t' read -r name commit; do
  if [[ -z "$commit" ]]; then current_repo="$name"; continue; fi
  [[ -n "$current_repo" ]] || { echo 'fixture revision has no repository' >&2; exit 1; }
  printf '%s\t%s\t%s\n' "$current_repo" "$name" "$commit"
done < "$work/lock-lines" > "$work/revisions.tsv"
[[ -s "$work/revisions.tsv" ]] || { echo 'fixture revision inventory is empty' >&2; exit 1; }
while IFS=$'\t' read -r repo tag sha; do
  src="$work/$repo@$tag"; mkdir -p "$src"
  git -C "$root/fixtures/.repos/$repo" archive "$sha" | tar -x -C "$src"
  dest="$out/$repo/$tag"; mkdir -p "$dest"
  case "$lang" in
    cs) dotnet "$root/platform/src/CapabilityExplorer.Extractors.CSharp/bin/Debug/net10.0/ce-cs.dll" extract \
          --source "$src" --repository "$repo" --commit "$sha" --out "$dest" --packages "$root/.sandbox-cache/nuget" | sed "s#^#$repo@$tag #" ;;
    ts) node "$root/extractors/ts/src/cli.ts" extract --source "$src" --repository "$repo" --commit "$sha" --out "$dest" | sed "s#^#$repo@$tag #" ;;
    sql) node "$root/extractors/ts/src/cli.ts" extract-sql --source "$src" --repository "$repo" --commit "$sha" --out "$dest" | sed "s#^#$repo@$tag #" ;;
  esac
done < "$work/revisions.tsv"
