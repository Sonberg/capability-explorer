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
exit "${ACT_TEST_FAIL:-0}"
MOCK
cat > "$scratch/bin/docker" <<'MOCK'
#!/usr/bin/env bash
set -eu
case "$1" in
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
  cp "$source_root/scripts/local-check" "$source_root/scripts/install-git-hooks" "$repo/scripts/"
  cp "$source_root/config/git-hooks/pre-push" "$repo/config/git-hooks/"
  printf '.sandbox-cache/\n' > "$repo/.gitignore"
  echo 'name: fixture' > "$repo/.github/workflows/ci.yml"
  echo original > "$repo/file.txt"
  git -c init.templateDir= init -q "$repo"
  cd "$repo"
  git config user.name 'Local CI test'
  git config user.email 'local-ci@example.invalid'
  git config commit.gpgsign false
  git add .
  git commit -qm initial
  rm -f "$ACT_TEST_LOG" "$ACT_TEST_ENV_LOG"
  unset ACT_TEST_FAIL ACT_TEST_CHANGE ACT_TEST_CHANGE_HEAD ACT_TEST_VERSION ACT_TEST_DOCKER_FAIL ACT_TEST_IMAGE_FAIL
  unset GH_TOKEN GITHUB_TOKEN GH_ENTERPRISE_TOKEN GITHUB_ENTERPRISE_TOKEN
  unset LOCAL_CI_EXPECTED_HEAD
  zero=0000000000000000000000000000000000000000
  head="$(git rev-parse HEAD)"
}
refs() { printf 'refs/heads/main %s refs/heads/main %s\n' "$head" "$zero"; }

new_repo
export GH_TOKEN=fixture-token GITHUB_TOKEN=fixture-token GH_ENTERPRISE_TOKEN=fixture-token GITHUB_ENTERPRISE_TOKEN=fixture-token
refs | config/git-hooks/pre-push origin ignored > "$scratch/output" 2>&1
contains 'pull_request' "$ACT_TEST_LOG"
contains '.github/workflows/ci.yml' "$ACT_TEST_LOG"
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

echo "Passed $count local CI harness checks"
