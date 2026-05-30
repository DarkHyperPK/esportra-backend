#!/usr/bin/env bash
# Validate API health before promote/smoke on backend deploy workflows.
set -euo pipefail

require() {
  local name="$1"
  if [[ -z "${!name:-}" ]]; then
    echo "::error title=Missing secret::${name} is not set in GitHub repository secrets."
    missing+=("$name")
  fi
}

missing=()
require API_URL

if ((${#missing[@]} > 0)); then
  printf 'Configure: %s\n' "${missing[@]}"
  exit 1
fi

API_URL="${API_URL%/}"
echo "Preflight: ${API_URL}/health/ready"
if ! curl --fail --silent --show-error --max-time 30 "${API_URL}/health/ready" >/dev/null; then
  echo "::error title=API unreachable::${API_URL}/health/ready did not respond."
  exit 1
fi

echo "API preflight passed."
