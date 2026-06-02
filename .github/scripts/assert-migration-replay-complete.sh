#!/usr/bin/env bash
# Assert CI migration replay journaled every embedded DbUp script.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SCRIPTS_DIR="${ROOT}/src/Esportra.Infrastructure/Migrations/Scripts"

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

EXPECTED="$(find "${SCRIPTS_DIR}" -maxdepth 1 -name '*.sql' | wc -l | tr -d ' ')"
ACTUAL="$(psql -h "${PGHOST}" -p "${PGPORT}" -U "${PGUSER}" -d "${PGDATABASE}" -tAc \
  'SELECT COUNT(*) FROM schemaversions;' | tr -d ' ')"

if [[ "${ACTUAL}" != "${EXPECTED}" ]]; then
  echo "ERROR: schemaversions count mismatch (expected ${EXPECTED}, got ${ACTUAL})" >&2
  exit 1
fi

echo "Migration replay complete: ${ACTUAL}/${EXPECTED} scripts journaled."
