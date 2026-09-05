---
name: external-schema-migrations
description: Verify external schema column names AND types before writing any migration that touches Hangfire, Supabase auth/storage, or other external schemas — blocking. Never assume snake_case or text types.
metadata:
  type: rule
---

## Mandatory pre-flight

Before writing ANY migration that touches a table in an external schema (`hangfire.*`, `auth.*`, `storage.*`, etc.):

1. **Look up the real schema — not from memory.** External systems often use different naming conventions and types than the rest of the codebase.
2. For **Hangfire**: check the embedded SQL migration scripts in the NuGet package cache:
   ```
   C:/Users/{user}/.nuget/packages/hangfire.postgresql/{version}/lib/netstandard2.0/Hangfire.PostgreSql.dll
   ```
   Or grep existing app code for queries against that table:
   ```
   grep -rn "hangfire\." --include="*.cs" src/
   ```
3. For **Supabase** (`auth.*`, `storage.*`): consult the baseline migration script or Supabase docs.

---

## Authoritative `hangfire.job` schema — v1.20.10

| Column | Type | Notes |
|--------|------|-------|
| `id` | `BIGINT` | PK |
| `stateid` | `BIGINT NULL` | FK → `hangfire.state.id` |
| `statename` | `TEXT NULL` | Was VARCHAR(20), widened in v12 |
| `invocationdata` | **`JSONB` NOT NULL** | Was TEXT, converted to JSONB in v20 |
| `arguments` | **`JSONB` NOT NULL** | Was TEXT, converted to JSONB in v20 |
| `createdat` | `TIMESTAMPTZ NOT NULL` | Was TIMESTAMP, changed in v19 |
| `expireat` | `TIMESTAMPTZ NULL` | Was TIMESTAMP, changed in v19 |
| `updatecount` | `INTEGER NOT NULL DEFAULT 0` | Added in v4 |

**Hangfire uses camelCase without underscores** — not snake_case like the rest of the codebase.

---

## Type traps

- **`JSONB` does not support `LIKE`** — cast explicitly: `invocationdata::text LIKE '...'`
- **`TIMESTAMPTZ` vs `TIMESTAMP`** — use `NOW()` (returns `TIMESTAMPTZ`) not `CURRENT_TIMESTAMP` for consistency
- **Never use `invocation_data`** — the column is `invocationdata` (no underscore)

---

## Why this rule exists

This migration (`20260827000000_delete_dead_invitation_expiry_job.sql`) failed three times in CI because the schema was assumed rather than verified:
1. `hangfire.job` didn't exist → assumed the schema was created by DbUp (it's created by the API on startup)
2. `invocation_data` wrong name → assumed snake_case
3. `invocationdata` wrong type → assumed TEXT, it's JSONB since Hangfire v20

All three failures were avoidable by reading the package schema first.
