# Admin Centre V2 — Master Plan

**Status:** Approved by owner 2026-08-23
**Scope:** Full admin centre redesign — backend data correctness, RBAC enforcement, pruning, frontend shell + all 26 pages.
**Design authority:** `frag-and-book-main/docs/UI_DESIGN_GUIDE.md` + `esportra-backend/docs/brand/esportra-brand-identity.md`

---

## Locked decisions

| # | Decision |
|---|---|
| D1 | Superadmin = god view + enforce proper RBAC on UI-exposed permission families |
| D2 | All dispute counters/previews/hub read real `tournament_disputes` (phantom `disputes` table eliminated) |
| D3 | Revenue = labeled placeholder until a payment product exists; no fabricated numbers anywhere |
| D4 | Five overlapping stat endpoints consolidated into ONE canonical aggregate |
| D5 | Presence metric = honest heuristic, renamed "Admins active recently" |
| D6 | RBAC enforcement bounded to permission families the admin UI actually uses |
| D7 | Dead backend surface hard-deleted (no deprecation window) |
| D8 | Sidebar: keep 7-group IA and links; visual rebuild only; no pending badges |
| D9 | Command Centre panels: Needs Attention · Platform Pulse · Sponsor fleet KPIs · System Health · Recent staff activity · Quick Access |

## Audit summary (evidence base)

- **192 admin endpoints** inventoried across 12 files.
- **Fake data:** `/api/admin/stats` returns hardcoded `totalRevenue=0`, `totalBookings=0`, `pendingPartners=0`.
- **No revenue pipeline exists** (no payments/subscriptions/invoices tables). Only venue-scoped money flows: `wallet_transactions`, `pos_orders.total`, `venue_sessions.total_charged`.
- **Phantom `disputes` table** counted by command-centre, dashboard-stats, AdminHub, entity-preview; real disputes in `tournament_disputes`.
- **Active Now always 0:** reads unwritten `admin_session_audit`.
- **184 of 251 permissions enforced nowhere**; families substituted ad-hoc (alerts/moderation/gdpr/reports/licenses/verification/venues).
- **Duplicates:** two impersonation systems, entity-history + legacy, five stat endpoints.
- **SignalR hub:** four event constants never broadcast; SafeCount masks DB errors as zero.
- **Misc:** license IDs via `Random.Shared.Next(100000,999999)`; phantom `"admin:view"` permission branch; sessions tool is an audit heuristic.

## Phases

### Phase 0 — This document (gate)

### Phase 1 — Backend data correctness
1. Disputes → `tournament_disputes`: command-centre counts, `AdminHub.RefreshCounts`, entity-preview dispute branch (verify status enum first).
2. Canonical aggregate: `/api/admin/command-centre` absorbs sponsors fleet KPIs (30d), system health (kill-switch states, unresolved anomalies, active flags), `admins_active_recently` heuristic, ghost-approvals pending.
3. Revenue honesty: fake fields removed; `payments_available:false`; client renders labeled placeholder.
4. License IDs: collision-safe generation.
5. Sessions responses labeled "recently active admins", window surfaced.
6. Hub: dead event constants pruned; SafeCount logs failures.

### Phase 2 — RBAC enforcement (bounded)
Permission-per-endpoint mapping: `alerts:*`, `gdpr:view/process`, `reports:*`, `licenses:view/issue/revoke/reinstate`, `verification:view/approve/reject`, `venues:view/edit`. Mismatches fixed (role-delete policy, anomalies routes, IP allowlist superadmin flag). Superadmin bypass rides existing policy infra. Frontend `can()` mirrors gating.

### Phase 3 — Backend prune (hard delete)
`operations/impersonation/*` · `entity-history-legacy` · deprecated stats trio · widgets/preferences API + FE hooks · `trends`/`activity-feed` + hooks · phantom `"admin:view"` branch → 404 unknown type · DROP phantom `disputes` table.

### Phase 4 — Frontend shell
Sponsors mini-rail removed → horizontal section tabs. `AdminLayout` rebuilt (sharp corners, hairlines, mono groups, rose tick/index, inline-meta Access panel, unified ambient, mobile bar). New shared `AdminPage` primitive.

### Phases 5–9 — Page batches (each verified + separate staging commit)
| Phase | Pages |
|---|---|
| 5 Security/System | AuditLogs · KillSwitchConfig · FeatureFlags · SystemSettings · AnomalyDetection · IpAllowlist · GdprCompliance |
| 6 Operations | AlertsManagement · ScheduledReports · Broadcasts · DisputeCenter |
| 7 Users & Access | SessionManagement · LicenseManagement · VerificationSystem · RoleBuilder · AdminAccess · AdminRoleManagement · UserManagement (last) |
| 8 Content | GameCatalogManagement · MapManagement · TeamManagement · VenueManagement · ContentModeration · TournamentManagement · TournamentDetails |
| 9 Analytics + sweep | Analytics · compliance grep across admin tree |

### Per-phase gate
`tsc -b` zero new errors · `npm run build` audits pass · staging checklist · conventional commit per phase.

### Risks
- `tournament_disputes` status enum must be verified during 1.1.
- `finance_admin` seeds reference nonexistent payment families — out of scope, noted.
- UserManagement (79KB) gets its own sub-plan inside Phase 7.
