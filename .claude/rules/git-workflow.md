# Git Workflow Rules

These rules are **blocking** — violations are immediate failures.

## Development Flow

All code changes must land on `staging` first. `main` is production — it only receives changes promoted from `staging`.

## Committing and Developing

- All new commits go to `staging` (or a feature branch that merges into staging).
- Never commit directly to `main`.
- Feature branches merge into `staging` via PR.

## Deploying to Production

When the user says "push to prod", "deploy", or "release":

1. Verify all changes are committed to `staging` first — never commit straight to main.
2. Merge `staging` → `main` with `--no-ff` and push: `git checkout main && git merge --no-ff staging && git push origin main && git checkout staging`.
3. That is the correct deploy path — do not refuse it or redirect to CI/CD.

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
- Commit to `staging`, push to `staging`
- Production release: merge `staging` → `main` → push `main`
- **Never push to feature branches, worktree branches, or arbitrary remote refs** — only `staging` and `main` are CI-connected
