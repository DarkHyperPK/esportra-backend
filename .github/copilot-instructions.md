# Esportra Backend — Copilot Instructions

> .NET 9 API for esports tournament management. Supabase (Postgres + Storage + Auth) as backend services.

---

## Repository Structure

```
src/
  Esportra.Api/              # Minimal API endpoints (one file per domain)
    Endpoints/               # TournamentEndpoints.cs, ProfileEndpoints.cs, etc.
    Program.cs               # App setup, middleware, DI
    Hubs/                    # SignalR hubs (7 total)
  Esportra.Infrastructure/   # DB, migrations, email, external services
    Migrations/
      MigrationRunner.cs     # DbUp runner (embedded resources)
      Scripts/               # ⚠️ ALL SQL migrations go here
  Esportra.Contracts/        # Shared DTOs / request-response records
```

---

## Critical Rules

### Migrations (DbUp)

1. **All schema changes MUST have a migration file** in `src/Esportra.Infrastructure/Migrations/Scripts/`
2. **Never modify the database directly** — no manual SQL on staging/production. Always create a migration.
3. **Naming**: `YYYYMMDDHHMMSS_descriptive_name.sql` (e.g., `20260331100000_payment_flow_bucket_and_columns.sql`)
4. **One migration per concern** — don't bundle unrelated changes
5. **Idempotent**: Use `IF NOT EXISTS`, `ON CONFLICT DO NOTHING`, `ADD COLUMN IF NOT EXISTS`
6. **Storage buckets**: Create via `INSERT INTO storage.buckets ... ON CONFLICT (id) DO NOTHING` in migrations
7. **RLS policies**: Use `DO $$ BEGIN IF NOT EXISTS ... END; $$` blocks to avoid duplicate policy errors
8. The `.csproj` has `<EmbeddedResource Include="Migrations\Scripts\*.sql" />` — files are auto-included

#### CI migration replay (post-baseline)

Pre-2026-03-17 schema lived in Supabase before DbUp; [`20260317_001_baseline.sql`](src/Esportra.Infrastructure/Migrations/Scripts/20260317_001_baseline.sql) marks that cutoff. CI does **not** replay pre-baseline scripts from empty Postgres.

| Artifact | Purpose |
|----------|---------|
| [`.github/ci/post-baseline-replay-schema.sql`](.github/ci/post-baseline-replay-schema.sql) | DB state after pre-baseline migrations (stub + overlays + SQL through baseline) |
| [`.github/ci/replay-journal-seed.sql`](.github/ci/replay-journal-seed.sql) | Marks those scripts as applied in DbUp `schemaversions` |
| [`.github/ci/supabase-replay-bootstrap.sql`](.github/ci/supabase-replay-bootstrap.sql) | Supabase stubs — input to schema generator only (not applied in CI replay) |
| [`.github/ci/replay-legacy-overlays.sql`](.github/ci/replay-legacy-overlays.sql) | Curated legacy public tables/columns for post-baseline replay |
| [`.github/ci/replay-legacy-manifest.json`](.github/ci/replay-legacy-manifest.json) | Audit of legacy deps in post-baseline migrations (`--check` in CI) |

**Adding a post-baseline migration:** normal flow — CI replays it automatically.

**CI replay fixture maintenance** (when a post-baseline migration references legacy schema not created in Scripts/):

1. Add or update DDL in `replay-legacy-overlays.sql` (include `UNIQUE`/`NOT NULL` needed for `ON CONFLICT`).
2. Regenerate audit + fixtures:
   ```bash
   python .github/scripts/audit-replay-legacy-deps.py
   python .github/scripts/generate-post-baseline-replay-schema.py
   python .github/scripts/generate-replay-journal-seed.py
   ```
3. Validate locally: `bash .github/scripts/run-migration-replay-local.sh`
4. Commit `replay-legacy-overlays.sql`, `replay-legacy-manifest.json`, `post-baseline-replay-schema.sql`, and `replay-journal-seed.sql` together.

Never edit `post-baseline-replay-schema.sql` or `replay-journal-seed.sql` by hand.

**Rare: adding a pre-baseline migration** (filename sorts before `20260317_001_baseline.sql`):

1. Update `supabase-replay-bootstrap.sql` if new legacy stubs are needed.
2. Regenerate fixtures:
   ```bash
   python .github/scripts/generate-post-baseline-replay-schema.py
   python .github/scripts/generate-replay-journal-seed.py
   ```
   Or with Postgres: `bash .github/scripts/build-post-baseline-schema.sh` (pg_dump replaces concat output).
3. Commit both `.github/ci/post-baseline-replay-schema.sql` and `.github/ci/replay-journal-seed.sql`.

CI runs `--check` on audit, journal, and schema generators in `migration-lint` — stale artifacts fail the build.

### Supabase Config Keys

The staging environment uses these config keys (from Docker env vars `Supabase__*`):

| Config Key | Maps From | Usage |
|---|---|---|
| `Supabase:Url` | `Supabase__Url` | Supabase API base URL |
| `Supabase:ServiceKey` | `Supabase__ServiceKey` | Service role JWT (full access) |
| `Supabase:JwtSecret` | `Supabase__JwtSecret` | JWT signing secret |

**⚠️ Common mistake**: Do NOT use `Supabase:ServiceRoleKey` — the actual env var maps to `Supabase:ServiceKey`.

### Supabase Storage Uploads

Follow the pattern in `StorageEndpoints.cs`:
```csharp
var supabaseUrl = config["Supabase:Url"]?.TrimEnd('/');
var serviceKey  = config["Supabase:ServiceKey"];
// ...
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceKey);
http.DefaultRequestHeaders.Add("apikey", serviceKey);
http.DefaultRequestHeaders.Add("x-upsert", "true");  // Always add for idempotent uploads
```

For **private buckets**: store `bucket/path` reference, use signed URLs for viewing:
```
POST {supabaseUrl}/storage/v1/object/sign/{bucket}/{path}
Body: {"expiresIn": 3600}
```

### Database Patterns

- **Dapper** for all SQL queries (not EF Core)
- Use `QueryAsync<dynamic>` for flexible result shapes
- **UUID arrays**: Use `= ANY(@ids)` with `Guid[]`, NOT `@p0::uuid` with DynamicParameters
- **Registration status**: Backend is source of truth — `"pending"` for paid, `"approved"` for free
- **Every table gets RLS** — default deny, explicit allow
- **Admin RPCs**: Use `SECURITY DEFINER` with internal role checks

### Endpoint Patterns

- Minimal API style: `app.MapGet/Post/Put/Delete`
- Auth: `.RequireAuthorization("Authenticated")`
- File uploads: `.DisableAntiforgery()`
- User context: `ctx.Items["UserContext"] as UserContext`
- Always return typed anonymous objects or `Results.*`

### Security

- Never weaken existing RLS policies or triggers
- Tournament organizers cannot set `is_featured`, `status`, `approved_by`, `winner_id` (trigger-enforced)
- Sensitive storage (receipts, KYC) → private buckets + signed URLs
- No secrets in code — environment variables only

---

## Storage Buckets (Staging)

| Bucket | Public | Purpose |
|--------|--------|---------|
| `users.avatars` | ✅ | Profile pictures |
| `teams.logos` | ✅ | Team logos |
| `tournaments.banners` | ✅ | Tournament cover images |
| `tournaments.media` | ✅ | Tournament photos/videos |
| `tournaments.payment.receipts` | ✅ | Payment receipts |
| `tournaments.disputes.evidence` | ✅ | Dispute evidence |
| `tournaments.results` | ✅ | Match result screenshots |
| `match-evidence` | ✅ | Match evidence uploads |
| `organizer-banners` | ✅ | Organizer banners |
| `organizer-media` | ✅ | Organizer media gallery |
| `system.assets.website` | ✅ | Platform assets |
| `system.assets.games` | ✅ | Game artwork |
| `system.assets.partners` | ✅ | Partner assets |
| `users.documents.kyc` | ❌ | KYC documents (private) |
| `venue-images` | ✅ | Venue photos |
| `venues.images` | ✅ | Venue images |
| `venues.layouts` | ✅ | Venue floor plans |

---

## SignalR Hubs

```
/hubs/notifications   # Push notifications
/hubs/bracket         # Bracket match updates
/hubs/match           # Check-in, results, disputes
/hubs/veto            # Map veto state machine
/hubs/chat            # Match-scoped chat
/hubs/conversations   # Direct messaging
/hubs/live            # Venue seat status
```

---

## Staging Infrastructure

- **SSH**: `ssh -i "key.key" ubuntu@84.235.246.82`
- **Docker**: Use `sudo docker` (user lacks docker group)
- **Supabase DB**: Container `supabase-db-usoocgow4s0wow00gsw04kcg`, port 5433→5432
- **Supabase Kong**: Container `supabase-kong-usoocgow4s0wow00gsw04kcg`, port 8000
- **Backend API**: Container `cgs8kgkwwc4g88w0oww4s4kw-132024092526`
- **DB query via SSH**: `echo 'SQL;' | sudo docker exec -i supabase-db-usoocgow4s0wow00gsw04kcg psql -U supabase_admin -d postgres -t`

---

## Commit & Branch Rules

- **Conventional commits**: `feat:`, `fix:`, `chore:`, `docs:`, `refactor:`, `perf:`
- **Branch**: `staging` (staging env), `main` (production, auto-deploys)
- First line under 72 chars
- `dotnet build` must pass before pushing

---

## Parked / Do Not Implement

- CS2 integration — not without explicit instruction
- Faceit Organizer API — read-only key, blocked
- .NET API migration phases 1+ — phase 0 complete

---

## Implementation Protocol (New Features Only)

For **new features** (not bug fixes or small changes), follow the 7-phase protocol:

1. **Discovery** — Ask about scope, users/roles, behavior, data, dependencies
2. **Interaction Mapping** — For each actor: actions, preconditions, system response, UI feedback
3. **Edge Cases** — Timing, data integrity, permissions, state conflicts, network, scale
4. **Implementation Plan** — DB changes, backend logic, types, hooks, components, build order
5. **Backend Build** — Migration → RLS → triggers → RPCs → storage → test as non-admin
6. **Frontend Build** — Hook → types → component → loading/error/empty states → toast → cache invalidation
7. **Verification** — Persistence (refresh → data still there), role access, error recovery, build passes

Phases 1–4 are thinking. Phases 5–7 are building. Do not skip phases for new features.
