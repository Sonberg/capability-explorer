#!/usr/bin/env bash
# Project core runtime adapter. Source from trusted project scripts only.
# No eval, CLI aliases, global configuration changes or backend fallback.
ce_runtime_fail() { echo "container-engine: $*" >&2; return 2; }

ce_engine_choice() {
  CE_ENGINE="${CE_CONTAINER_ENGINE-docker}"
  case "$CE_ENGINE" in docker|podman) ;; *) ce_runtime_fail 'CE_CONTAINER_ENGINE must be docker or podman'; return 2 ;; esac
}

ce_require_docker() {
  ce_engine_choice || return
  [[ "$CE_ENGINE" == docker ]] || {
    ce_runtime_fail "$1 is not qualified for Podman in the core profile; no Docker fallback was attempted"; return 2;
  }
}

# Bounded metadata only. Never echo raw stderr, credentials or connection files.
ce_capture() (
  set -eu
  out="$(mktemp "${TMPDIR:-/tmp}/ce-runtime-out.XXXXXX")"
  err="$(mktemp "${TMPDIR:-/tmp}/ce-runtime-err.XXXXXX")"
  child=''
  cleanup() {
    if [[ -n "$child" ]] && kill -0 "$child" 2>/dev/null; then
      kill -TERM "$child" 2>/dev/null || true
      sleep 0.1
      kill -KILL "$child" 2>/dev/null || true
      wait "$child" 2>/dev/null || true
    fi
    rm -f "$out" "$err"
  }
  trap cleanup EXIT
  trap 'exit 130' INT TERM
  "$@" >"$out" 2>"$err" & child=$!
  started=$SECONDS
  while kill -0 "$child" 2>/dev/null; do
    if (( SECONDS - started >= 20 )) || (( $(wc -c < "$out") + $(wc -c < "$err") > 65536 )); then
      ce_runtime_fail 'metadata probe exceeded time/output limit'; exit 2
    fi
    sleep 0.1
  done
  code=0; wait "$child" || code=$?; child=''
  (( code == 0 )) || { ce_runtime_fail 'metadata probe failed; check the selected engine/connection'; exit 2; }
  (( $(wc -c < "$out") <= 16384 )) || { ce_runtime_fail 'metadata output too large'; exit 2; }
  cat "$out"
)

# Run only the captured executable/context. Do not let ambient selectors change it.
ce_engine() (
  ce_engine_exec "$@"
)

ce_engine_exec() {
  unset DOCKER_HOST DOCKER_CONTEXT CONTAINER_HOST CONTAINER_CONNECTION CONTAINER_SSHKEY
  exec "${CE_ENGINE_CMD[@]}" "$@"
}

ce_engine_init() {
  local endpoint metadata platform identity context_subject template machine verified_version version digest ssh_identity
  ce_engine_choice || return
  CE_ENGINE_BIN="$(command -v "$CE_ENGINE")" || { ce_runtime_fail "selected $CE_ENGINE executable is missing"; return 2; }
  [[ "$CE_ENGINE_BIN" == /* && -x "$CE_ENGINE_BIN" ]] || { ce_runtime_fail 'runtime executable must resolve to an absolute file'; return 2; }
  CE_ENGINE_CMD=("$CE_ENGINE_BIN")
  CE_ENGINE_CONTEXT=''
  CE_ENGINE_ROOTLESS=null
  if [[ "$CE_ENGINE" == docker ]]; then
    if [[ -n "${DOCKER_CONTEXT:-}" || -z "${DOCKER_HOST:-}" ]]; then
      CE_ENGINE_CONTEXT="${DOCKER_CONTEXT:-}"
      [[ -n "$CE_ENGINE_CONTEXT" ]] || CE_ENGINE_CONTEXT="$(ce_capture "$CE_ENGINE_BIN" context show)" || return
      [[ "$CE_ENGINE_CONTEXT" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$ ]] || { ce_runtime_fail 'unsupported Docker context name'; return 2; }
      endpoint="$(ce_capture "$CE_ENGINE_BIN" context inspect "$CE_ENGINE_CONTEXT" --format '{{.Endpoints.docker.Host}}')" || return
    else endpoint="$DOCKER_HOST"; fi
    [[ "$endpoint" == unix:///* && ${#endpoint} -le 1024 && "$endpoint" != *$'\n'* ]] || { ce_runtime_fail 'core profile requires a local Unix Docker endpoint'; return 2; }
    CE_ENGINE_CMD+=(--host "$endpoint")
    metadata="$(ce_capture ce_engine_exec version --format '{{.Client.Version}}|{{.Server.Version}}|{{.Server.Os}}|{{.Server.Arch}}|{{.Server.Platform.Name}}')" || return
    IFS='|' read -r CE_ENGINE_CLIENT_VERSION CE_ENGINE_SERVER_VERSION CE_ENGINE_OS CE_ENGINE_ARCH platform <<< "$metadata"
    [[ "$platform" == *Docker* ]] || { ce_runtime_fail 'Docker request did not identify a Docker backend'; return 2; }
    identity="$(ce_capture ce_engine_exec info --format '{{.ID}}')" || return
    context_subject="docker|$endpoint|$identity"
  else
    # Native Linux core never consults remote connection defaults.
    case "$(uname -s)" in
      Linux)
        [[ -z "${CE_PODMAN_CONNECTION:-}" ]] || { ce_runtime_fail 'Linux core uses native rootless Podman, not a remote connection'; return 2; }
        CE_ENGINE_CONTEXT=native-rootless
        CE_ENGINE_CMD+=(--remote=false)
        context_subject=podman-native-rootless
        ;;
      Darwin)
        CE_ENGINE_CONTEXT="${CE_PODMAN_CONNECTION:-}"
        [[ "$CE_ENGINE_CONTEXT" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$ ]] || { ce_runtime_fail 'macOS requires an explicit CE_PODMAN_CONNECTION machine name'; return 2; }
        template="{{range .}}{{if eq .Name \"$CE_ENGINE_CONTEXT\"}}{{.URI}}|{{.IsMachine}}|{{.Identity}}{{end}}{{end}}"
        metadata="$(ce_capture ce_engine_exec system connection list --format "$template")" || return
        [[ "$metadata" != *[[:cntrl:]]* ]] || { ce_runtime_fail 'invalid machine connection metadata'; return 2; }
        IFS='|' read -r endpoint machine ssh_identity <<< "$metadata"
        [[ "$machine" == true && "$endpoint" =~ ^ssh://[A-Za-z0-9._-]+@127\.0\.0\.1:[0-9]+/run/user/[0-9]+/podman/podman\.sock$ ]] || {
          ce_runtime_fail 'core macOS profile requires a loopback rootless Podman machine connection'; return 2;
        }
        [[ "$ssh_identity" == /* && -f "$ssh_identity" && -r "$ssh_identity" && ! -L "$ssh_identity" && "$ssh_identity" != *[[:cntrl:]]* && "$ssh_identity" != *'|'* ]] || {
          ce_runtime_fail 'machine SSH identity must be a readable absolute file'; return 2;
        }
        # Freeze endpoint and key path; never re-resolve a mutable connection name.
        CE_ENGINE_CMD+=(--url "$endpoint" --identity "$ssh_identity")
        context_subject="podman-machine|$endpoint"
        ;;
      *) ce_runtime_fail 'Podman core currently supports Linux or macOS profiles only'; return 2 ;;
    esac
    metadata="$(ce_capture ce_engine_exec info --format '{{.Host.Security.Rootless}}|{{.Host.Arch}}|{{.Version.Version}}|{{.Host.OS}}')" || return
    IFS='|' read -r CE_ENGINE_ROOTLESS CE_ENGINE_ARCH CE_ENGINE_SERVER_VERSION CE_ENGINE_OS <<< "$metadata"
    [[ "$CE_ENGINE_ROOTLESS" == true ]] || { ce_runtime_fail 'rootful or non-Podman backend is outside the core profile'; return 2; }
    metadata="$(ce_capture ce_engine_exec version --format '{{.Client.Version}}|{{.Server.Version}}')" || return
    IFS='|' read -r CE_ENGINE_CLIENT_VERSION verified_version <<< "$metadata"
    [[ "$verified_version" == "$CE_ENGINE_SERVER_VERSION" ]] || { ce_runtime_fail 'inconsistent Podman server identity'; return 2; }
    identity="$(ce_capture ce_engine_exec info --format '{{.Store.GraphRoot}}|{{.Host.Hostname}}')" || return
    [[ -n "$identity" && ${#identity} -le 2048 && "$identity" != *$'\n'* ]] || { ce_runtime_fail 'invalid Podman storage/host identity'; return 2; }
    context_subject="$context_subject|$(id -u)|$identity"
  fi
  for version in "$CE_ENGINE_CLIENT_VERSION" "$CE_ENGINE_SERVER_VERSION"; do
    [[ "$version" =~ ^[A-Za-z0-9][A-Za-z0-9.+_-]{0,79}$ ]] || { ce_runtime_fail 'invalid runtime version metadata'; return 2; }
  done
  [[ "$CE_ENGINE_OS" == linux && "$CE_ENGINE_ARCH" =~ ^[A-Za-z0-9_-]{1,30}$ ]] || { ce_runtime_fail 'invalid or unsupported server platform'; return 2; }
  context_subject="$context_subject|$CE_ENGINE_SERVER_VERSION|$CE_ENGINE_OS|$CE_ENGINE_ARCH|$CE_ENGINE_ROOTLESS"
  if command -v sha256sum >/dev/null; then digest="$(printf '%s' "$context_subject" | sha256sum)"
  else digest="$(printf '%s' "$context_subject" | shasum -a 256)"; fi
  CE_ENGINE_CONTEXT_HASH="${digest%% *}"
  [[ "$CE_ENGINE_CONTEXT_HASH" =~ ^[a-f0-9]{64}$ ]] || { ce_runtime_fail 'cannot fingerprint runtime context'; return 2; }
}

ce_image_ref() {
  local image="$1" first="${1%%/*}"
  if [[ "$CE_ENGINE" == docker ]]; then printf '%s\n' "$image"
  elif [[ "$image" == */* && ( "$first" == *.* || "$first" == *:* || "$first" == localhost ) ]]; then printf '%s\n' "$image"
  elif [[ "$image" == */* ]]; then printf 'docker.io/%s\n' "$image"
  else printf 'docker.io/library/%s\n' "$image"; fi
}
