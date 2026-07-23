#!/usr/bin/env bash
# Assert CI migration replay journaled every embedded DbUp script.
#
# Logic: every .sql file in Scripts/ must have a matching schemaversions entry.
# The journal may contain extra entries (historically applied scripts that were
# later deleted from the repo, e.g. removed season feature). That's expected.
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
MISSING_FILE="$(mktemp)"
trap 'rm -f "${EXPECTED_FILE}" "${ACTUAL_FILE}" "${MISSING_FILE}"' EXIT

# What we expect: every .sql file on disk
find "${SCRIPTS_DIR}" -maxdepth 1 -name '*.sql' -printf '%f\n' | sort > "${EXPECTED_FILE}"

# What's in the journal (strip the DbUp namespace prefix)
psql -h "${PGHOST}" -p "${PGPORT}" -U "${PGUSER}" -d "${PGDATABASE}" -tA \
  -c "SELECT regexp_replace(scriptname, '^Esportra\.Infrastructure\.Migrations\.Scripts\.', '') FROM schemaversions ORDER BY 1;" \
  | sed '/^[[:space:]]*$/d' | sort > "${ACTUAL_FILE}"

# Check: every file on disk must be in the journal
comm -23 "${EXPECTED_FILE}" "${ACTUAL_FILE}" > "${MISSING_FILE}"

if [[ -s "${MISSING_FILE}" ]]; then
  echo "ERROR: The following migration scripts were NOT applied:" >&2
  cat "${MISSING_FILE}" >&2
  echo "" >&2
  echo "Expected $(wc -l < "${EXPECTED_FILE}" | tr -d ' ') scripts in journal, found $(wc -l < "${ACTUAL_FILE}" | tr -d ' ')." >&2
  exit 1
fi

DISK_COUNT="$(wc -l < "${EXPECTED_FILE}" | tr -d ' ')"
JOURNAL_COUNT="$(wc -l < "${ACTUAL_FILE}" | tr -d ' ')"
echo "Migration replay complete: all ${DISK_COUNT} scripts journaled (${JOURNAL_COUNT} total entries including historical)."
