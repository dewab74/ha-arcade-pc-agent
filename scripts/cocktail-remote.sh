#!/usr/bin/env bash
# SSH helper for the Windows PC the agent runs on (its SSH shell is PowerShell).
# Keeps one control socket open so repeated commands reuse a single login.
# Reads COCKTAIL_HOST, COCKTAIL_USERNAME and, for password login (needs
# sshpass), COCKTAIL_PASSWORD from .env; leave COCKTAIL_PASSWORD empty to use
# your SSH keys. -F /dev/null keeps personal SSH config (jump hosts, aliases)
# out of it; host keys are trusted on first use and checked afterwards.

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENV_FILE="${ROOT_DIR}/.env"
SOCKET_DIR="${TMPDIR:-/tmp}/cocktail-agent-ssh"

if [[ -f "${ENV_FILE}" ]]; then
  set -a
  # shellcheck disable=SC1090
  source "${ENV_FILE}"
  set +a
fi

: "${COCKTAIL_HOST:?COCKTAIL_HOST is required}"
: "${COCKTAIL_USERNAME:?COCKTAIL_USERNAME is required}"

mkdir -p "${SOCKET_DIR}"
chmod 700 "${SOCKET_DIR}"

socket_key="$(printf '%s' "${COCKTAIL_USERNAME}@${COCKTAIL_HOST}:22" | shasum | awk '{print $1}')"
SOCKET_PATH="${SOCKET_DIR}/${socket_key}"

ssh_base_args=(
  -F /dev/null
  -o StrictHostKeyChecking=accept-new
  -o ServerAliveInterval=15
  -o ServerAliveCountMax=8
  -o ControlPath="${SOCKET_PATH}"
  "${COCKTAIL_USERNAME}@${COCKTAIL_HOST}"
)

if [[ -n "${COCKTAIL_PASSWORD:-}" ]]; then
  ssh_base_args=(-o PreferredAuthentications=password -o PubkeyAuthentication=no "${ssh_base_args[@]}")
  sshpass_cmd=(sshpass -p "${COCKTAIL_PASSWORD}")
else
  sshpass_cmd=()
fi

usage() {
  cat <<EOF
Usage:
  scripts/cocktail-remote.sh open
  scripts/cocktail-remote.sh status
  scripts/cocktail-remote.sh exec -- <remote command>
  scripts/cocktail-remote.sh ps-command -- <powershell command>
  scripts/cocktail-remote.sh ps-file <local-script.ps1>
  scripts/cocktail-remote.sh put <local-file> <remote-path>   (e.g. C:/ProgramData/CocktailAgent/x)
  scripts/cocktail-remote.sh close
EOF
}

socket_running() {
  ssh \
    -F /dev/null \
    -o ControlPath="${SOCKET_PATH}" \
    -O check \
    "${COCKTAIL_USERNAME}@${COCKTAIL_HOST}" >/dev/null 2>&1
}

ensure_open() {
  if socket_running; then
    return 0
  fi

  rm -f "${SOCKET_PATH}"

  ${sshpass_cmd[@]+"${sshpass_cmd[@]}"} ssh \
    "${ssh_base_args[@]}" \
    -o ControlMaster=yes \
    -o ControlPersist=1h \
    -Nf
}

cmd_open() {
  ensure_open
  printf 'Opened control socket: %s\n' "${SOCKET_PATH}"
}

cmd_status() {
  if socket_running; then
    printf 'Control socket is active: %s\n' "${SOCKET_PATH}"
  else
    printf 'Control socket is not active: %s\n' "${SOCKET_PATH}"
    return 1
  fi
}

cmd_exec() {
  [[ $# -gt 0 ]] || {
    usage
    exit 1
  }
  ensure_open
  ssh \
    "${ssh_base_args[@]}" \
    -o ControlMaster=auto \
    "$@"
}

cmd_ps_command() {
  local command_text
  local encoded_command
  [[ $# -gt 0 ]] || {
    usage
    exit 1
  }
  ensure_open
  command_text="$*"
  encoded_command="$(
    printf '%s' "${command_text}" \
      | iconv -f UTF-8 -t UTF-16LE \
      | base64 | tr -d '\n'
  )"
  ssh \
    "${ssh_base_args[@]}" \
    -o ControlMaster=auto \
    "powershell -NoLogo -NoProfile -EncodedCommand ${encoded_command}"
}

cmd_ps_file() {
  local script_path
  local encoded_command
  [[ $# -eq 1 ]] || {
    usage
    exit 1
  }
  script_path="$1"
  [[ -f "${script_path}" ]] || {
    printf 'Script not found: %s\n' "${script_path}" >&2
    exit 1
  }
  ensure_open
  encoded_command="$(
    iconv -f UTF-8 -t UTF-16LE "${script_path}" \
      | base64 | tr -d '\n'
  )"
  # Windows caps a command line at 32767 characters; past that ssh runs
  # nothing and exits quietly. Large scripts are copied over and run as a file.
  if ((${#encoded_command} > 30000)); then
    local remote_name
    remote_name="cocktail-remote-$$-$(basename "${script_path}")"
    scp -q -F /dev/null -o StrictHostKeyChecking=accept-new \
      -o ControlPath="${SOCKET_PATH}" \
      "${script_path}" "${COCKTAIL_USERNAME}@${COCKTAIL_HOST}:C:/Users/Public/${remote_name}"
    local rc=0
    ssh \
      "${ssh_base_args[@]}" \
      -o ControlMaster=auto \
      "powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File C:\\Users\\Public\\${remote_name}; \$rc = \$LASTEXITCODE; Remove-Item C:\\Users\\Public\\${remote_name}; exit \$rc" || rc=$?
    return "${rc}"
  fi
  ssh \
    "${ssh_base_args[@]}" \
    -o ControlMaster=auto \
    "powershell -NoLogo -NoProfile -EncodedCommand ${encoded_command}"
}

cmd_put() {
  [[ $# -eq 2 ]] || {
    usage
    exit 1
  }
  [[ -f "$1" ]] || {
    printf 'File not found: %s\n' "$1" >&2
    exit 1
  }
  ensure_open
  scp -q -F /dev/null -o StrictHostKeyChecking=accept-new \
    -o ControlPath="${SOCKET_PATH}" \
    "$1" "${COCKTAIL_USERNAME}@${COCKTAIL_HOST}:$2"
}

cmd_close() {
  if socket_running; then
    ssh -S "${SOCKET_PATH}" -O exit "${COCKTAIL_USERNAME}@${COCKTAIL_HOST}"
  else
    printf 'Control socket is not active: %s\n' "${SOCKET_PATH}"
  fi
}

subcommand="${1:-}"
case "${subcommand}" in
  open)
    shift
    cmd_open "$@"
    ;;
  status)
    shift
    cmd_status "$@"
    ;;
  exec)
    shift
    [[ "${1:-}" == "--" ]] && shift
    cmd_exec "$@"
    ;;
  ps-command)
    shift
    [[ "${1:-}" == "--" ]] && shift
    cmd_ps_command "$@"
    ;;
  ps-file)
    shift
    cmd_ps_file "$@"
    ;;
  put)
    shift
    cmd_put "$@"
    ;;
  close)
    shift
    cmd_close "$@"
    ;;
  *)
    usage
    exit 1
    ;;
esac
