# ESAR — Installation Guide

## Prerequisites

| Component | Version | Notes |
|---|---|---|
| PostgreSQL | 16+ | HA recommended (Patroni / managed) |
| Redis | 7+ | Optional in dev (in-memory fallback) |
| RabbitMQ | 3.13+ | Optional in dev (events become no-ops, manual sync runs inline) |
| Docker / Kubernetes | 24+ / 1.27+ | For containerized installs |
| .NET SDK / Node | 8.0 / 20 | Only for source builds |

## Required secrets

| Setting | Description |
|---|---|
| `ConnectionStrings__Postgres` | `Host=...;Port=5432;Database=esar;Username=esar;Password=...` |
| `Jwt__SigningKey` | ≥ 32 characters, random |
| `Security__EncryptionKey` | Base64 of 32 random bytes (`openssl rand -base64 32`) — encrypts connector secrets. **Losing it makes stored connector credentials unreadable.** |
| `ESAR_ADMIN_INITIAL_PASSWORD` | First admin password (only used when the `admin` user does not exist) |

## Option A — Docker Compose (evaluation / small deployments)

```bash
export POSTGRES_PASSWORD=... RABBITMQ_PASSWORD=... \
       JWT_SIGNING_KEY=... ENCRYPTION_KEY=... ADMIN_PASSWORD=...
docker compose up -d --build
```

Services: portal `:8090`, API/Swagger `:8080`, PostgreSQL `:5432`, Redis `:6379`, RabbitMQ mgmt `:15672`.
Startup order is handled by health checks — the API initializes the schema, workers start after it is healthy.

## Option B — Kubernetes / Helm (production)

```bash
# 1. Provision PostgreSQL, Redis, RabbitMQ (managed or in-cluster).
# 2. Create the schema (in order): db/001_schema.sql, db/002_seed.sql, db/003_v2_features.sql, db/004_reconcile.sql, db/005_policy_scope_filters.sql, db/006_asset_policy_exempt.sql, db/007_external_identity_unique.sql, db/008_user_protected.sql
# 3. Deploy:
helm upgrade --install esar deploy/helm/esar -n esar --create-namespace \
  --set image.registry=ghcr.io/your-org \
  --set ingress.host=esar.example.com \
  --set config.corsOrigin=https://esar.example.com \
  --set secrets.postgresConnection="Host=...;..." \
  --set secrets.rabbitmqPassword=... \
  --set secrets.jwtSigningKey=... \
  --set secrets.encryptionKey=...
```

Keep `config.autoMigrate=false` in production; schema changes go through the SQL scripts.
Raw manifests (no Helm): `kubectl apply -f deploy/k8s/esar.yaml` after editing the Secret.

## Option C — From source (development)

```bash
dotnet build ESAR.sln
dotnet test  ESAR.sln                          # integration tests need Docker
ESAR_ADMIN_INITIAL_PASSWORD=Dev-Password-123! dotnet run --project src/ESAR.Api      # :8080
dotnet run --project src/ESAR.Workers
cd src/frontend && npm install && npm run dev  # :5173, proxies /api → :8080
```

## Post-install checklist

1. Log in as `admin`, change the password (Users page).
2. Confirm `GET /health/ready` returns 200 and Swagger loads.
3. Create connectors (Administration guide §2) and run a health check, then a Full sync.
4. Review Settings: matching thresholds, `approval.requireForNewAssets`, stale-asset windows.
5. Configure Azure AD SSO (optional): create an Azure App Registration (Single-page application,
   redirect URI = ESAR's origin, `openid profile email` permissions), then on the **Settings page**
   set `auth.entra.tenantId` + `auth.entra.clientId`. The Login page shows a "Sign in with
   Microsoft" button once both are set — no `.env`, no rebuild.
6. AD login (optional): once an enabled `ActiveDirectory` connector exists, the Login page shows an
   "Active Directory" tab that authenticates via LDAP bind using the connector's server/baseDn.
7. For Azure AD SSO / AD login, pre-provision accounts on the Users page (pick the provider, no
   password) or enable `auth.federated.autoProvision` in Settings to auto-create a Viewer account
   on first successful login.
8. Configure SMTP / Teams / Slack / ServiceNow / Jira sections for notifications and ticketing.
