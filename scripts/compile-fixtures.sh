#!/usr/bin/env bash
# Checks that every fixture revision compiles (or fails) as intended.
#   restore phase (needs network):  scripts/sandbox dotnet scripts/compile-fixtures.sh restore-dotnet
#                                    scripts/sandbox node   scripts/compile-fixtures.sh restore-node
#   build phase (run --offline):     scripts/sandbox --offline dotnet scripts/compile-fixtures.sh build-dotnet
#                                    scripts/sandbox --offline node   scripts/compile-fixtures.sh build-node
# Each fixture tag is checked out into fixtures/.work/<repo>@<tag> so fixtures/.repos stays pristine.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
repos="$root/fixtures/.repos"
work="$root/fixtures/.work"
phase="${1:?phase}"

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

rc=0
case "$phase" in
  restore-dotnet)
    for c in "${dotnet_cases[@]}"; do read -r repo tag entry expect <<<"$c"
      dir=$(checkout "$repo" "$tag")
      # Restore failures for intentionally broken projects are expected; the build phase asserts them.
      (cd "$dir" && dotnet restore "$entry" -v q -nologo >/dev/null 2>&1) || [[ $expect != ok ]] || { echo "restore failed: $repo@$tag"; rc=1; }
    done ;;
  build-dotnet)
    for c in "${dotnet_cases[@]}"; do read -r repo tag entry expect <<<"$c"
      dir=$(checkout "$repo" "$tag"); log="$dir/.build.log"; set +e
      (cd "$dir" && dotnet build "$entry" --no-restore -v q -nologo) >"$log" 2>&1; status=$?; set -e
      check "$repo@$tag" "$expect" "$log" $status || rc=1
    done ;;
  restore-node)
    for c in "${node_cases[@]}"; do read -r repo tag entry expect <<<"$c"
      dir=$(checkout "$repo" "$tag")
      (cd "$dir/$entry" && npm ci --ignore-scripts >/dev/null) || { echo "npm ci failed: $repo@$tag"; rc=1; }
    done ;;
  build-node)
    for c in "${node_cases[@]}"; do read -r repo tag entry expect <<<"$c"
      dir=$(checkout "$repo" "$tag"); log="$dir/.build.log"; set +e
      (cd "$dir/$entry" && ./node_modules/.bin/tsc -p . --noEmit) >"$log" 2>&1; status=$?; set -e
      check "$repo@$tag" "$expect" "$log" $status || rc=1
    done ;;
  *) echo "unknown phase $phase" >&2; exit 2 ;;
esac
exit $rc
