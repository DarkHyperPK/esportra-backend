# Coolify Deployment Guide — Esportra Backend

## Architecture

Coolify deploys the API using the **Dockerfile** in this repo.
Redis is added as a **native Coolify Service** inside the same project environment.
Being in the same environment means both containers share the **same Docker network** — they reach each other by service name with zero extra config.

```
GitHub (staging branch)
        │  push triggers auto-deploy
        ▼
  Coolify Project: "Esportra"
  └── Environment: "Staging"
      ├── Application: "esportra-api-staging"   ← Dockerfile
      └── Service:     "esportra-redis-staging"  ← Redis template
              │
              └─ same Docker network → reachable as "esportra-redis-staging:6379"
```

---

## Prerequisites

- Coolify instance running and accessible
- GitHub repo connected to Coolify (GitHub App or SSH key)
- `staging` branch exists in the repo

---

## Step 1 — Create a Coolify Project

1. Coolify sidebar → **Projects** → **Add Project**
2. Name: `Esportra`
3. This creates a default `production` environment. Create a second environment: **`staging`**

---

## Step 2 — Deploy Redis (inside staging environment)

> **Critical**: Redis MUST be in the same environment as the API. Same environment = same Docker network = hostname resolution works.

1. Open the `Esportra` project → click **`staging`** environment
2. Click **+ New Resource**
3. Choose **Service** → select **Redis** from the template list
4. Configure:
   - **Name**: `esportra-redis-staging`
   - **Image**: `redis:7-alpine` (already set by template)
   - **Redis Password**: generate a strong password and **copy it** — you'll need it in Step 3
5. Click **Deploy**
6. Wait until the Redis service shows **Running** (green)
7. Click on the Redis service → note the **Internal Connection String** shown in the UI
   - It will look like: `redis://:YOUR_PASSWORD@esportra-redis-staging:6379`
   - The hostname is `esportra-redis-staging` — this is what the API uses

---

## Step 3 — Deploy the API (inside staging environment)

1. Open the `Esportra` project → **`staging`** environment
2. Click **+ New Resource** → **Application**
3. Connect your GitHub repo: `DarkHyperPK/esportra-backend`
4. Configure:
   - **Name**: `esportra-api-staging`
   - **Branch**: `staging`
   - **Build Pack**: `Dockerfile`
   - **Dockerfile location**: `src/Esportra.Api/Dockerfile`
   - **Port**: `8080`
5. **Do NOT deploy yet** — set environment variables first (Step 4)

---

## Step 4 — Set Environment Variables

In the API application settings → **Environment Variables**, add ALL of these:

```
ASPNETCORE_ENVIRONMENT=Staging

# Redis — use the hostname from Step 2 + the password you set
ConnectionStrings__Redis=esportra-redis-staging:6379,password=YOUR_REDIS_PASSWORD

# Postgres (Supabase connection string)
ConnectionStrings__Postgres=Host=...;Database=postgres;Username=postgres;Password=...

# Supabase
Supabase__JwtSecret=your-supabase-jwt-secret
Supabase__JwtAudience=authenticated
Supabase__JwtIssuer=https://YOUR_PROJECT.supabase.co
Supabase__Url=https://YOUR_PROJECT.supabase.co
Supabase__ServiceKey=your-supabase-service-role-key

# Email
Resend__ApiKey=re_...

# Frontend URLs
FrontendUrl=https://staging.esportra.com
PartnerUrl=https://partner-staging.esportra.com

# Optional integrations (leave empty if not in use)
Riot__ApiKey=
Riot__OAuthClientId=
Riot__OAuthClientSecret=
Faceit__ApiKey=
Faceit__ClientId=
Faceit__ClientSecret=
Rawg__ApiKey=
```

> **Tip**: In Coolify, you can paste a block of `KEY=VALUE` lines and it bulk-imports them.

---

## Step 5 — Health Check (optional but recommended)

In the API application settings → **Health Check**:

- **URL**: `/health/live`
- **Method**: `GET`
- **Expected status**: `200`
- **Interval**: `30s`
- **Retries**: `3`

The API exposes:
- `GET /health/live` — app is running (Kestrel alive)
- `GET /health/ready` — app + Postgres + Redis are all healthy

---

## Step 6 — Deploy

1. Click **Deploy** on the API application
2. Watch the build log — you should see:
   ```
   [STARTUP] ✅ APPLICATION STARTED — Kestrel is listening!
   [Redis] ✅ Connected — IDistributedCache swapped to Redis.
   ```
3. Verify:
   ```
   curl https://YOUR_COOLIFY_DOMAIN/health/ready
   ```
   Expected response:
   ```json
   {
     "status": "Healthy",
     "checks": {
       "postgres": { "status": "Healthy" },
       "redis":    { "status": "Healthy", "latency_ms": 1 }
     }
   }
   ```

---

## Step 7 — CI/CD (GitHub Actions + Coolify Webhook)

The repo already has `.github/workflows/deploy-staging.yml` and `deploy-prod.yml`.
By default, Coolify auto-deploys on push (its own GitHub App integration).

To upgrade to **CI-gated deploys** (build must pass before Coolify deploys):

1. Coolify → API application → **Deployments** tab → copy the **Webhook URL**
2. GitHub → repo **Settings** → **Secrets and variables** → **Actions** → add:
   - Name: `COOLIFY_STAGING_WEBHOOK`
   - Value: the webhook URL you copied
3. Coolify → API application → **General** → disable **"Auto Deploy on push"**

From now on: push to `staging` → GitHub Actions builds and tests → on success, triggers Coolify via webhook.

---

## Step 8 — Production (when ready)

Repeat Steps 2–7 using the `production` environment:
- Environment: `production`
- Redis name: `esportra-redis` (or `esportra-redis-production`)
- API name: `esportra-api`
- Branch: `main`
- GitHub secret: `COOLIFY_PROD_WEBHOOK`

---

## Troubleshooting

### Redis `UnableToConnect` in logs
- Confirm Redis service is **Running** in Coolify (not just "Created")
- Confirm both services are in the **same Coolify environment** (same project, same env tab)
- Check the hostname: it must exactly match the Redis service name you set in Coolify
- Check the password: `ConnectionStrings__Redis` must include `,password=YOUR_REDIS_PASSWORD`

### `Resource temporarily unavailable` (Postgres DNS)
- This is a transient startup error — Postgres DNS resolves after a few seconds
- The `AutomatedRemindersJob` retries every 5 minutes automatically — no action needed
- If it persists, check `ConnectionStrings__Postgres` is set correctly

### Coolify shows "file not found" for Dockerfile
- Ensure the **Dockerfile location** is set to `src/Esportra.Api/Dockerfile` (not just `Dockerfile`)
- Trigger a **"Force Fetch"** or redeploy to make Coolify pull the latest commit

### Health check `/health/ready` returns unhealthy
- Redis `Degraded`: Redis not connected yet — wait ~30s for `RedisBackgroundConnector` to retry
- Postgres `Unhealthy`: Check `ConnectionStrings__Postgres` env var

### DataProtection key warning in logs
```
Storing keys in a directory '/root/.aspnet/DataProtection-Keys' that may not be persisted...
```
This is cosmetic for staging. For production, mount a persistent volume at `/root/.aspnet/DataProtection-Keys` in Coolify's persistent storage settings.

---

## Local Development

Uses Docker Compose (Redis + API together locally):

```bash
# Create a .env file (gitignored) with your local secrets:
cp .env.example .env   # edit with your values

# Start everything
docker compose up

# API is at http://localhost:5200
# Redis is at localhost:6379
```

See `docker-compose.yml` and `docker-compose.override.yml` for details.
