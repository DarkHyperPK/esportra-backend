# Database Migrations

This project uses [DbUp](https://dbup.readthedocs.io/) for PostgreSQL schema migrations.

## How It Works

- SQL migration scripts live in `src/Esportra.Infrastructure/Migrations/Scripts/`
- They are embedded in the assembly and run **automatically on app startup**
- A `schemaversions` table in the database tracks which scripts have been applied
- Each script runs exactly once, in filename order, inside its own transaction
- If a migration fails, the app **will not start** (fail-fast)

## Adding a New Migration

1. Create a new `.sql` file in `src/Esportra.Infrastructure/Migrations/Scripts/`:
   ```
   YYYYMMDD_NNN_short_description.sql
   ```
   Example: `20260318_001_add_match_notes_column.sql`

2. Write your DDL/DML:
   ```sql
   ALTER TABLE match_result_reports ADD COLUMN notes TEXT;
   ```

3. Build, test locally, commit, push.

## Rules

| Rule | Why |
|------|-----|
| **Never edit an existing migration file** | Other environments have already run it. Changing it won't re-run. |
| **Never rename a migration file** | DbUp tracks by filename. Renaming = re-running. |
| **Never delete a migration file** | It will look like it was never applied in new environments. |
| **Keep migrations small and focused** | One concern per file. Easier to debug failures. |
| **Use `IF NOT EXISTS` guards where sensible** | Makes scripts safer if run manually. |
| **Test on staging before production** | Staging deploys auto-apply first. |

## Rollbacks

There is no automatic rollback. If a migration needs to be undone, add a **new** migration that reverses it:
```
20260318_002_revert_add_match_notes_column.sql
```
```sql
ALTER TABLE match_result_reports DROP COLUMN IF EXISTS notes;
```

## Naming Convention

```
YYYYMMDD_NNN_description.sql
```
- `YYYYMMDD` — date the migration was created
- `NNN` — sequential number for that day (001, 002, ...)
- `description` — snake_case short description

## Deployment Flow

```
Developer adds .sql → push to staging → Coolify redeploys → 
migrations auto-run → test on staging → merge to main → 
Coolify deploys production → same migrations auto-run
```
