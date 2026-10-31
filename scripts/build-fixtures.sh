#!/usr/bin/env bash
# Builds fixture git repositories deterministically from fixtures/src overlays.
#   scripts/build-fixtures.sh            build into fixtures/.repos and verify against revisions.lock.json
#   scripts/build-fixtures.sh --update   build and rewrite revisions.lock.json
# Needs only bash, git and node (for JSON). Runs offline. Intended to run inside scripts/sandbox.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
manifest="$root/fixtures/manifest.json"
lock="$root/fixtures/revisions.lock.json"
out="$root/fixtures/.repos"
update=0
[[ "${1:-}" == "--update" ]] && update=1

# Isolate from any user/system git configuration.
export GIT_CONFIG_NOSYSTEM=1 GIT_CONFIG_GLOBAL=/dev/null HOME="${HOME:-/tmp}"
git_() { git -c core.autocrlf=false -c core.fileMode=true -c commit.gpgsign=false -c tag.gpgsign=false -c init.defaultBranch=main "$@"; }

json() { node -e "const m=require(process.argv[1]); $2" "$manifest"; }
author_name="$(json x 'console.log(m.author.name)')"
author_email="$(json x 'console.log(m.author.email)')"

rm -rf "$out" "$root/fixtures/.work"; mkdir -p "$out"
result="{"
first_repo=1
while IFS=$'\t' read -r repo steps; do
  dir="$out/$repo"
  git_ init -q "$dir"
  [[ $first_repo -eq 1 ]] || result+=","
  first_repo=0
  result+="\"$repo\":{"
  first_step=1
  while IFS=$'\t' read -r step tag date message; do
    src="$root/fixtures/src/$repo/$step"
    [[ -d "$src" ]] || { echo "missing $src" >&2; exit 1; }
    if [[ -f "$src/.delete" ]]; then
      while IFS= read -r p; do [[ -n "$p" ]] && git_ -C "$dir" rm -q -- "$p"; done < "$src/.delete"
    fi
    # Copy overlay content, normalising permissions so the tree hash is host-independent.
    (cd "$src" && find . -type f ! -name .delete -print0) | while IFS= read -r -d '' f; do
      mkdir -p "$dir/$(dirname "$f")"
      cp "$src/$f" "$dir/$f"; chmod 0644 "$dir/$f"
    done
    git_ -C "$dir" add -A
    GIT_AUTHOR_NAME="$author_name" GIT_AUTHOR_EMAIL="$author_email" GIT_AUTHOR_DATE="$date" \
    GIT_COMMITTER_NAME="$author_name" GIT_COMMITTER_EMAIL="$author_email" GIT_COMMITTER_DATE="$date" \
      git_ -C "$dir" commit -q -m "$message"
    git_ -C "$dir" tag "$tag"
    sha="$(git_ -C "$dir" rev-parse HEAD)"
    [[ $first_step -eq 1 ]] || result+=","
    first_step=0
    result+="\"$tag\":\"$sha\""
    echo "$repo $tag $sha"
  done < <(json x "for (const r of m.repositories) if (r.id==='$repo') for (const s of r.steps) console.log([s.dir,s.tag,s.date,s.message].join('\t'))")
  result+="}"
done < <(json x 'for (const r of m.repositories) console.log(r.id + "\t" + r.steps.length)')
result+="}"

formatted="$(node -e 'console.log(JSON.stringify(JSON.parse(process.argv[1]), null, 2))' "$result")"
if [[ $update -eq 1 ]]; then
  printf '%s\n' "$formatted" > "$lock"
  echo "wrote $lock"
elif ! diff <(printf '%s\n' "$formatted") "$lock" >/dev/null 2>&1; then
  echo "fixture revisions differ from $lock (run with --update if the change is intended)" >&2
  diff <(printf '%s\n' "$formatted") "$lock" >&2 || true
  exit 1
else
  echo "fixture revisions match revisions.lock.json"
fi
