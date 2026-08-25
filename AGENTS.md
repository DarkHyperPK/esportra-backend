# AGENTS.md

Entry-point instructions for all AI coding agents (opencode, Claude Code, Codex, etc.) working in this repository.

## 1. Inheritance — Claude configuration is binding

This repo's canonical instructions live in the Claude configuration. Every agent MUST treat all of the following as binding, with authority equal to this file:

| Source | Contents |
|---|---|
| `CLAUDE.md` | Architecture (Esportra.Api / Core / Infrastructure / Contracts / Migrator), Dapper + Supabase patterns, middleware order, DbUp migration rules, testing, git workflow |
| `.claude/rules/*.md` | Guardrails — clean code, enterprise code, refactoring, security, git workflow (all blocking) |
| `.claude/skills/*/SKILL.md` | Domain skills (`clean-architecture`, `root-cause-diagnosis`, `secure-development`) — load the relevant skill before working in its area |
| `.claude/agents/*.md` | Subagent definitions and constraints (`code-quality-reviewer`, `devops`, `refactoring-planner`, `security-reviewer`) |
| `.claude/commands/*.md` | Workflow definitions (`code-health`, `refactor`, `security-check`) |

Conflict resolution: the strictest security or safety rule wins. Never weaken, skip, or reinterpret inherited rules. Do not modify `CLAUDE.md` or `.claude/**` to work around a failure.

## 2. HARD RULES — Push & deployment consent (blocking)

Violations are immediate failures. No exceptions without an explicit user instruction in the current conversation.

### R1 — Never push unless explicitly asked
- Local commits happen only when the user asks. `git push` additionally requires an explicit user request.
- Never push to "finish" a task, wrap up a session, trigger CI, back up work, or as a side effect of anything else. No proactive pushes, ever.

### R2 — The target branch must be named in the request
- A push request without a branch name is incomplete: STOP and ask which branch.
- Never infer the branch from context, current checkout, defaults, or habit — not even `staging`.

### R3 — Pushing IS deploying
CI listens to exactly two refs; a push to either ships code to a live environment:

| Ref pushed | Consequence |
|---|---|
| `origin/staging` | **STAGING** deployment |
| `origin/main` | **PRODUCTION** deployment |

Before pushing, state the consequence (e.g., "pushing `staging` will deploy to staging"). Treat every push to these refs as a deploy event.

### R4 — `main` is production; it is protected
- Never commit directly to `main`.
- Production releases happen only by merging `staging` → `main` with `--no-ff`, and only when the user explicitly asks for a release/deploy/promotion.

### R5 — No surprise refs, no history rewrites
- Push only to branches the user has named in their request. Never create or push new remote branches, tags, or arbitrary refs unprompted.
- Never force-push and never delete remote branches unless explicitly asked.

### Precedence
Section 2 overrides any older instruction that conflicts with it. In particular, older rules restricting pushes to `staging`/`main` do NOT override R2/R5 when the user explicitly names a different branch in their request.
