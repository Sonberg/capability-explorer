#!/usr/bin/env bash
# Isolated Git repositories and fake runner/daemon; no remote CI or image pulls.
set -euo pipefail
source_root="$(cd "$(dirname "$0")/.." && pwd)"
scratch="$(mktemp -d)"
trap 'cd /; rm -rf "$scratch"' EXIT
mkdir -p "$scratch/bin" "$scratch/home"
export HOME="$scratch/home" XDG_CONFIG_HOME="$scratch/home/.config"
export PATH="$scratch/bin:$PATH"
export ACT_TEST_LOG="$scratch/act.args" ACT_TEST_ENV_LOG="$scratch/act.env"
cat > "$scratch/bin/gh" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
[[ "$1" == act ]] || exit 91
shift
if [[ "$1" == --version ]]; then
  echo "${ACT_TEST_VERSION:-act version 0.2.94+dirty}"
  exit 0
fi
printf '%s\n' "$@" > "$ACT_TEST_LOG"
printf '%s\n' "${GH_TOKEN-unset}" "${GITHUB_TOKEN-unset}" "${GH_ENTERPRISE_TOKEN-unset}" "${GITHUB_ENTERPRISE_TOKEN-unset}" > "$ACT_TEST_ENV_LOG"
[[ -z "${ACT_TEST_CHANGE:-}" ]] || echo changed >> file.txt
[[ -z "${ACT_TEST_CHANGE_HEAD:-}" ]] || git commit --allow-empty -qm advanced
if [[ -n "${ACT_TEST_ADVANCE_TARGET:-}" ]]; then
  next="$(git --git-dir="$ACT_TEST_ADVANCE_TARGET" commit-tree HEAD^{tree} -p HEAD -m advanced)"
  git --git-dir="$ACT_TEST_ADVANCE_TARGET" update-ref refs/heads/main "$next"
fi
if [[ -n "${ACT_TEST_ADVANCE_OTHER:-}" ]]; then
  next="$(git --git-dir="$ACT_TEST_ADVANCE_OTHER" commit-tree HEAD^{tree} -p HEAD -m advanced-main)"
  git --git-dir="$ACT_TEST_ADVANCE_OTHER" update-ref refs/heads/stage-1-contract-and-fixtures "$next"
fi
[[ -z "${ACT_TEST_REWIND_TARGET:-}" ]] || git --git-dir="$ACT_TEST_REMOTE" update-ref refs/heads/main "$ACT_TEST_REWIND_TARGET"
[[ -z "${ACT_TEST_LOSE_REMOTE:-}" ]] || git remote set-url origin "$ACT_TEST_LOSE_REMOTE"
exit "${ACT_TEST_FAIL:-0}"
MOCK
cat > "$scratch/bin/docker" <<'MOCK'
#!/usr/bin/env bash
set -eu
case "$1" in
  context) echo unix:///test/docker.sock ;;
  info) exit "${ACT_TEST_DOCKER_FAIL:-0}" ;;
  image) exit "${ACT_TEST_IMAGE_FAIL:-0}" ;;
  *) exit 92 ;;
esac
MOCK
chmod +x "$scratch/bin/gh" "$scratch/bin/docker"
count=0
pass() { count=$((count + 1)); echo "ok $count - $*"; }
fail() { echo "FAIL: $*" >&2; exit 1; }
expect_failure() {
  local status=0
  "$@" > "$scratch/output" 2>&1 || status=$?
  [[ $status -ne 0 ]] || fail "unexpected success: $*"
}
contains() { grep -F -- "$1" "$2" >/dev/null || fail "missing '$1' in $2"; }
not_run() { [[ ! -f "$ACT_TEST_LOG" ]] || fail 'runner unexpectedly executed'; }
new_repo() {
  repo="$scratch/repo-$count"
  mkdir -p "$repo/scripts" "$repo/config/git-hooks" "$repo/.github/workflows"
  cp "$source_root/scripts/local-check" "$source_root/scripts/install-git-hooks" "$source_root/scripts/check-main-alignment" "$source_root/scripts/check-target-alignment" "$source_root/scripts/pr-check" "$repo/scripts/"
  mkdir -p "$repo/scripts/lib"
  mkdir -p "$repo/scripts/ci" "$repo/tests"
  cp "$source_root/scripts/ci/docs-check.py" "$repo/scripts/ci/"
  cp "$source_root/tests/docs-ci.test.py" "$repo/tests/"
  cp "$source_root/scripts/lib/container-engine.sh" "$source_root/scripts/lib/source-context.sh" "$repo/scripts/lib/"
  printf '#!/bin/sh\nexit "${ACT_TEST_AUDIT_FAIL:-0}"\n' > "$repo/scripts/actions-audit"
  chmod +x "$repo/scripts/actions-audit"
  cp "$source_root/config/git-hooks/pre-push" "$repo/config/git-hooks/"
  printf '.sandbox-cache/\n' > "$repo/.gitignore"
  echo 'name: fixture' > "$repo/.github/workflows/ci.yml"
  echo original > "$repo/file.txt"
  git -c init.templateDir= init -q -b feature "$repo"
  cd "$repo"
  git config user.name 'Local CI test'
  git config user.email 'local-ci@example.invalid'
  git config commit.gpgsign false
  git add .
  git commit -qm initial
  git branch main
  git branch stage-1-contract-and-fixtures
  remote="$scratch/remote-$count.git"
  git -c init.templateDir= clone --bare -q "$repo" "$remote"
  git --git-dir="$remote" config user.name 'Local CI test'
  git --git-dir="$remote" config user.email 'local-ci@example.invalid'
  git --git-dir="$remote" config commit.gpgsign false
  git remote add origin "$remote"
  rm -f "$ACT_TEST_LOG" "$ACT_TEST_ENV_LOG"
  unset ACT_TEST_AUDIT_FAIL ACT_TEST_FAIL ACT_TEST_CHANGE ACT_TEST_CHANGE_HEAD ACT_TEST_VERSION ACT_TEST_DOCKER_FAIL ACT_TEST_IMAGE_FAIL
  unset GH_TOKEN GITHUB_TOKEN GH_ENTERPRISE_TOKEN GITHUB_ENTERPRISE_TOKEN
  unset LOCAL_CI_EXPECTED_HEAD ACT_TEST_ADVANCE_TARGET ACT_TEST_ADVANCE_OTHER ACT_TEST_REWIND_TARGET ACT_TEST_REMOTE ACT_TEST_LOSE_REMOTE
  zero=0000000000000000000000000000000000000000
  head="$(git rev-parse HEAD)"
}
refs() { printf 'refs/heads/feature %s refs/heads/feature %s\n' "$head" "$zero"; }

new_repo
export ACT_TEST_AUDIT_FAIL=19
expect_failure scripts/local-check
not_run
pass 'failed host action advisory audit blocks runner'

new_repo
export GH_TOKEN=fixture-token GITHUB_TOKEN=fixture-token GH_ENTERPRISE_TOKEN=fixture-token GITHUB_ENTERPRISE_TOKEN=fixture-token
refs | config/git-hooks/pre-push origin ignored > "$scratch/output" 2>&1
contains 'pull_request' "$ACT_TEST_LOG"
contains '.github/workflows/ci.yml' "$ACT_TEST_LOG"
event="$(sed -n '/^--eventpath$/ { n; p; }' "$ACT_TEST_LOG")"
contains '{"action":"synchronize","pull_request":{"base":{"ref":"main"}}}' "$event"
contains '--bind' "$ACT_TEST_LOG"
contains 'ubuntu-24.04=catthehacker/ubuntu:act-24.04' "$ACT_TEST_LOG"
contains '--pull=false' "$ACT_TEST_LOG"
contains '--no-cache-server' "$ACT_TEST_LOG"
contains 'GITHUB_TOKEN=' "$ACT_TEST_LOG"
for flag in --env-file --secret-file --var-file --input-file; do
  actual="$(sed -n "/^$flag\$/ { n; p; }" "$ACT_TEST_LOG")"
  [[ "$actual" == /dev/null ]] || fail "$flag must disable automatic file loading"
done
[[ "$(sort -u "$ACT_TEST_ENV_LOG")" == unset ]] || fail 'host GitHub tokens reached act'
contains "head=$head" .sandbox-cache/local-ci/last-success.txt
contains 'runner=act version 0.2.94+dirty' .sandbox-cache/local-ci/last-success.txt
contains '@sha256:c58e2b364da03b0c804c7d660f2ecbedf2f221a382b9baa0b344b0144780ff43' "$ACT_TEST_LOG"
pass 'HEAD push runs entire workflow with no automatic secret files or host GitHub token'

rm -f "$ACT_TEST_LOG"
scripts/local-check > "$scratch/output" 2>&1
[[ -f "$ACT_TEST_LOG" ]] || fail 'success receipt incorrectly skipped a new check'
pass 'successful receipt never skips a later complete check'

export ACT_TEST_FAIL=23
status=0
refs | config/git-hooks/pre-push origin ignored > "$scratch/output" 2>&1 || status=$?
[[ $status -eq 23 ]] || fail "runner failure was not propagated ($status)"
[[ ! -f .sandbox-cache/local-ci/last-success.txt ]] || fail 'failed run retained success receipt'
[[ ! -d .sandbox-cache/local-ci/run.lock ]] || fail 'failed run retained lock'
pass 'runner failure blocks push and removes old success evidence and lock'

new_repo
printf 'refs/heads/feature %s refs/heads/main %s\n' "$head" "$zero" | expect_failure config/git-hooks/pre-push origin ignored
contains 'updates to main are prohibited' "$scratch/output"
not_run
pass 'push to main is rejected before CI'

new_repo
{ refs; printf 'refs/heads/feature %s refs/heads/main %s\n' "$head" "$zero"; } | expect_failure config/git-hooks/pre-push origin ignored
contains 'updates to main are prohibited' "$scratch/output"
not_run
pass 'mixed feature and main update push is rejected before CI'

new_repo
printf '(delete) %s refs/heads/main %s\n' "$zero" "$head" | expect_failure config/git-hooks/pre-push origin ignored
contains 'updates to main are prohibited' "$scratch/output"
not_run
pass 'main deletion is rejected before the deletion-only shortcut'

new_repo
{ refs; printf '(delete) %s refs/heads/main %s\n' "$zero" "$head"; } | expect_failure config/git-hooks/pre-push origin ignored
contains 'updates to main are prohibited' "$scratch/output"
not_run
pass 'mixed feature and main deletion push is rejected before CI'

new_repo
git checkout -q main
expect_failure scripts/local-check
contains 'work on a feature branch targeting main; do not work directly on main' "$scratch/output"
not_run
pass 'local CI refuses checked-out main'
expect_failure scripts/pr-check
contains 'work on a feature branch targeting main; do not work directly on main' "$scratch/output"
not_run
pass 'PR preparation refuses checked-out main'

new_repo
echo dirty >> file.txt
refs | expect_failure config/git-hooks/pre-push origin ignored
contains 'commit or stash' "$scratch/output"
not_run
pass 'dirty tracked tree blocks push before runner starts'

new_repo
echo untracked > untracked.txt
refs | expect_failure config/git-hooks/pre-push origin ignored
not_run
pass 'untracked source blocks push before runner starts'

new_repo
old="$head"
echo second >> file.txt
git add file.txt
git commit -qm second
head="$(git rev-parse HEAD)"
printf 'refs/heads/old %s refs/heads/old %s\n' "$old" "$zero" | expect_failure config/git-hooks/pre-push origin ignored
contains 'not checked-out HEAD' "$scratch/output"
not_run
pass 'non-HEAD outgoing branch fails closed'

LOCAL_CI_EXPECTED_HEAD="$old" expect_failure scripts/local-check
contains 'HEAD changed after outgoing refs' "$scratch/output"
not_run
pass 'HEAD must still equal the ref validated by pre-push when CI starts'

{ refs; printf 'refs/heads/old %s refs/heads/old %s\n' "$old" "$zero"; } | expect_failure config/git-hooks/pre-push origin ignored
not_run
pass 'multiple outgoing source commits fail before runner starts'

new_repo
git -c tag.gpgsign=false tag -a checked -m checked
{ refs; printf 'refs/tags/checked %s refs/tags/checked %s\n' "$(git rev-parse checked)" "$zero"; } | config/git-hooks/pre-push origin ignored > "$scratch/output" 2>&1
[[ -f "$ACT_TEST_LOG" ]] || fail 'annotated tag to HEAD was rejected'
pass 'annotated tag and branch to the same HEAD are checked together'

new_repo
echo dirty >> file.txt
printf '(delete) %s refs/heads/old %s\n' "$zero" "$head" | config/git-hooks/pre-push origin ignored
not_run
pass 'deletion-only push needs no source CI'

new_repo
git checkout --orphan unborn -q
git rm -r --cached -q .
printf '(delete) %s refs/heads/old %s\n' "$zero" "$head" | config/git-hooks/pre-push origin ignored
not_run
pass 'deletion-only push also works from an unborn branch'

new_repo
printf 'malformed\n' | expect_failure config/git-hooks/pre-push origin ignored
not_run
pass 'malformed outgoing ref input blocks push'

printf 'malformed' | expect_failure config/git-hooks/pre-push origin ignored
not_run
pass 'unterminated malformed input cannot bypass ref validation'

new_repo
expect_failure scripts/local-check --dryrun
not_run
pass 'arbitrary act flags cannot bypass CI'

new_repo
export ACT_TEST_VERSION='gh-act audit version 1.0'
expect_failure scripts/local-check
contains 'not a released nektos/act' "$scratch/output"
not_run
pass 'workflow auditor cannot substitute for workflow execution'

new_repo
export ACT_TEST_VERSION='act version 0.2.85'
expect_failure scripts/local-check
contains '0.2.86 or newer is required' "$scratch/output"
not_run
pass 'act versions affected by GHSA-xmgr-9pqc-h5vw cannot execute CI'

new_repo
export ACT_TEST_DOCKER_FAIL=1
expect_failure scripts/local-check
contains 'Docker daemon' "$scratch/output"
not_run
pass 'unavailable Docker blocks check'

new_repo
export ACT_TEST_IMAGE_FAIL=1
expect_failure scripts/local-check
contains 'pull the local runner image first' "$scratch/output"
not_run
pass 'missing runner image fails with explicit setup command'

new_repo
mkdir -p .sandbox-cache/local-ci/run.lock
expect_failure scripts/local-check
contains 'another check holds' "$scratch/output"
not_run
pass 'concurrent worktree check is rejected'

new_repo
export ACT_TEST_CHANGE=1
expect_failure scripts/local-check
contains 'working tree changed during CI' "$scratch/output"
[[ ! -f .sandbox-cache/local-ci/last-success.txt ]] || fail 'changed source received success evidence'
pass 'source mutation during successful runner blocks push'

new_repo
export ACT_TEST_CHANGE_HEAD=1
expect_failure scripts/local-check
contains 'HEAD changed during CI' "$scratch/output"
[[ ! -f .sandbox-cache/local-ci/last-success.txt ]] || fail 'changed HEAD received success evidence'
pass 'HEAD mutation during successful runner blocks push'

new_repo
mkdir -p "$XDG_CONFIG_HOME/act"
printf '%s\n' --dryrun > "$XDG_CONFIG_HOME/act/actrc"
expect_failure scripts/local-check
contains 'unsupported flag' "$scratch/output"
not_run
rm -f "$XDG_CONFIG_HOME/act/actrc"
pass 'user actrc cannot inject a dry run'

printf '%s\n' '-P ubuntu-latest=node:22-bookworm' > "$XDG_CONFIG_HOME/act/actrc"
scripts/local-check > "$scratch/output" 2>&1
rm -f "$XDG_CONFIG_HOME/act/actrc"
pass 'existing literal user platform mappings remain supported'

new_repo
printf '%s\n' '--secret GITHUB_TOKEN=fixture-token' > .actrc
git add .actrc
git commit -qm config
expect_failure scripts/local-check
not_run
pass 'tracked project actrc cannot inject credentials'

new_repo
scripts/install-git-hooks > "$scratch/output" 2>&1
hook="$(git rev-parse --git-common-dir)/hooks/pre-push"
[[ -x "$hook" ]] || fail 'hook was not installed'
scripts/install-git-hooks > "$scratch/output" 2>&1
refs | "$hook" origin ignored > "$scratch/output" 2>&1
[[ -f "$ACT_TEST_LOG" ]] || fail 'installed dispatcher did not invoke current checkout'
pass 'installer is idempotent and installed hook checks current checkout'

git worktree add -qb linked "$scratch/linked-$count"
(
  cd "$scratch/linked-$count"
  scripts/install-git-hooks > "$scratch/output" 2>&1
  refs | "$(git rev-parse --git-common-dir)/hooks/pre-push" origin ignored > "$scratch/output" 2>&1
)
[[ -f "$scratch/linked-$count/.sandbox-cache/local-ci/last-success.txt" ]] || fail 'shared dispatcher used a different checkout'
pass 'linked worktrees share installation while checking their own source'

mv config/git-hooks/pre-push config/git-hooks/pre-push.missing
refs | expect_failure "$hook" origin ignored
contains 'tracked config/git-hooks/pre-push is missing' "$scratch/output"
pass 'shared dispatcher fails closed on a checkout without the tracked hook'

new_repo
mkdir -p .git/hooks
printf '#!/bin/sh\nexit 17\n' > .git/hooks/pre-push
cp .git/hooks/pre-push "$scratch/existing-hook"
expect_failure scripts/install-git-hooks
cmp -s .git/hooks/pre-push "$scratch/existing-hook" || fail 'existing hook was overwritten'
pass 'installer preserves existing hook content'

new_repo
git config core.hooksPath custom-hooks
expect_failure scripts/install-git-hooks
[[ "$(git config core.hooksPath)" == custom-hooks ]] || fail 'custom hooksPath changed'
[[ ! -e .git/hooks/pre-push ]] || fail 'installed hook despite custom hooksPath'
pass 'installer preserves custom core.hooksPath'

new_repo
next="$(git --git-dir="$remote" commit-tree HEAD^{tree} -p HEAD -m upstream)"
git --git-dir="$remote" update-ref refs/heads/main "$next"
expect_failure scripts/local-check
contains 'HEAD does not contain origin/main' "$scratch/output"
not_run
pass 'fresh remote target ahead of HEAD blocks CI before runner starts'
git merge --ff-only origin/main > "$scratch/output" 2>&1
head="$(git rev-parse HEAD)"
scripts/local-check > "$scratch/output" 2>&1
scripts/pr-check >> "$scratch/output" 2>&1
contains "base=$head" .sandbox-cache/local-ci/last-success.txt
contains 'target=main' .sandbox-cache/local-ci/last-success.txt
pass 'integrated target gets exact base receipt and passes PR preparation'

new_repo
git remote set-url origin "$scratch/missing-remote.git"
expect_failure scripts/local-check
contains 'cannot refresh origin/main' "$scratch/output"
not_run
pass 'unavailable target never falls back to a cached tracking ref'

new_repo
export ACT_TEST_ADVANCE_TARGET="$remote"
expect_failure scripts/local-check
[[ -f "$ACT_TEST_LOG" ]] || fail 'runner did not run before target advanced'
[[ ! -f .sandbox-cache/local-ci/last-success.txt ]] || fail 'moving target received success receipt'
pass 'target advancing during CI invalidates the successful runner result'

new_repo
expect_failure scripts/pr-check
contains 'no successful local CI receipt' "$scratch/output"
scripts/local-check > "$scratch/output" 2>&1
scripts/pr-check >> "$scratch/output" 2>&1
next="$(git --git-dir="$remote" commit-tree HEAD^{tree} -p HEAD -m upstream)"
git --git-dir="$remote" update-ref refs/heads/main "$next"
expect_failure scripts/pr-check
contains 'HEAD does not contain origin/main' "$scratch/output"
pass 'target advancing after CI blocks PR preparation'

new_repo
export ACT_TEST_ADVANCE_OTHER="$remote"
scripts/local-check > "$scratch/output" 2>&1
[[ -f "$ACT_TEST_LOG" ]] || fail 'runner did not execute while unrelated stage advanced'
contains "base=$head" .sandbox-cache/local-ci/last-success.txt
contains 'target=main' .sandbox-cache/local-ci/last-success.txt
scripts/pr-check >> "$scratch/output" 2>&1
next="$(git --git-dir="$remote" commit-tree HEAD^{tree} -p HEAD -m later-stage)"
git --git-dir="$remote" update-ref refs/heads/stage-1-contract-and-fixtures "$next"
scripts/pr-check >> "$scratch/output" 2>&1
pass 'independent stage advancement during and after CI preserves the main receipt'

new_repo
scripts/local-check > "$scratch/output" 2>&1
sed 's/^target=.*/target=stage-1-contract-and-fixtures/' .sandbox-cache/local-ci/last-success.txt > "$scratch/tampered-receipt"
cp "$scratch/tampered-receipt" .sandbox-cache/local-ci/last-success.txt
expect_failure scripts/pr-check
contains 'receipt target does not match main' "$scratch/output"
pass 'PR preparation rejects receipt target tampering'

new_repo
scripts/local-check > "$scratch/output" 2>&1
git commit --allow-empty -qm changed
expect_failure scripts/pr-check
contains 'receipt does not match HEAD' "$scratch/output"
pass 'PR preparation rejects untested HEAD'

new_repo
scripts/local-check > "$scratch/output" 2>&1
sed '/^base=/d' .sandbox-cache/local-ci/last-success.txt > "$scratch/old-receipt"
cp "$scratch/old-receipt" .sandbox-cache/local-ci/last-success.txt
expect_failure scripts/pr-check
contains 'target branch changed since CI' "$scratch/output"
pass 'legacy receipt without tested target is rejected'

new_repo
mkdir -p docs
printf '# Example\n\n[Source](../file.txt)\n' > docs/example.md
git add docs/example.md
git commit -qm documentation
head="$(git rev-parse HEAD)"
export ACT_TEST_DOCKER_FAIL=1 ACT_TEST_AUDIT_FAIL=19
refs | config/git-hooks/pre-push origin ignored > "$scratch/output" 2>&1
not_run
contains 'mode=docs' .sandbox-cache/local-ci/last-success.txt
scripts/pr-check >> "$scratch/output" 2>&1
pass 'approved Markdown push and PR check need no Docker or full runner'
sed 's/^mode=docs/mode=full/' .sandbox-cache/local-ci/last-success.txt > "$scratch/wrong-profile"
cp "$scratch/wrong-profile" .sandbox-cache/local-ci/last-success.txt
expect_failure scripts/pr-check
contains 'receipt CI profile does not match' "$scratch/output"
pass 'PR check rejects a mismatched documentation receipt profile'

new_repo
export CE_CONTAINER_ENGINE=podman
expect_failure scripts/local-check
contains 'not qualified' "$scratch/output"
not_run
unset CE_CONTAINER_ENGINE
scripts/local-check > "$scratch/output" 2>&1
rm -f "$ACT_TEST_LOG"
export CE_CONTAINER_ENGINE=podman
expect_failure scripts/pr-check
contains 'not qualified' "$scratch/output"
not_run
unset CE_CONTAINER_ENGINE
pass 'full-profile local and PR checks still refuse unqualified Podman'

new_repo
mkdir -p docs
printf '# Example\n\n[Missing](missing.md)\n' > docs/example.md
git add docs/example.md
git commit -qm broken-documentation
expect_failure scripts/local-check
not_run
contains 'missing local link' "$scratch/output"
[[ ! -f .sandbox-cache/local-ci/last-success.txt ]] || fail 'broken documentation received success evidence'
pass 'broken documentation blocks push without producing a success receipt'

new_repo
mkdir -p .sandbox-cache/local-ci/run.lock
expect_failure scripts/pr-check
contains 'another local CI or PR check' "$scratch/output"
pass 'PR preparation cannot race a running local gate'

new_repo
export ACT_TEST_REAL_GIT="$(command -v git)"
cat > "$scratch/bin/git" <<'MOCK'
#!/usr/bin/env bash
[[ "$1" != status ]] || exit 27
exec "$ACT_TEST_REAL_GIT" "$@"
MOCK
chmod +x "$scratch/bin/git"
expect_failure scripts/pr-check
contains 'working tree must be clean' "$scratch/output"
rm -f "$scratch/bin/git"
pass 'git status failure with empty output cannot pass PR preparation'

new_repo
old="$head"
git commit --allow-empty -qm descendant
head="$(git rev-parse HEAD)"
git --git-dir="$remote" fetch -q "$repo" feature:refs/heads/main
export ACT_TEST_REWIND_TARGET="$old" ACT_TEST_REMOTE="$remote"
expect_failure scripts/local-check
contains 'target branch changed during CI' "$scratch/output"
[[ ! -f .sandbox-cache/local-ci/last-success.txt ]] || fail 'different ancestor base received success receipt'
pass 'a changed target already contained in HEAD still invalidates CI'

new_repo
export ACT_TEST_LOSE_REMOTE="$scratch/unavailable-after-ci.git"
expect_failure scripts/local-check
contains 'cannot refresh origin/main' "$scratch/output"
[[ -f "$ACT_TEST_LOG" ]] || fail 'runner never executed'
[[ ! -f .sandbox-cache/local-ci/last-success.txt ]] || fail 'failed final fetch received success receipt'
pass 'final fetch failure invalidates successful runner evidence'

new_repo
base="$head"
git commit --allow-empty -qm local-descendant
export ACT_TEST_REPLACEMENT="$(git rev-parse HEAD)"
export ACT_TEST_REAL_GIT="$(command -v git)"
cat > "$scratch/bin/git" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
"$ACT_TEST_REAL_GIT" "$@"
if [[ "$1" == fetch ]]; then
  "$ACT_TEST_REAL_GIT" update-ref refs/remotes/origin/main "$ACT_TEST_REPLACEMENT"
fi
MOCK
chmod +x "$scratch/bin/git"
next="$(git --git-dir="$remote" commit-tree "$base^{tree}" -p "$base" -m unrelated-stage)"
git --git-dir="$remote" update-ref refs/heads/stage-1-contract-and-fixtures "$next"
actual="$(scripts/check-main-alignment)"
rm -f "$scratch/bin/git"
[[ "$actual" == "$base" ]] || fail 'alignment followed another worktree tracking-ref mutation'
[[ -z "$(git for-each-ref refs/ce-target-check)" ]] || fail 'temporary verification ref leaked'
pass 'legacy alignment alias binds a main fetch despite another worktree tracking-ref mutation'

echo "Passed $count local CI harness checks"
