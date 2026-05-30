# CI/CD setup — deploy branches + Coolify

See the frontend doc for the full picture: [frag-and-book-main/docs/ci-cd-setup.md](https://github.com/DarkHyperPK/Esportra/blob/main/docs/ci-cd-setup.md).

## Backend-specific

| Workflow | Trigger | Checks |
|----------|---------|--------|
| `pr-check.yml` | PR to `staging` / `main` | Build, migration lint, replay |
| `deploy-staging.yml` | Push `staging` | Same + promote `deploy/staging` + API smoke |
| `deploy-prod.yml` | Push `main` | Same + prod-absent table lint + promote `deploy/main` + API smoke |

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
