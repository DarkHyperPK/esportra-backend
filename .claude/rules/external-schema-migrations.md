---
name: external-schema-migrations
description: Verify actual column names before writing any migration that touches Hangfire, Supabase auth/storage, or other external schemas — blocking
metadata:
  type: rule
---

When a migration touches a table owned by an external library or system, column names MUST be verified against the real schema. Do not assume snake_case.

## Known external schemas

**Hangfire (`hangfire.*`)** — camelCase without underscores:
- `hangfire.job`: `id`, `stateid`, `statename`, `invocationdata`, `arguments`, `createdat`, `expireat`, `updatedat`
- `hangfire.state`: `id`, `jobid`, `name`, `reason`, `createdat`, `data`

**Supabase (`auth.*`, `storage.*`)** — snake_case, consistent with Postgres conventions.

## How to verify before writing

1. Grep existing app code for references to the table:
   ```
   grep -r "hangfire\.job" --include="*.cs" src/
   ```
2. If no existing queries exist, check the library's migration source or docs.
3. Never assume a column name from its snake_case equivalent — use the exact name the library created.

**Why:** External schemas are outside our control and often violate our project conventions. A wrong column name passes `dotnet build`, passes `dotnet test`, and only fails at runtime when the migrator executes against a real database — crashing the backend on deploy.
