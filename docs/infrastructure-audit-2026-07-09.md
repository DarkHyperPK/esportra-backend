# Infrastructure Audit Report — esportra-vcn (79.72.48.169)

## Migration CI Replay Status

**Status: TEMPORARILY DISABLED** — CI fixtures incomplete.

The migration replay jobs were added to CI but are temporarily disabled because the CI fixtures are missing many legacy tables and columns that exist in prod/staging but were never created via DbUp migrations (they existed in Supabase before tracking started).

### What's Missing

| Table/Column | Issue |
|--------------|-------|
| `tournaments.settings` | Column exists in prod, not in migrations |
| `br_rounds` | Legacy table dropped by `20260611120000`, needs stub |
| `br_round_results` | Renamed to `br_lobby_results`, needs legacy version |
| `br_round_evidence` | Renamed to `br_lobby_evidence`, needs legacy version |
| Many more... | DB evolved organically in Supabase |

### Migrations Fixed

1. `20260611120000_br_pro_lobby_model.sql` — removed reference to non-existent `r.map` column
2. `20260622110000_br_round_map.sql` — made conditional (skip if `br_rounds` dropped)
3. `20260622120000_br_round_evidence_staging_repair.sql` — made conditional
4. `20260609180000_match_result_reports_upsert_key.sql` — renamed to fix ordering

### To Re-enable Migration Replay

1. Dump all columns from staging: compare against CI fixtures
2. Add missing stubs to `.github/ci/replay-legacy-overlays.sql`
3. Run `python .github/scripts/generate-post-baseline-replay-schema.py`
4. Remove `if: false` from migration-replay jobs in workflows
5. Test locally: `bash .github/scripts/run-migration-replay-local.sh`

---

## Executive Summary

**Overall Status:** System is healthy but has **1 CRITICAL issue** and several recommendations.

---

## CRITICAL — Fix Immediately

### Staging API Crash Loop
**Container:** `cgs8kgkwwc4g88w0oww4s4kw-192724095452` (api-staging.esportra.com)
**Status:** Restarting every ~30 seconds

**Root Cause:** The **committed** migration files use wrong column names. You have **local fixes** that aren't committed yet.

**The Problem:**
```sql
-- COMMITTED (broken):
INSERT INTO admin_permissions (id, key, name, description, category)
VALUES (gen_random_uuid(), 'feature_flags:view', 'View Feature Flags', ...);

-- LOCAL FIX (correct):
INSERT INTO admin_permissions (name, description, resource, action)
VALUES ('feature_flags:view', 'View feature flags...', 'feature_flags', 'view');
```

The `admin_permissions` table has `name`, not `key`. The committed code references a non-existent column.

**Files with uncommitted fixes:**
- `src/Esportra.Infrastructure/Migrations/Scripts/20260708100000_feature_flags.sql`
- `src/Esportra.Infrastructure/Migrations/Scripts/20260708110000_broadcast_notifications.sql`
- `src/Esportra.Infrastructure/Migrations/Scripts/20260708120000_ghost_mode.sql`

**Immediate Fix:**
1. Commit the local migration fixes: `git add src/Esportra.Infrastructure/Migrations/Scripts/20260708*.sql && git commit -m "fix: use correct admin_permissions schema in new migrations"`
2. Push to staging branch
3. Wait for Coolify to redeploy
4. Staging will recover

**This is EXACTLY what your CI migration replay would catch!** If it had been running, the PR would have been blocked.

---

## Good

| Area | Status | Details |
|------|--------|---------|
| **System Resources** | Healthy | 4 cores, 24GB RAM, 20% disk used (38GB/193GB) |
| **Uptime** | Excellent | 94 days |
| **Backups** | Working | Daily at 3am UTC, 7-day retention, prod: 1.4MB, staging: 6.4MB |
| **Auto-updates** | Enabled | unattended-upgrades configured |
| **SSL/HTTPS** | Working | Traefik (coolify-proxy) handling 80/443 |
| **Containers** | 44 running | Coolify, 2x Supabase stacks (prod/staging), Redis, etc. |
| **PostgreSQL** | Healthy | Both supabase-db containers up 3+ months |

---

## Needs Attention

### 1. System Reboot Required
- Kernel/security updates pending
- `*** System restart required ***` flag set
- **Action:** Schedule maintenance window, reboot server

### 2. Docker Updates Pending
- Docker CE: 27.0.3 → 29.4.0 (major version jump)
- containerd: 2.2.1 → 2.2.2
- **Action:** Plan Docker upgrade during maintenance

### 3. No fail2ban Installed
- SSH is exposed on port 22
- No brute-force protection detected
- **Action:** Install fail2ban: `sudo apt install fail2ban`

### 4. rpcbind Exposed (Port 111)
- NFS-related service, usually not needed
- Listening on 0.0.0.0:111
- **Action:** Disable if not needed: `sudo systemctl disable --now rpcbind`

### 5. Migration Replay Container Still Running
- `esportra-migration-replay` has been up 5 weeks
- Port 5444 exposed unnecessarily
- **Action:** Remove: `sudo docker rm -f esportra-migration-replay`

### 6. Orphan Container
- `condescending_allen` — no clear purpose, up 2 months
- **Action:** Investigate and remove if unused

### 7. Backups — No Offsite Copy
- Backups stored locally at `/opt/backups/`
- If server fails, backups are lost
- **Action:** Add S3/B2/offsite sync to backup script

---

## Security Posture

| Check | Status |
|-------|--------|
| SSH key-only auth | Likely (no password auth visible in config) |
| Root login disabled | Not explicitly set (check `/etc/ssh/sshd_config`) |
| Firewall | iptables active, default deny on INPUT |
| fail2ban | Not installed |
| Port 5432/5433 (Postgres) | Bound to 127.0.0.1 only — good |
| Port 8080 | Exposed (Traefik dashboard?) — verify needed |

---

## Recommended Actions

### Immediate (Today)
1. Fix staging migration crash — staging is DOWN
2. Verify the migration file content in git vs deployed

### This Week
1. Install fail2ban
2. Remove orphan containers (`esportra-migration-replay`, `condescending_allen`)
3. Disable rpcbind if not needed
4. Add offsite backup sync

### Maintenance Window
1. Reboot server (kernel updates)
2. Upgrade Docker (27 → 29)

---

## Port Map

| Port | Service | Binding | Notes |
|------|---------|---------|-------|
| 22 | SSH | 0.0.0.0 | Needs fail2ban |
| 80 | Traefik HTTP | 0.0.0.0 | OK |
| 443 | Traefik HTTPS | 0.0.0.0 | OK |
| 111 | rpcbind | 0.0.0.0 | Disable if unused |
| 5432 | Supabase DB (prod) | 127.0.0.1 | Good - local only |
| 5433 | Supabase DB (staging) | 127.0.0.1 | Good - local only |
| 5434 | Postgres 17 (host) | 127.0.0.1 | Good - local only |
| 5444 | Migration replay | 0.0.0.0 | Remove container |
| 6001-6002 | Coolify realtime | 0.0.0.0 | OK |
| 8000 | Coolify UI | 0.0.0.0 | OK |
| 8080 | Traefik dashboard | 0.0.0.0 | Verify auth required |
