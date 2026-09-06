# Git Workflow Rules

These rules are **blocking** — violations are immediate failures.

## Development Flow

All code changes must land on `staging` first. `main` is production — it only receives changes promoted from `staging`.

## Push Consent (HARD RULES)

- **Never `git push` unless the user explicitly asks for a push in the current conversation.** Never push to finish a task, trigger CI, or back up work.
- A push request must name the target branch. If the user says "push" without naming a branch, STOP and ask which branch. Never infer it from the current checkout or habit — not even `staging`.
- **Pushing IS deploying.** CI listens to exactly two refs:
  - `git push origin staging` → STAGING deployment
  - `git push origin main` → PRODUCTION deployment
  State the consequence before pushing and treat every push to these refs as a deploy event.
- Never force-push and never delete remote branches unless explicitly asked.

Exception: the "only staging/main" restrictions below do not block a push when the user has explicitly named that other branch in their request.

## Committing and Developing

- All new commits go to `staging` (or a feature branch that merges into staging).
- Never commit directly to `main`.
- Feature branches merge into `staging` via PR.

## Deploying to Production

When the user says "push to prod", "deploy", or "release":

1. Verify all changes are committed to `staging` first — never commit straight to main.
2. **Verify staging CI is green.** Check the last CI run on `origin/staging` passed all jobs — including `migration-replay`. Do not promote if any job is red or skipped. For changes that touch CI infrastructure (workflows, scripts, fixtures), this check is mandatory before promoting.
3. Merge `staging` → `main` with `--no-ff` and push: `git checkout main && git merge --no-ff staging && git push origin main && git checkout staging`.
4. That is the correct deploy path — do not refuse it or redirect to CI/CD.

The rule bans **direct commits to main**, not **promoting staging to main**. Staging → main is the intended release mechanism.

`--no-ff` is mandatory: it preserves a clear merge commit on main so the deploy is always an explicit promotion event, never a silent fast-forward that makes staging and main look identical in history.

## What Is Banned

- `git commit` while on `main`
- Committing new work directly to `main` without going through `staging`
- Force-pushing to `main`
- Bypassing staging entirely for new changes
- Fast-forward merges to `main` (`git merge staging` without `--no-ff`) — always use `--no-ff`
- Pushing to any branch other than `staging` or `main` — only these two trigger CI

## Branch Hygiene

- Default working branch: `staging`
- Commit to `staging`, push to `staging` — but only when the user asks, and only with the branch named (see Push Consent above)
- Production release: merge `staging` → `main` → push `main`, only on explicit request
- **Never push to feature branches, worktree branches, or arbitrary remote refs unprompted** — only `staging` and `main` are CI-connected
