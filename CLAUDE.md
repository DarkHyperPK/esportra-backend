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
- **`DROP INDEX` must be preceded by `DROP CONSTRAINT`** — always run `ALTER TABLE t DROP CONSTRAINT IF EXISTS <name>` before `DROP INDEX IF EXISTS <name>` on the same name. Constraint-backing indexes can only be dropped via the constraint.
- **No environment-specific DDL assumptions** — never assume whether a constraint vs standalone index exists based on environment. Use `pg_constraint`/`pg_indexes` guards or write both operations (DROP CONSTRAINT IF EXISTS + DROP INDEX IF EXISTS) when structural state may differ between environments.
- **Bulk `UPDATE`/`DELETE` requires pre-flight comment** — any migration with an `UPDATE` or `DELETE` affecting more than a trivial number of rows must include a comment block immediately above it:
  ```sql
  -- PRE-FLIGHT: Run on production before deploying, confirm expected count before proceeding:
  -- SELECT <query that returns the rows to be affected>;
  -- Expected: <description of acceptable result>
  ```
- One migration per concern
- **No `_staging_repair` in migration filenames** — migrations must be safe to run on all environments. If a known schema divergence exists between environments, document it in `.github/ci/prod-schema-divergences.md` instead of creating a repair migration.
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

## Company Agents & Design Authority

The company hierarchy (agents in `.claude/agents/`, skills in `.claude/skills/`) is canonical and versioned in `DarkHyperPK/Esportra`. Canonical files carry `<!-- esportra-canonical: company-v2 -->`.

**Canonical skills (repo paths in frontend):**
- `.claude/skills/company/`: orchestration and `reference/operating-standard.md`
- `.claude/skills/discovery-first/`: understand before building; ask BLOCKING and SHAPING questions
- `.claude/skills/design-recipe/`: choose the design direction per surface (nine directions, signals, Direction Contract, tasting rubric)
- `.claude/skills/esportra-brand/`: brand invariants, tokens, voice, imagery

**Order of authority** (highest first):
1. CEO answers (`clarifications.md`)
2. This `CLAUDE.md`
3. Brand invariants (`esportra-brand`)
4. The surface's Direction Contract
5. `design-recipe`
6. The role file in `.claude/agents/`
7. Vendored generic skills (`frontend-design`, `impeccable`, `theme-factory`, …)
8. Personal taste

**Rules:**
- **Never** use `brand-guidelines` for Esportra. It is Anthropic's brand.
- `theme-factory` presets only as a Themed-event direction chosen via `design-recipe`.
- `frontend-design` and `impeccable` work *inside* the chosen direction. Esportra's pinned signatures (stage black, rose cue, Poppins, square corners) are brand commitments, not defaults to replace.
- If a loaded skill or agent lacks the canonical marker, a personal (`~/.claude/skills`) or plugin copy is shadowing it. Read the repo file by path and follow that.
- Changes to company agents and skills go through a PR to `staging`, like code.

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

Company agents defined in `.claude/agents/` — canonical and versioned in the frontend repo (`DarkHyperPK/Esportra`), synced here locally.

**C-Suite (all use `opus` model):**

| Agent | Role |
|-------|------|
| `cto` | Technical strategy, architecture decisions, implementation orchestration |
| `cpo` | Product requirements, user stories, acceptance criteria |
| `cmo` | Marketing, positioning, growth analysis |
| `cfo` | Cost, resource, and budget analysis |
| `coo` | Operational implications, process, team readiness |
| `cio` | Security, compliance, privacy, risk |

**Engineering ICs (all use `sonnet` model):**

| Agent | Role |
|-------|------|
| `senior-software-architect` | System design, component design, cross-cutting concerns |
| `senior-backend-engineer` | API endpoints, business logic, .NET/Dapper implementation |
| `senior-database-engineer` | Schema design, migrations, RLS, query optimization |
| `senior-frontend-engineer` | React/TypeScript UI, state, component implementation |
| `senior-devops-engineer` | CI/CD, infra, Docker, deployment |
| `senior-ui-ux-designer` | Visual design specs, component anatomy, design system |
| `creative-lead` | Frontend quality authority. Owns motion design, interaction polish, design direction. Leads Frontend Engineer and UX Designer. Nothing ships frontend without sign-off. |

**QA (all use `sonnet` model):**

| Agent | Role |
|-------|------|
| `qa-lead` | QA orchestration, staging verification, final QA gate |
| `backend-qa` | Backend code review, logic, error handling, API contracts |
| `frontend-qa` | Frontend code review, component correctness, accessibility |
| `integration-qa` | End-to-end flows, middleware, cross-service contracts |
| `performance-qa` | Load, latency, query performance review |
| `senior-security-qa` | Security audit — injection, auth gaps, data exposure, secrets |

**Strategy & Utility:**

| Agent | Role | Tools |
|-------|------|-------|
| `think-tank` | C-suite USP ideation engine — generates 18 ideas, returns ranked shortlist of 6 | Glob, Grep, Read, WebSearch |
| `research-analyst` | USP feasibility evaluator — scores across 8 dimensions, returns PURSUE/VALIDATE/PARK/REJECT verdicts | Glob, Grep, Read, WebSearch |
| `devops` | Git push/deploy operations. Enforces staging-only workflow. Never pushes to main. | Bash, Read, Grep |
| `code-quality-reviewer` | Reviews code for clarity, simplicity, and maintainability. Reports HIGH/MEDIUM/LOW findings. | Glob, Grep, Read |
| `refactoring-planner` | Plans safe, incremental refactoring steps with verification points and risks. | Glob, Grep, Read |
| `security-reviewer` | Reviews code for security vulnerabilities. Reports CRITICAL/HIGH/MEDIUM/LOW findings. | Glob, Grep, Read |

## Skills

Canonical skills versioned in the frontend repo, synced locally to `.claude/skills/`:

| Skill | Description | When to Use |
|-------|-------------|-------------|
| `company` | CEO entry point for the full AI company pipeline. Orchestrates executive analysis → proposal → implementation → QA → audit. | Any feature, initiative, or objective to execute autonomously via `/company` |
| `company-status` | CEO dashboard — active projects, task states, blockers, approvals. Read-only. | `/company-status` |
| `discovery-first` | Understand before building. Restate the job, sort facts/assumptions/unknowns, ask decisive questions. Every agent runs this first. | Start of any task, thin briefs, before irreversible steps |
| `design-recipe` | Esportra creative method — choose a design direction (9 directions), compose with archetypes, taste with rubric. Takes precedence over generic design skills. | Any visual, UX, brand, marketing, or frontend work |
| `esportra-brand` | Esportra brand invariants — palette, type, voice, imagery, signature moves. Replaces `brand-guidelines` for all Esportra work. | Any Esportra-branded output |
| `clean-architecture` | Patterns for maintainable, testable code. Dependency direction, SRP, explicit dependencies, file organization. | New features, interface design, refactoring |
| `secure-development` | Security-first API practices. Input validation, parameterized queries, authorization, secrets management. | Endpoints, user input, auth/authz, sensitive data |
| `root-cause-diagnosis` | Full multi-angle root-cause protocol. Traces data path, audits assumptions, distinguishes defects from intended workflow. | Bug reports, regressions, unexpected behavior |
| `cyclomatic-complexity` | Audit and enforce CC limits (≤ 10 hard, ≤ 7 preferred). | Any method with branching logic, code reviews |
| `i-have-adhd` | ADHD-optimized output: next action first, numbered steps, state restatement, specific time estimates. Session-persistent. **Mandatory for all CEO-facing outputs.** | `/i-have-adhd` |
| `caveman` | Ultra-compressed output, all technical substance kept. Session-persistent. **Mandatory for all CEO-facing outputs.** | `/caveman [lite\|full\|ultra]` |

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
| `layout-viewport.md` | Frontend layout rule. `CommandPageGrid` and all page-level layout shells must use full viewport width — no `max-w-*` or `mx-auto` on outer grid containers. Violations block CTO audit merge. |

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

## AI Company

This repository runs an autonomous AI company operating system. The CEO (you) is the only human. Every other role — executives, engineers, designers, QA — is an AI agent.

### CEO Commands

- `/company "objective"` — triggers the full pipeline: executive analysis → proposal → CEO approval → delegation → implementation → QA → CTO audit → executive review → CEO acceptance
- `/company-status` — view active projects, task states, blockers, and approvals

### How It Works

1. You describe a feature or objective
2. Executives analyze it in parallel (CTO, CPO full depth; CMO/CFO/COO/CIO lightweight)
   - **Model:** All C-suite (CTO, CPO, CMO, CFO, COO, CIO) → `opus` | IC agents under them (engineers, QA, designers) → `sonnet`
3. System synthesizes a proposal with conflicts surfaced for your decision
4. **HARD STOP** — you approve, modify, or reject
5. CTO orchestrates implementation (architecture → engineering → QA → audit)
6. **HARD STOP** — you accept the final report or request changes

### Organizational Hierarchy

```
CEO (You)
├── CTO → Senior Architect, Backend, Frontend, Database, DevOps Engineers, QA Lead
├── CPO → Product requirements, acceptance criteria
├── CMO → Marketing/positioning analysis
├── CFO → Cost/resource analysis
├── COO → Operational implications
└── CIO → Security, compliance, privacy
```

### Agent Definitions

All agent files: `.claude/agents/` — canonical, versioned in `DarkHyperPK/Esportra` (staging branch), synced here
All rules: `.claude/rules/` (local only — gitignored)
Runtime state: `.claude/company/projects/` (local only — gitignored)

### Natural Language Detection

For feature-scale requests outside of `/company`, the system will ask:
> "This looks like a company-level feature request. Route through the company workflow? (or handle directly)"
