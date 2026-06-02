#!/usr/bin/env bash
# One-time maintainer script: build the post-baseline CI replay schema snapshot.
#
# Applies the Supabase stub + all pre-baseline migration SQL (through baseline
# inclusive), then pg_dump --schema-only into .github/ci/post-baseline-replay-schema.sql.
#
# Requires: psql, pg_dump, a running Postgres 16 instance.
#
# Example (local Docker):
#   docker run -d --name esportra-baseline-build -e POSTGRES_PASSWORD=migrationtest \
#     -p 5433:5432 postgres:16
#   PGPASSWORD=migrationtest PGPORT=5433 bash .github/scripts/build-post-baseline-schema.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
STUB="${ROOT}/.github/ci/supabase-replay-bootstrap.sql"
SCRIPTS="${ROOT}/src/Esportra.Infrastructure/Migrations/Scripts"
OUTPUT="${ROOT}/.github/ci/post-baseline-replay-schema.sql"
BASELINE="20260317_001_baseline.sql"

PGHOST="${PGHOST:-localhost}"
PGPORT="${PGPORT:-5432}"
PGUSER="${PGUSER:-postgres}"
PGDATABASE="${PGDATABASE:-esportra_baseline_build}"
export PGPASSWORD="${PGPASSWORD:-migrationtest}"

if [[ ! -f "${STUB}" ]]; then
  echo "ERROR: Missing stub SQL: ${STUB}" >&2
  exit 1
fi

if ! command -v psql >/dev/null 2>&1; then
  echo "ERROR: psql not found" >&2
  exit 1
fi

if ! command -v pg_dump >/dev/null 2>&1; then
  echo "ERROR: pg_dump not found" >&2
  exit 1
fi

echo "Ensuring database ${PGDATABASE} exists..."
if ! psql -h "${PGHOST}" -p "${PGPORT}" -U "${PGUSER}" -d postgres -tc \
  "SELECT 1 FROM pg_database WHERE datname = '${PGDATABASE}'" | grep -q 1; then
  psql -h "${PGHOST}" -p "${PGPORT}" -U "${PGUSER}" -d postgres \
    -c "CREATE DATABASE ${PGDATABASE}"
fi

echo "Applying Supabase replay stub..."
psql -h "${PGHOST}" -p "${PGPORT}" -U "${PGUSER}" -d "${PGDATABASE}" \
  -v ON_ERROR_STOP=1 -f "${STUB}"

OVERLAYS="${ROOT}/.github/ci/replay-legacy-overlays.sql"
if [[ ! -f "${OVERLAYS}" ]]; then
  echo "ERROR: Missing legacy overlays: ${OVERLAYS}" >&2
  exit 1
fi

echo "Applying legacy replay overlays..."
psql -h "${PGHOST}" -p "${PGPORT}" -U "${PGUSER}" -d "${PGDATABASE}" \
  -v ON_ERROR_STOP=1 -f "${OVERLAYS}"

echo "Applying pre-baseline migrations (through ${BASELINE})..."
while IFS= read -r -d '' f; do
  base="$(basename "${f}")"
  echo "  -> ${base}"
  psql -h "${PGHOST}" -p "${PGPORT}" -U "${PGUSER}" -d "${PGDATABASE}" \
    -v ON_ERROR_STOP=1 -f "${f}"
  if [[ "${base}" == "${BASELINE}" ]]; then
    break
  fi
done < <(find "${SCRIPTS}" -maxdepth 1 -name '*.sql' -print0 | sort -z)

echo "Dumping schema-only snapshot (excluding schemaversions)..."
pg_dump -h "${PGHOST}" -p "${PGPORT}" -U "${PGUSER}" -d "${PGDATABASE}" \
  --schema-only --no-owner --no-acl \
  --exclude-table=schemaversions \
  > "${OUTPUT}"

echo "Wrote ${OUTPUT} ($(wc -c < "${OUTPUT}") bytes)"
