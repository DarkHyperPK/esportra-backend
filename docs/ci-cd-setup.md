# CI/CD setup — deploy branches + Coolify

See the frontend doc for the full picture: [frag-and-book-main/docs/ci-cd-setup.md](https://github.com/DarkHyperPK/Esportra/blob/main/docs/ci-cd-setup.md).

Re-run staging deploy workflow from GitHub Actions if push did not trigger (path filters).

## Backend-specific

| Workflow | Trigger | Checks |
|----------|---------|--------|
| `pr-check.yml` | PR to `staging` / `main` | Build, migration lint, **post-baseline replay** |
| `deploy-staging.yml` | Push `staging` | Same + promote `deploy/staging` + API smoke |
| `deploy-prod.yml` | Push `main` | Same + prod-absent table lint + promote `deploy/main` + API smoke |

Migration replay applies a committed baseline schema snapshot and only tests DbUp scripts after `20260317_001_baseline.sql`. See [`.github/copilot-instructions.md`](../.github/copilot-instructions.md) (CI migration replay section).

### Post-baseline replay (local)

Reproduce the CI `migration-replay` job before pushing:

```bash
bash .github/scripts/run-migration-replay-local.sh
```

Regenerate committed CI artifacts after overlay or pre-baseline changes:

```bash
python .github/scripts/audit-replay-legacy-deps.py
python .github/scripts/generate-post-baseline-replay-schema.py
python .github/scripts/generate-replay-journal-seed.py
```

CI asserts every embedded script is journaled via `assert-migration-replay-complete.sh`.

## Coolify branch switch

- Staging backend app → `deploy/staging`
- Production backend app → `deploy/main`

## Bootstrap

```powershell
git push origin staging:deploy/staging
git push origin main:deploy/main
```

## Secrets

| Secret | Example |
|--------|---------|
| `E2E_STAGING_API_URL` | `https://api-staging.esportra.com` |
| `E2E_PROD_API_URL` | `https://backend.esportra.com` |
