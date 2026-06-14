#!/usr/bin/env bash
# Assert BR games-model tables/columns exist after migration replay.
set -euo pipefail

PGHOST="${PGHOST:-localhost}"
PGPORT="${PGPORT:-5432}"
PGUSER="${PGUSER:-postgres}"
PGDATABASE="${PGDATABASE:-esportra_test}"
export PGPASSWORD="${PGPASSWORD:-migrationtest}"

if ! command -v psql >/dev/null 2>&1; then
  echo "Installing postgresql-client..."
  sudo apt-get update -qq
  sudo apt-get install -y postgresql-client
fi

assert_exists() {
  local kind="$1"
  local name="$2"
  local sql="$3"
  local result
  result="$(psql -h "${PGHOST}" -p "${PGPORT}" -U "${PGUSER}" -d "${PGDATABASE}" -tAc "${sql}" | tr -d ' ')"
  if [[ "${result}" != "t" ]]; then
    echo "ERROR: expected ${kind} '${name}' to exist after migration replay" >&2
    exit 1
  fi
}

assert_exists "table" "br_games" \
  "SELECT to_regclass('public.br_games') IS NOT NULL"

assert_exists "column" "br_lobby_evidence.game_id" \
  "SELECT EXISTS (
     SELECT 1 FROM information_schema.columns
     WHERE table_schema = 'public'
       AND table_name = 'br_lobby_evidence'
       AND column_name = 'game_id'
   )"

assert_exists "column" "br_lobby_results.game_id" \
  "SELECT EXISTS (
     SELECT 1 FROM information_schema.columns
     WHERE table_schema = 'public'
       AND table_name = 'br_lobby_results'
       AND column_name = 'game_id'
   )"

echo "BR games model schema checks passed."
