# Production Schema Divergences

This file tracks known structural differences between the staging and production databases.
Each entry must include: which migration created the divergence, what the difference is,
and the target date by which production will be brought to parity.

**Rule:** Never create migrations with `_staging_repair` in the filename. If a divergence
exists, document it here instead. Both environments must converge to the same schema state.

---

## DIV-001 — tournament_invitations.ux_tournament_invitations_code

**Created by:** `20260617003000_tournament_invitations_staging_repair.sql`
**Divergence:** On staging, `ux_tournament_invitations_code` exists as a **standalone index**
(created by the staging-repair migration). On production, it exists as a
**constraint-backing index** (created by the original UNIQUE constraint DDL).

**Impact:** Staging has a standalone index; production has a UNIQUE constraint. Both enforce
uniqueness on `code`. The structural form differs but the behavior is identical.

**Target parity date:** To be resolved in a future dedicated migration if structural alignment
is needed. The `_staging_repair` migration that caused this divergence should not be repeated.

**Status:** PERSISTENT — ux_tournament_invitations_code was intentionally retained in
20260827000500 after review. No removal planned at this time.

---

## CI Fixtures

Production replay fixtures were last regenerated: **2026-09-06**
Regenerate after any migration that drops or renames a constraint or index:
```bash
PROD_SSH_KEY=~/.ssh/ssh-key-2026-04-04.key bash .github/scripts/dump-replay-schema.sh --env production
```
