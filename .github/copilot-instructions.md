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
- **Registration status**: Backend is source of truth — `"pending"` for paid, `"registered"` for free
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
| `tournaments.payment.receipts` | ❌ | Payment receipts (private) |
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
