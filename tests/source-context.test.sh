#!/usr/bin/env bash
# Source admission is read-only and must precede every delivery side effect.
set -euo pipefail
source_root="$(cd "$(dirname "$0")/.." && pwd -P)"
scratch="$(mktemp -d)"
trap 'cd /; rm -rf "$scratch"' EXIT
export GIT_CONFIG_NOSYSTEM=1
export HOME="$scratch/home" XDG_CONFIG_HOME="$scratch/home/.config"
mkdir -p "$HOME" "$scratch/owner/scripts/lib" "$scratch/owner/config/git-hooks"
for path in scripts/local-check scripts/pr-check config/git-hooks/pre-push; do
  cp "$source_root/$path" "$scratch/owner/$path"
done
if [[ -f "$source_root/scripts/lib/source-context.sh" ]]; then
  cp "$source_root/scripts/lib/source-context.sh" "$scratch/owner/scripts/lib/"
fi
git -c init.templateDir= init -q -b feature "$scratch/owner"
git -C "$scratch/owner" config user.name 'Source context test'
git -C "$scratch/owner" config user.email 'source-context@example.invalid'
git -C "$scratch/owner" config commit.gpgsign false
git -C "$scratch/owner" add .
git -C "$scratch/owner" commit -qm initial
git -c init.templateDir= init -q -b feature "$scratch/other"
git -c init.templateDir= init --bare -q "$scratch/registry.git"
mkdir "$scratch/not-a-repository"
count=0
for entry in config/git-hooks/pre-push scripts/local-check scripts/pr-check; do
  for context in other registry.git not-a-repository; do
    before="$(git -C "$scratch/owner" hash-object .git/config .git/index "$entry")"
    status=0
    (cd "$scratch/$context"; bash "$scratch/owner/$entry" </dev/null) > "$scratch/output" 2>&1 || status=$?
    [[ $status -ne 0 ]] || { echo "unexpected admission: $entry from $context" >&2; exit 1; }
    grep -F 'source-context:' "$scratch/output" >/dev/null ||
      { echo "missing source-context diagnostic: $entry from $context" >&2; cat "$scratch/output"; exit 1; }
    [[ "$before" == "$(git -C "$scratch/owner" hash-object .git/config .git/index "$entry")" ]] ||
      { echo 'rejected probe changed owner metadata/source' >&2; exit 1; }
    [[ ! -e "$scratch/owner/.sandbox-cache" && ! -e "$scratch/$context/.sandbox-cache" ]] ||
      { echo 'rejected probe reserved local CI state' >&2; exit 1; }
    count=$((count + 1))
  done
done

source "$source_root/scripts/lib/source-context.sh"
cd "$scratch/owner"
[[ "$(ce_source_context_root "$(pwd -P)")" == "$(pwd -P)" ]] ||
  { echo 'ordinary checkout was not admitted' >&2; exit 1; }
count=$((count + 1))
git worktree add -q -b linked "$scratch/linked"
cd "$scratch/linked"
[[ "$(ce_source_context_root "$(pwd -P)")" == "$(pwd -P)" ]] ||
  { echo 'linked checkout was not admitted' >&2; exit 1; }
count=$((count + 1))

mkdir "$scratch/bin"
cat > "$scratch/bin/git" <<'MOCK'
#!/usr/bin/env bash
exit 128
MOCK
chmod +x "$scratch/bin/git"
if PATH="$scratch/bin:$PATH" ce_source_context_root "$PWD" > "$scratch/output" 2>&1; then
  echo 'inaccessible Git context was admitted' >&2
  exit 1
fi
grep -F 'source-context:' "$scratch/output" >/dev/null
count=$((count + 1))
echo "Passed $count source-context admission checks"
