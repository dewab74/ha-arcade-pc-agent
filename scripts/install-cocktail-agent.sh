#!/usr/bin/env bash
# Builds, tests and installs the Cocktail agent on a Windows PC over SSH.
#
#   cp .env.example .env    # once: fill in the PC's SSH login and the agent settings
#   bash scripts/install-cocktail-agent.sh
#
# 1. dotnet test, then dotnet publish (self-contained single exe, win-x64)
# 2. builds agent.json from the AGENT_* settings in .env
# 3. stages the exe, agent.json and the optional CA file on the PC
#    (C:\ProgramData\CocktailAgent\staging)
# 4. if AGENT_MQTT_PASSWORD_COMMAND is set, runs it and pipes its output over
#    SSH stdin into %LOCALAPPDATA%\CocktailAgent\agent.password.new, which the
#    installer DPAPI-protects and deletes (never a command line or a shared folder)
# 5. runs scripts/install-cocktail-agent.ps1 -FromStaging -ExpectedSha256 <sha>
#
# Needs: the .NET SDK, jq, sshpass (for scripts/cocktail-remote.sh), the PC
# reachable over SSH as its signed-in user (an administrator).
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REMOTE="${ROOT_DIR}/scripts/cocktail-remote.sh"
ENV_FILE="${ROOT_DIR}/.env"
VERSION="$(tr -d '[:space:]' <"${ROOT_DIR}/VERSION")"
OUT="${ROOT_DIR}/artifacts/publish"

[[ -f "${ENV_FILE}" ]] || {
  echo "No ${ENV_FILE}; copy .env.example and fill it in" >&2
  exit 1
}
set -a
# shellcheck disable=SC1090
source "${ENV_FILE}"
set +a
: "${AGENT_MQTT_HOST:?AGENT_MQTT_HOST is required in .env}"
: "${AGENT_MQTT_USERNAME:?AGENT_MQTT_USERNAME is required in .env}"
command -v jq >/dev/null || {
  echo 'jq is required' >&2
  exit 1
}
if [[ -n "${AGENT_CA_FILE:-}" && ! -f "${AGENT_CA_FILE}" ]]; then
  echo "AGENT_CA_FILE ${AGENT_CA_FILE} does not exist" >&2
  exit 1
fi

# From the repo root, so global.json (test runner, SDK roll-forward) applies.
cd "${ROOT_DIR}"
dotnet test tests/CocktailAgent.Core.Tests
rm -rf "${OUT}"
dotnet publish src/CocktailAgent -c Release -o "${OUT}" -p:Version="${VERSION}" --nologo
exe="${OUT}/CocktailAgent.exe"
sha="$(shasum -a 256 "${exe}" | awk '{print $1}')"
echo "Built CocktailAgent ${VERSION}: ${sha}"

# agent.json: unset optional settings are left out (the agent's defaults apply).
settings="$(mktemp -t agent-json)"
script="$(mktemp -t install-cocktail-agent).ps1"
trap 'rm -f "${settings}" "${script}"' EXIT
jq -n \
  --arg host "${AGENT_MQTT_HOST}" \
  --arg port "${AGENT_MQTT_PORT:-8883}" \
  --arg tls "${AGENT_MQTT_TLS:-true}" \
  --arg username "${AGENT_MQTT_USERNAME}" \
  --arg nodeId "${AGENT_NODE_ID:-}" \
  --arg deviceName "${AGENT_DEVICE_NAME:-}" \
  --arg suggestedArea "${AGENT_AREA:-}" \
  --arg launchBoxDir "${AGENT_LAUNCHBOX_DIR:-}" \
  --arg inputWatchDir "${AGENT_INPUT_WATCH_DIR:-}" \
  --arg muteEntityId "${AGENT_MUTE_ENTITY_ID:-}" \
  --arg volumeEntityId "${AGENT_VOLUME_ENTITY_ID:-}" \
  --arg nasShare "${AGENT_NAS_SHARE:-}" \
  '{host: $host, port: ($port | tonumber), useTls: ($tls == "true"), username: $username,
    nodeId: $nodeId, deviceName: $deviceName, suggestedArea: $suggestedArea,
    launchBoxDir: $launchBoxDir, inputWatchDir: $inputWatchDir,
    muteEntityId: $muteEntityId, volumeEntityId: $volumeEntityId, nasShare: $nasShare}
   | with_entries(select(.value != ""))' >"${settings}"

staging='C:/ProgramData/CocktailAgent/staging'
bash "${REMOTE}" ps-command -- 'Remove-Item C:\ProgramData\CocktailAgent\staging -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force C:\ProgramData\CocktailAgent\staging | Out-Null' >/dev/null
bash "${REMOTE}" put "${exe}" "${staging}/CocktailAgent.exe"
bash "${REMOTE}" put "${settings}" "${staging}/agent.json"
if [[ -n "${AGENT_CA_FILE:-}" ]]; then
  bash "${REMOTE}" put "${AGENT_CA_FILE}" "${staging}/ca.pem"
fi

if [[ -n "${AGENT_MQTT_PASSWORD_COMMAND:-}" ]]; then
  password="$(bash -c "${AGENT_MQTT_PASSWORD_COMMAND}")"
  [[ -n "${password}" ]] || {
    echo 'AGENT_MQTT_PASSWORD_COMMAND printed nothing' >&2
    exit 1
  }
  # The PC's SSH shell is PowerShell. One line on stdin -> the private file;
  # read back its length, never its value.
  written="$(
    printf '%s\n' "${password}" \
      | bash "${REMOTE}" exec -- \
        '$d = Join-Path $env:LOCALAPPDATA "CocktailAgent"; New-Item -ItemType Directory -Force $d | Out-Null; $f = Join-Path $d "agent.password.new"; [Console]::In.ReadLine() | Set-Content -NoNewline -Encoding ascii $f; (Get-Content $f -Raw).Length'
  )"
  written="$(printf '%s' "${written}" | tr -d '\r' | tail -n 1)"
  if [[ "${written}" != "${#password}" ]]; then
    echo "Password file on the PC has length '${written}', expected ${#password}" >&2
    exit 1
  fi
  unset password
fi

# Run the installer with its parameters: its param() block becomes a script block's.
{
  printf '& {\n'
  cat "${ROOT_DIR}/scripts/install-cocktail-agent.ps1"
  printf "\n} -FromStaging -ExpectedSha256 '%s'\n" "${sha}"
} >"${script}"
bash "${REMOTE}" ps-file "${script}"
