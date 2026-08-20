#!/usr/bin/env bash
# Apply CI post-baseline replay fixture before Esportra.Migrator.
#
# 1. post-baseline-replay-schema.sql — DB state after pre-baseline migrations
# 2. replay-journal-seed.sql — marks those scripts as already applied in DbUp
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SCHEMA="${ROOT}/.github/ci/post-baseline-replay-schema.sql"
JOURNAL="${ROOT}/.github/ci/replay-journal-seed.sql"

PGHOST="${PGHOST:-localhost}"
PGPORT="${PGPORT:-5432}"
PGUSER="${PGUSER:-postgres}"
PGDATABASE="${PGDATABASE:-esportra_test}"
export PGPASSWORD="${PGPASSWORD:-migrationtest}"

if [[ ! -f "${SCHEMA}" ]]; then
  echo "ERROR: Missing replay schema SQL: ${SCHEMA}" >&2
  echo "Regenerate: bash .github/scripts/dump-replay-schema.sh" >&2
  exit 1
fi

if [[ ! -f "${JOURNAL}" ]]; then
  echo "ERROR: Missing replay journal seed: ${JOURNAL}" >&2
  echo "Regenerate: bash .github/scripts/dump-replay-schema.sh" >&2
  exit 1
fi

if ! command -v psql >/dev/null 2>&1; then
  echo "Installing postgresql-client..."
  sudo apt-get update -qq
  sudo apt-get install -y postgresql-client
fi

echo "Applying post-baseline replay schema to ${PGUSER}@${PGHOST}:${PGPORT}/${PGDATABASE}"
psql -h "${PGHOST}" -p "${PGPORT}" -U "${PGUSER}" -d "${PGDATABASE}" \
  -v ON_ERROR_STOP=1 -f "${SCHEMA}"

echo "Seeding DbUp journal (pre-baseline scripts marked applied)"
psql -h "${PGHOST}" -p "${PGPORT}" -U "${PGUSER}" -d "${PGDATABASE}" \
  -v ON_ERROR_STOP=1 -f "${JOURNAL}"

echo "Replay fixture applied."
