#!/usr/bin/env bash
# Apply Supabase-shaped bootstrap before Esportra.Migrator in CI replay jobs.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
BOOTSTRAP="${ROOT}/.github/ci/supabase-replay-bootstrap.sql"

if [[ ! -f "${BOOTSTRAP}" ]]; then
  echo "ERROR: Missing replay bootstrap SQL: ${BOOTSTRAP}" >&2
  exit 1
fi

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

echo "Applying replay bootstrap to ${PGUSER}@${PGHOST}:${PGPORT}/${PGDATABASE}"
psql -h "${PGHOST}" -p "${PGPORT}" -U "${PGUSER}" -d "${PGDATABASE}" -v ON_ERROR_STOP=1 -f "${BOOTSTRAP}"
