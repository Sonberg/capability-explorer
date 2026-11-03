#!/usr/bin/env bash
# Checks that every fixture revision compiles (or fails) as intended.
#   restore phase: scripts/compile-fixtures.sh restore-dotnet | restore-node [jobs]
#   build phase:   scripts/compile-fixtures.sh build-dotnet   | build-node [jobs]
# Each fixture tag is checked out into fixtures/.work/<repo>@<tag> so fixtures/.repos stays pristine.
# jobs is capped at two to bound concurrent fixture resource use.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
repos="$root/fixtures/.repos"
work="$root/fixtures/.work"
phase="${1-}"
if [[ $# -gt 2 ]]; then
  echo "usage: $0 <restore-dotnet|restore-node|build-dotnet|build-node> [1|2]" >&2
  exit 2
fi
jobs="${2-2}"
case "$jobs" in
  1|2) ;;
  *) echo "jobs must be 1 or 2" >&2; exit 2 ;;
esac
case "$phase" in
  restore-dotnet|restore-node|build-dotnet|build-node) ;;
  *) echo "unknown phase: ${phase:-<missing>}" >&2; exit 2 ;;
esac

# repo  tag  entry  expectation
dotnet_cases=(
  "api-cs v1-base Sales.sln ok"
  "api-cs v2-rename Sales.sln ok"
  "api-cs v3-filter Sales.sln ok"
  "worker-cs v1-base Sales.Worker.sln ok"
  "worker-cs v2-broken Sales.Worker.sln fail:CS0234.*'Shared'"
  "forecast-cs v1-base Forecast.sln ok"
  "forecast-cs v2-contract Forecast.sln ok"
)
node_cases=(
  "web-ts v1-base . ok"
  "pricing-node v1-base . ok"
  "pricing-node v2-move . ok"
)

checkout() {
  local repo=$1 tag=$2 dir="$work/$1@$2"
  if [[ ! -d "$dir" ]]; then
    mkdir -p "$work"
    git clone -q --no-hardlinks --branch "$tag" "$repos/$repo" "$dir" 2>/dev/null
  fi
  echo "$dir"
}

check() {
  local label=$1 expect=$2 log=$3 status=$4
  if [[ $expect == ok ]]; then
    if [[ $status -eq 0 ]]; then echo "PASS $label compiles"; else echo "FAIL $label expected to compile"; tail -20 "$log"; return 1; fi
  else
    local pattern=${expect#fail:}
    if [[ $status -ne 0 ]] && grep -Eq "$pattern" "$log"; then echo "PASS $label fails as intended ($pattern)"
    else echo "FAIL $label expected failure matching $pattern"; tail -20 "$log"; return 1; fi
  fi
}

execute_case() {
  local phase=$1 repo=$2 tag=$3 entry=$4 expect=$5
  local dir log status
  dir=$(checkout "$repo" "$tag") || { echo "FAIL checkout $repo@$tag"; return 1; }
  if [[ $phase == *-dotnet ]]; then
    # Concurrent first use must not race .NET's HOME-based migration mutex tree.
    export HOME="$dir/.dotnet-home"
    export DOTNET_CLI_HOME="$HOME"
    mkdir -p "$HOME"
  fi

  case "$phase" in
    restore-dotnet)
      log="$dir/.restore-dotnet.log"
      if (cd "$dir" && dotnet restore "$entry" --configfile "$root/platform/NuGet.Config" -v q -nologo) >"$log" 2>&1; then status=0; else status=$?; fi
      # Restore failures for intentionally broken projects are expected; the build phase asserts them.
      if [[ $status -ne 0 && $expect == ok ]]; then echo "FAIL restore failed: $repo@$tag"; tail -20 "$log"; return 1; fi
      if [[ $status -ne 0 ]]; then echo "PASS $repo@$tag expected broken-project restore failure"; else echo "PASS $repo@$tag restored"; fi
      ;;
    restore-node)
      log="$dir/.restore-node.log"
      if (cd "$dir/$entry" && npm ci --ignore-scripts) >"$log" 2>&1; then status=0; else status=$?; fi
      if [[ $status -ne 0 ]]; then echo "FAIL npm ci failed: $repo@$tag"; tail -20 "$log"; return 1; fi
      echo "PASS $repo@$tag restored"
      ;;
    build-dotnet)
      log="$dir/.build.log"
      if (cd "$dir" && dotnet build "$entry" --no-restore -v q -nologo) >"$log" 2>&1; then status=0; else status=$?; fi
      check "$repo@$tag" "$expect" "$log" "$status"
      ;;
    build-node)
      log="$dir/.build.log"
      if (cd "$dir/$entry" && ./node_modules/.bin/tsc -p . --noEmit) >"$log" 2>&1; then status=0; else status=$?; fi
      check "$repo@$tag" "$expect" "$log" "$status"
      ;;
  esac
}

interrupted=0
on_interrupt() {
  interrupted=1
  echo "received interrupt" >&2
}
trap on_interrupt INT TERM

wait_case() {
  local pid=$1 status=0
  if wait "$pid"; then status=0; else status=$?; fi
  if [[ $interrupted -eq 1 ]]; then
    # A trapped signal can interrupt wait while the child is still running.
    while kill -0 "$pid" 2>/dev/null; do
      if wait "$pid"; then status=0; else status=$?; fi
    done
    status=1
  fi
  return "$status"
}

run_phase() {
  local -a cases pids labels
  local c repo tag entry expect label result_log
  local rc=0 active=0 pid
  case "$phase" in
    restore-dotnet|build-dotnet) cases=("${dotnet_cases[@]}") ;;
    restore-node|build-node) cases=("${node_cases[@]}") ;;
  esac
  mkdir -p "$work/.logs/$phase"
  pids=()
  labels=()

  for c in "${cases[@]}"; do
    if [[ $interrupted -eq 1 ]]; then rc=1; break; fi
    read -r repo tag entry expect <<<"$c"
    label="$repo@$tag"
    result_log="$work/.logs/$phase/$label.log"
    execute_case "$phase" "$repo" "$tag" "$entry" "$expect" >"$result_log" 2>&1 &
    pids+=("$!")
    labels+=("$label")
    active=$((active + 1))
    if [[ $active -eq $jobs ]]; then
      for pid in "${pids[@]}"; do
        if ! wait_case "$pid"; then rc=1; fi
      done
      for label in "${labels[@]}"; do
        result_log="$work/.logs/$phase/$label.log"
        if [[ -f "$result_log" ]]; then cat "$result_log"; else echo "FAIL $label did not complete"; rc=1; fi
      done
      pids=()
      labels=()
      active=0
    fi
  done

  if [[ $active -gt 0 ]]; then
    for pid in "${pids[@]}"; do
      if ! wait_case "$pid"; then rc=1; fi
    done
    for label in "${labels[@]}"; do
      result_log="$work/.logs/$phase/$label.log"
      if [[ -f "$result_log" ]]; then cat "$result_log"; else echo "FAIL $label did not complete"; rc=1; fi
    done
  fi
  if [[ $interrupted -eq 1 ]]; then echo "FAIL $phase interrupted"; rc=1; fi
  return "$rc"
}

run_phase
