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

EXPECTED_FILE="$(mktemp)"
ACTUAL_FILE="$(mktemp)"
trap 'rm -f "${EXPECTED_FILE}" "${ACTUAL_FILE}"' EXIT

find "${SCRIPTS_DIR}" -maxdepth 1 -name '*.sql' -printf '%f\n' | sort > "${EXPECTED_FILE}"
psql -h "${PGHOST}" -p "${PGPORT}" -U "${PGUSER}" -d "${PGDATABASE}" -tAc \
  'SELECT regexp_replace(scriptname, ''^.*\.'', '''') FROM schemaversions ORDER BY 1;' \
  | sed '/^[[:space:]]*$/d' | sort > "${ACTUAL_FILE}"

if ! diff -u "${EXPECTED_FILE}" "${ACTUAL_FILE}"; then
  echo "ERROR: schemaversions does not exactly match migration scripts" >&2
  exit 1
fi

ACTUAL="$(wc -l < "${ACTUAL_FILE}" | tr -d ' ')"
echo "Migration replay complete: ${ACTUAL} exact scripts journaled."
