#!/usr/bin/env bash
# Read-only source admission; callers supply the root that owns their entrypoint.
ce_source_context_root() {
  local expected="$1" inside root
  inside="$(git rev-parse --is-inside-work-tree 2>/dev/null)" || {
    echo 'source-context: Git context is unavailable; use a readable working checkout' >&2
    return 1
  }
  if [[ "$inside" != true ]]; then
    echo 'source-context: bare/non-worktree delivery is unsupported; use a linked working checkout, not the repository registry' >&2
    return 1
  fi
  root="$(git rev-parse --show-toplevel)" || {
    echo 'source-context: cannot read the working checkout root' >&2
    return 1
  }
  root="$(cd "$root" && pwd -P)" || {
    echo 'source-context: cannot access the working checkout root' >&2
    return 1
  }
  if [[ "$root" != "$expected" ]]; then
    echo "source-context: entrypoint belongs to a different checkout; run the selected checkout's own delivery command" >&2
    return 1
  fi
  printf '%s\n' "$root"
}
