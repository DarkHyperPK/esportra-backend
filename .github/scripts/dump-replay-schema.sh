#!/usr/bin/env bash
# Regenerate CI replay fixture from the staging database.
#
# Usage:
#   SSH_KEY=~/.ssh/esportra.key bash .github/scripts/dump-replay-schema.sh
#
# Requires SSH access to the staging host. Replaces:
#   - .github/ci/post-baseline-replay-schema.sql
#   - .github/ci/replay-journal-seed.sql
#
# After running, commit the updated files.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SSH_KEY="${SSH_KEY:?Set SSH_KEY to your staging SSH key path}"
SSH_HOST="${SSH_HOST:-ubuntu@79.72.48.169}"
DB_CONTAINER="${DB_CONTAINER:-supabase-db-usoocgow4s0wow00gsw04kcg}"

SCHEMA_OUT="${ROOT}/.github/ci/post-baseline-replay-schema.sql"
JOURNAL_OUT="${ROOT}/.github/ci/replay-journal-seed.sql"

echo "Dumping public schema from staging..."
ssh -i "${SSH_KEY}" -o StrictHostKeyChecking=no "${SSH_HOST}" \
  "sudo docker exec ${DB_CONTAINER} pg_dump -U postgres -d postgres \
    --schema-only --no-owner --no-privileges --no-comments --schema=public" \
  > /tmp/staging_public_raw.sql

echo "Fetching applied migration journal..."
ssh -i "${SSH_KEY}" -o StrictHostKeyChecking=no "${SSH_HOST}" \
  "sudo docker exec ${DB_CONTAINER} psql -U postgres -d postgres -t -A \
    -c \"SELECT scriptname FROM schemaversions ORDER BY scriptname;\"" \
  > /tmp/staging_scripts.txt

# ── Build replay schema ───────────────────────────────────────────────────────
cat > "${SCHEMA_OUT}" << 'HEADER'
-- CI post-baseline replay schema — auto-generated from staging pg_dump.
-- DO NOT EDIT BY HAND. Regenerate with: bash .github/scripts/dump-replay-schema.sh

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

-- Public schema from staging pg_dump:
HEADER

grep -v "^--\|^SET \|^SELECT pg_catalog\|^ALTER.*OWNER TO\|^GRANT\|^REVOKE\|^\\\\connect\|^CREATE SCHEMA public\|^CREATE SCHEMA IF NOT EXISTS public" \
  /tmp/staging_public_raw.sql >> "${SCHEMA_OUT}"

echo "Wrote ${SCHEMA_OUT} ($(wc -l < "${SCHEMA_OUT}") lines)"

# ── Build journal seed ────────────────────────────────────────────────────────
cat > "${JOURNAL_OUT}" << 'HEADER'
-- CI replay journal seed — marks all staging-applied scripts as already run.
-- DO NOT EDIT BY HAND. Regenerate with: bash .github/scripts/dump-replay-schema.sh

CREATE TABLE IF NOT EXISTS schemaversions (
  schemaversionid SERIAL PRIMARY KEY,
  scriptname VARCHAR(512) NOT NULL,
  applied TIMESTAMPTZ NOT NULL DEFAULT now()
);

HEADER

while IFS= read -r script; do
  [ -z "${script}" ] && continue
  echo "INSERT INTO schemaversions (scriptname, applied) SELECT '${script}', now() WHERE NOT EXISTS (SELECT 1 FROM schemaversions WHERE scriptname = '${script}');" >> "${JOURNAL_OUT}"
done < /tmp/staging_scripts.txt

echo "" >> "${JOURNAL_OUT}"
echo "SELECT 'replay journal seed applied (' || count(*) || ' scripts)' AS status FROM schemaversions;" >> "${JOURNAL_OUT}"

SCRIPT_COUNT=$(grep -c "INSERT INTO" "${JOURNAL_OUT}")
echo "Wrote ${JOURNAL_OUT} (${SCRIPT_COUNT} scripts)"
echo ""
echo "Done. Commit the updated files:"
echo "  git add .github/ci/post-baseline-replay-schema.sql .github/ci/replay-journal-seed.sql"
