#!/usr/bin/env bash
# Local reproduction of CI post-baseline migration replay.
#
# Usage:
#   bash .github/scripts/run-migration-replay-local.sh
#   bash .github/scripts/run-migration-replay-local.sh --keep-db
#   bash .github/scripts/run-migration-replay-local.sh --skip-build
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CONTAINER="${ESPORTA_REPLAY_CONTAINER:-esportra-migration-replay}"
PGPORT="${ESPORTA_REPLAY_PORT:-5433}"
DOCKER="${DOCKER:-docker}"
if ! docker info >/dev/null 2>&1; then
  if sudo docker info >/dev/null 2>&1; then
    DOCKER="sudo docker"
  fi
fi
KEEP_DB=false
SKIP_BUILD=false

for arg in "$@"; do
  case "${arg}" in
    --keep-db) KEEP_DB=true ;;
    --skip-build) SKIP_BUILD=true ;;
    *)
      echo "Unknown option: ${arg}" >&2
      echo "Usage: $0 [--keep-db] [--skip-build]" >&2
      exit 1
      ;;
  esac
done

cleanup() {
  if [[ "${KEEP_DB}" == "false" ]]; then
    ${DOCKER} rm -f "${CONTAINER}" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

if ! command -v docker >/dev/null 2>&1 && ! sudo docker info >/dev/null 2>&1; then
  echo "ERROR: docker is required for local migration replay." >&2
  exit 1
fi

${DOCKER} rm -f "${CONTAINER}" >/dev/null 2>&1 || true
${DOCKER} run -d --name "${CONTAINER}" \
  -e POSTGRES_USER=postgres \
  -e POSTGRES_PASSWORD=migrationtest \
  -e POSTGRES_DB=esportra_test \
  -p "${PGPORT}:5432" \
  postgres:16 >/dev/null

echo "Waiting for Postgres on port ${PGPORT}..."
for _ in $(seq 1 30); do
  if ${DOCKER} exec "${CONTAINER}" pg_isready -U postgres >/dev/null 2>&1; then
    break
  fi
  sleep 1
done

if ! ${DOCKER} exec "${CONTAINER}" pg_isready -U postgres >/dev/null 2>&1; then
  echo "ERROR: Postgres did not become ready." >&2
  exit 1
fi

export PGHOST=localhost
export PGPORT="${PGPORT}"
export PGUSER=postgres
export PGDATABASE=esportra_test
export PGPASSWORD=migrationtest

echo "Applying replay fixture..."
bash "${ROOT}/.github/scripts/apply-replay-bootstrap.sh"

if [[ "${SKIP_BUILD}" == "false" ]]; then
  dotnet restore "${ROOT}/src/Esportra.Migrator/Esportra.Migrator.csproj"
  dotnet build "${ROOT}/src/Esportra.Migrator/Esportra.Migrator.csproj" -c Release --no-restore
fi

echo "Running Esportra.Migrator..."
export ConnectionStrings__Postgres="Host=${PGHOST};Port=${PGPORT};Database=${PGDATABASE};Username=${PGUSER};Password=${PGPASSWORD};Trust Server Certificate=true;"
dotnet run --project "${ROOT}/src/Esportra.Migrator/Esportra.Migrator.csproj" -c Release --no-build

bash "${ROOT}/.github/scripts/assert-migration-replay-complete.sh"
echo "Local migration replay succeeded."
