# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run Commands

```bash
# Build
dotnet build

# Run API (listens on 0.0.0.0:8080)
dotnet run --project src/Esportra.Api

# Run tests
dotnet test
dotnet test src/Esportra.Api.Tests
dotnet test src/Esportra.Core.Tests

# Run database migrations
dotnet run --project src/Esportra.Migrator

# Docker local dev
docker compose up
```

## Architecture

.NET 9 esports tournament platform using Supabase (Postgres + Storage + Auth). Minimal APIs with Dapper (no EF Core).

**Projects:**
- `Esportra.Api` — Endpoints, SignalR hubs, middleware, background jobs, Program.cs (DI/bootstrap)
- `Esportra.Core` — Domain logic (bracket, match, tournaments, BR, notifications)
- `Esportra.Infrastructure` — Database, migrations (DbUp), email, external service clients
- `Esportra.Contracts` — Shared DTOs, `IDbConnectionFactory` interface
- `Esportra.Migrator` — Standalone migration runner (runs before API in prod)

**Key patterns:**
- Endpoints are static extension methods grouped by domain in `src/Esportra.Api/Endpoints/` (54 files), registered via `Map*Endpoints()` in Program.cs
- Database access via Dapper with `IDbConnectionFactory` (NpgsqlConnectionFactory). Snake_case columns map to PascalCase properties automatically
- Auth: Supabase JWT validated by ASP.NET, then `RoleEnrichmentMiddleware` fetches DB roles/permissions into `UserContext` (cached 60s in Redis at `user-ctx:{userId}`)
- Access `UserContext` in endpoints: `ctx.Items["UserContext"] as UserContext`
- SignalR hubs at `/hubs/{name}` for real-time (bracket, match, veto, chat, conversations, notifications, live, venue-sync, br)
- Redis HybridCache (L1 memory + L2 Redis) with `SwappableDistributedCache` fallback
- Background jobs as `BackgroundService` hosted services with scoped DI per cycle

**Middleware order:** ForwardedHeaders → HealthChecks → CORS → ExceptionHandler → Authentication → RoleEnrichment → SuspensionGate → GhostMode → AdminMutationAudit → RateLimit → Authorization → AuthorizationEnforcement

## Database Migrations (DbUp)

All schema changes require a migration file in `src/Esportra.Infrastructure/Migrations/Scripts/`.

- **Naming:** `YYYYMMDDHHmmss_descriptive_name.sql`
- **Must be idempotent:** `IF NOT EXISTS`, `ON CONFLICT DO NOTHING`, `ADD COLUMN IF NOT EXISTS`
- **External schemas** (`hangfire.*`, `auth.*`, `storage.*`): verify actual column names before use — don't assume snake_case. Hangfire uses camelCase: `invocationdata`, `statename`, `createdat`, etc. See `.claude/rules/external-schema-migrations.md`.
- **`ADD CONSTRAINT` has no `IF NOT EXISTS`** — wrap every constraint addition in a `DO $$` guard:
  ```sql
  DO $$
  BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'constraint_name') THEN
      ALTER TABLE t ADD CONSTRAINT constraint_name ...;
    END IF;
  END $$;
  ```
- One migration per concern
- Files auto-embed via `.csproj` wildcard — no manual registration needed
- `20260317_001_baseline.sql` marks the cutoff from Supabase-native schema to DbUp tracking

**CI replay fixture maintenance** (when a new migration references legacy schema not in Scripts/):
1. Update `replay-legacy-overlays.sql`
2. Regenerate: `python .github/scripts/audit-replay-legacy-deps.py && python .github/scripts/generate-post-baseline-replay-schema.py && python .github/scripts/generate-replay-journal-seed.py`
3. Validate: `bash .github/scripts/run-migration-replay-local.sh`
4. Commit all changed CI artifacts together

Never edit `post-baseline-replay-schema.sql` or `replay-journal-seed.sql` by hand.

## Coding Style

- Follow current .NET conventions, nullable reference types enabled
- Prefer `record` for immutable DTOs/value objects, `class` for entities with identity
- Explicit access modifiers on public/internal APIs
- Immutable updates — never mutate input models in-place; use `with` expressions
- `async`/`await` over `.Result`/`.Wait()`, pass `CancellationToken` through public APIs
- Functions < 50 lines, files < 800 lines
- No `dynamic` — prefer generics or explicit models
- `dotnet format` for formatting, remove unused `using` directives
- **Run `dotnet format` before staging any `.cs` file** — then verify with `dotnet format --verify-no-changes` (must exit 0). CI enforces this; `dotnet build`/`dotnet test` do not catch whitespace violations.
- Options pattern for config (strongly typed, no raw string reading)

## Security (Blocking)

Security is blocking, not advisory. Never weaken protections to unblock development.

- Never hardcode secrets — environment variables only, `appsettings.*.json` free of real credentials
- Parameterized queries always — never concatenate user input into SQL
- Validate DTOs at application boundary (FluentValidation or guard clauses)
- Framework auth handlers — no custom token parsing
- Authorization policies at endpoint/handler boundaries
- Never log raw tokens, passwords, or PII
- Safe client-facing errors — no stack traces, SQL, or paths in responses
- Rate limiting on all endpoints
- On security finding: STOP → fix CRITICAL/HIGH → rotate exposed secrets → grep for same pattern

## Critical Rules

- **Dapper only** — no EF Core, no repository abstractions. Direct SQL queries
- **UUID arrays:** `= ANY(@ids)` with `Guid[]`, not `@p0::uuid`
- **Every table gets RLS** — default deny, explicit allow
- **Never weaken existing RLS policies or triggers**
- **Supabase config key is `Supabase:ServiceKey`** (NOT `ServiceRoleKey`)
- **File uploads:** `.DisableAntiforgery()` on endpoint, proxy via service_role key
- **Private buckets:** store path reference, generate signed URLs for viewing
- **No secrets in code** — environment variables only
- **Conventional commits:** `feat:`, `fix:`, `chore:`, `docs:`, `refactor:`, `perf:` — first line < 72 chars
- **Session-scoped commits only (hard rule)** — never stage, commit, or push unrelated changes. Stage explicitly by file path (`git add -- <files>`), never `git add .` / `git add -A`. Only files modified for the current session's task may be committed; pre-existing dirty/untracked files stay untouched.

## Testing

- **xUnit** for unit and integration tests
- **FluentAssertions** for readable assertions
- **Moq** or **NSubstitute** for mocking
- **Testcontainers** for real infrastructure in integration tests
- **WebApplicationFactory** for API integration tests through HTTP (test auth, validation, serialization via middleware, not around it)
- Mirror `src/` structure under `tests/`
- Name tests by behavior: `MethodName_ReturnsExpected_WhenCondition`
- Target 80%+ coverage on domain logic, validation, auth, failure paths
- TDD: write test (RED) → implement (GREEN) → refactor (IMPROVE)

## Agents

Custom agents defined in `.claude/agents/` for specialized tasks:

| Agent | Description | Tools |
|-------|-------------|-------|
| `code-quality-reviewer` | Reviews code for clarity, simplicity, and maintainability. Focuses on readability, naming, structure, and duplication. Reports findings as HIGH/MEDIUM/LOW priority. | Glob, Grep, Read |
| `refactoring-planner` | Plans safe, incremental refactoring steps. Outputs prerequisite checks, ordered steps with verification, commit points, and risks. Never mixes behavior changes with refactoring. | Glob, Grep, Read |
| `security-reviewer` | Reviews code for security vulnerabilities and policy violations. Covers injection, auth gaps, data exposure, secrets handling, and input validation. Reports as CRITICAL/HIGH/MEDIUM/LOW severity. | Glob, Grep, Read |
| `devops` | Handles git push/deploy operations. Enforces staging-only workflow. Never pushes to main. | Bash, Read, Grep |

## Skills

Custom skills defined in `.claude/skills/` for guided workflows:

| Skill | Description | When to Use |
|-------|-------------|-------------|
| `clean-architecture` | Patterns for maintainable, testable code organization. Covers dependency direction, single responsibility, explicit dependencies, and file organization. | Creating new features, deciding where code lives, designing interfaces, refactoring tangled code |
| `secure-development` | Security-first development practices for APIs and data handling. Covers input validation, parameterized queries, authorization, error handling, and secrets management. | Building endpoints, handling user input, auth/authz work, sensitive data, external integrations |
| `root-cause-diagnosis` | Full multi-angle root-cause diagnosis protocol. Traces the complete data path, audits assumptions with evidence, distinguishes defects from intended workflow. | Bug reports, regressions, errors, unexpected behavior, investigating "why does X fail?" |
| `cyclomatic-complexity` | Audit and enforce cyclomatic complexity limits (CC ≤ 10 hard limit, ≤ 7 preferred). Covers counting rules, violation thresholds, and refactoring patterns. | Writing or modifying any method with branching logic, code reviews, pre-commit checks |

## Rules

Always-active rules defined in `.claude/rules/` that govern all code changes:

| Rule | Purpose |
|------|---------|
| `clean-code.md` | Write code that's easy to read, change, and delete. Functions < 50 lines, params ≤ 4, names reveal intent, no comments unless WHY is non-obvious. |
| `enterprise-code.md` | Code must be robust by design. Bans compensating helpers, duplicated state, string round-trips, symptom patches, and architecture bypasses. Requires single source of truth, normalize at boundary, atomicity, root-cause discipline. |
| `refactoring.md` | Refactor to improve structure without changing behavior. Small steps, run tests after each, commit frequently. Never refactor while fixing a bug or without test coverage. |
| `security.md` | Blocking security rules. Parameterized queries, RLS on every table, framework auth handlers, no hardcoded secrets, no PII in logs, safe client errors. Violations must be fixed before proceeding. |
| `git-workflow.md` | Never push to main. All work on staging. Production deploys via CI/CD only. |
| `cyclomatic-complexity.md` | Blocking CC limits. CC ≤ 10 per method (hard), ≤ 7 preferred. CC 11–15 requires refactor before merge; CC 16+ is a hard block. |

## Parked (Do Not Implement)

- CS2 integration
- Faceit Organizer API
- .NET API migration phases 1+

## Implementation Protocol (New Features)

For new features (not bug fixes), follow 7 phases:
1. Discovery — scope, actors, data, dependencies
2. Interaction Mapping — per-actor actions and system responses
3. Edge Cases — timing, permissions, state conflicts, scale
4. Implementation Plan — DB → backend → types → build order
5. Backend Build — migration → RLS → triggers → endpoints
6. Frontend Build (if applicable)
7. Verification — persistence, role access, error recovery, build passes

Phases 1–4 are thinking. Phases 5–7 are building.
