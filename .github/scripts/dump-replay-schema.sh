#!/usr/bin/env bash
# Regenerate CI replay fixture from the staging or production database.
#
# Usage:
#   SSH_KEY=~/.ssh/esportra.key bash .github/scripts/dump-replay-schema.sh [--env staging|production]
#
# Regeneration procedure:
#   STAGING:    SSH_KEY=~/.ssh/esportra.key bash .github/scripts/dump-replay-schema.sh --env staging
#   PRODUCTION: PROD_SSH_KEY=~/.ssh/esportra-prod.key PROD_SSH_HOST=ubuntu@<PROD_HOST> \
#               bash .github/scripts/dump-replay-schema.sh --env production
#
# When to regenerate:
#   - After any migration that drops or renames a constraint or index
#   - After any structural schema change that differs between environments
#   - Commit the updated files after regeneration
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
STAGING_DB_CONTAINER="${STAGING_DB_CONTAINER:-supabase-db-usoocgow4s0wow00gsw04kcg}"
PROD_DB_CONTAINER="${PROD_DB_CONTAINER:-supabase-db-moooksg0ssk04cg8coksgowc}"

# ── Parse --env flag ──────────────────────────────────────────────────────────
ENV="staging"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --env)
      shift
      ENV="${1:?--env requires a value: staging or production}"
      ;;
    *)
      echo "Unknown argument: $1" >&2
      echo "Usage: $0 [--env staging|production]" >&2
      exit 1
      ;;
  esac
  shift
done

if [[ "${ENV}" != "staging" && "${ENV}" != "production" ]]; then
  echo "ERROR: --env must be 'staging' or 'production', got '${ENV}'" >&2
  exit 1
fi

# ── Resolve env-specific settings ────────────────────────────────────────────
if [[ "${ENV}" == "staging" ]]; then
  SSH_KEY="${SSH_KEY:?Set SSH_KEY to your staging SSH key path}"
  SSH_HOST="${SSH_HOST:-ubuntu@79.72.48.169}"
  DB_CONTAINER="${STAGING_DB_CONTAINER}"
  SCHEMA_OUT="${ROOT}/.github/ci/post-baseline-replay-schema.sql"
  JOURNAL_OUT="${ROOT}/.github/ci/replay-journal-seed.sql"
  SCHEMA_HEADER_COMMENT="-- CI post-baseline replay schema — auto-generated from staging pg_dump."
  JOURNAL_HEADER_COMMENT="-- CI replay journal seed — marks all staging-applied scripts as already run."
  JOURNAL_STATUS_MSG="replay journal seed applied"
  COMMIT_MSG="  git add .github/ci/post-baseline-replay-schema.sql .github/ci/replay-journal-seed.sql"
else
  SSH_KEY="${PROD_SSH_KEY:?Set PROD_SSH_KEY to your production SSH key path}"
  SSH_HOST="${PROD_SSH_HOST:-ubuntu@79.72.48.169}"
  DB_CONTAINER="${PROD_DB_CONTAINER}"
  SCHEMA_OUT="${ROOT}/.github/ci/post-baseline-prod-replay-schema.sql"
  JOURNAL_OUT="${ROOT}/.github/ci/replay-prod-journal-seed.sql"
  SCHEMA_HEADER_COMMENT="-- CI production post-baseline replay schema — auto-generated from production pg_dump."
  JOURNAL_HEADER_COMMENT="-- CI production replay journal seed — marks all production-applied scripts as already run."
  JOURNAL_STATUS_MSG="replay-prod journal seed applied"
  COMMIT_MSG="  git add .github/ci/post-baseline-prod-replay-schema.sql .github/ci/replay-prod-journal-seed.sql"
fi

# ── Dump schema and journal from remote ──────────────────────────────────────
echo "Dumping public schema from ${ENV} (${SSH_HOST})..."
ssh -i "${SSH_KEY}" -o StrictHostKeyChecking=no "${SSH_HOST}" \
  "sudo docker exec ${DB_CONTAINER} pg_dump -U postgres -d postgres \
    --schema-only --no-owner --no-privileges --no-comments --schema=public" \
  > /tmp/${ENV}_public_raw.sql

echo "Fetching applied migration journal from ${ENV}..."
ssh -i "${SSH_KEY}" -o StrictHostKeyChecking=no "${SSH_HOST}" \
  "sudo docker exec ${DB_CONTAINER} psql -U postgres -d postgres -t -A \
    -c \"SELECT scriptname FROM schemaversions ORDER BY scriptname;\"" \
  > /tmp/${ENV}_scripts.txt

# ── Build replay schema ───────────────────────────────────────────────────────
cat > "${SCHEMA_OUT}" << HEADER
${SCHEMA_HEADER_COMMENT}
-- DO NOT EDIT BY HAND. Regenerate with: bash .github/scripts/dump-replay-schema.sh --env ${ENV}

CREATE SCHEMA IF NOT EXISTS auth;
CREATE SCHEMA IF NOT EXISTS storage;
CREATE SCHEMA IF NOT EXISTS extensions;

CREATE EXTENSION IF NOT EXISTS pgcrypto WITH SCHEMA extensions;
CREATE EXTENSION IF NOT EXISTS "uuid-ossp" WITH SCHEMA extensions;

-- Supabase roles referenced by RLS policies
DO \$\$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'anon') THEN
    CREATE ROLE anon NOLOGIN;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticated') THEN
    CREATE ROLE authenticated NOLOGIN;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'service_role') THEN
    CREATE ROLE service_role NOLOGIN BYPASSRLS;
  END IF;
END \$\$;

-- Disable function body validation during restore (functions may reference
-- tables that appear later in the dump due to pg_dump ordering limitations).
SET check_function_bodies = off;

-- auth.users stub (FK target for public tables)
CREATE TABLE IF NOT EXISTS auth.users (
    id uuid NOT NULL PRIMARY KEY,
    email character varying(255),
    created_at timestamp with time zone,
    deleted_at timestamp with time zone
);

-- Public schema from ${ENV} pg_dump:
HEADER

grep -v "^--\|^SET \|^SELECT pg_catalog\|^ALTER.*OWNER TO\|^GRANT\|^REVOKE\|^\\\\connect\|^CREATE SCHEMA public\|^CREATE SCHEMA IF NOT EXISTS public" \
  /tmp/${ENV}_public_raw.sql >> "${SCHEMA_OUT}"

echo "Wrote ${SCHEMA_OUT} ($(wc -l < "${SCHEMA_OUT}") lines)"

# ── Build journal seed ────────────────────────────────────────────────────────
cat > "${JOURNAL_OUT}" << HEADER
${JOURNAL_HEADER_COMMENT}
-- DO NOT EDIT BY HAND. Regenerate with: bash .github/scripts/dump-replay-schema.sh --env ${ENV}

CREATE TABLE IF NOT EXISTS schemaversions (
  schemaversionid SERIAL PRIMARY KEY,
  scriptname VARCHAR(512) NOT NULL,
  applied TIMESTAMPTZ NOT NULL DEFAULT now()
);

HEADER

while IFS= read -r script; do
  [ -z "${script}" ] && continue
  echo "INSERT INTO schemaversions (scriptname, applied) SELECT '${script}', now() WHERE NOT EXISTS (SELECT 1 FROM schemaversions WHERE scriptname = '${script}');" >> "${JOURNAL_OUT}"
done < /tmp/${ENV}_scripts.txt

echo "" >> "${JOURNAL_OUT}"
echo "SELECT '${JOURNAL_STATUS_MSG} (' || count(*) || ' scripts)' AS status FROM schemaversions;" >> "${JOURNAL_OUT}"

SCRIPT_COUNT=$(grep -c "INSERT INTO" "${JOURNAL_OUT}")
echo "Wrote ${JOURNAL_OUT} (${SCRIPT_COUNT} scripts)"
echo ""
echo "Done. Commit the updated files:"
echo "${COMMIT_MSG}"
