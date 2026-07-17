# ESAR — Enterprise Security Asset Registry

Centralized, source-agnostic **single source of truth for cyber assets**. ESAR automatically
discovers, correlates, deduplicates, enriches and governs every enterprise IT asset across
cloud, on-prem, EDR, vulnerability-management, SIEM and CMDB systems — and exposes the golden
records through a versioned REST API and a modern web portal.

## Capabilities

| Area | What it does |
|---|---|
| Discovery | 12 built-in connectors (Azure, Entra ID, Intune, Defender, AD/LDAP, vCenter, CrowdStrike, SentinelOne, Tenable, Qualys, ServiceNow CMDB, Generic REST) + reusable framework for the rest (AWS, GCP, Splunk, QRadar, …) |
| Matching | Configurable rule engine: hard identifiers (Azure Resource ID → AWS Instance ID → VMware UUID → BIOS UUID → Serial → AD GUID → EDR Endpoint ID) + weighted soft scoring (hostname/MAC/IP/OS/domain), explainable decisions, simulation, manual review queue, rule versioning |
| Merge & priority | Field-level merge with per-attribute source authority (configurable priorities), full history of previous values |
| Policy engine | Security-baseline policies per asset type/environment/criticality — compliance requirements are data, not code |
| Compliance workflow | 9 controls (SIEM, EDR, AV, VS, monitoring, backup, patch, encryption, classification) with remediation states (Waiting for SIEM Onboarding, Waiting for EDR Installation, Risk Accepted, …) |
| Relationship engine | Asset dependency graph (RunsOn, DependsOn, Uses, Hosts, …) with impact analysis / blast radius |
| Scoring | Data Quality, Asset Health and dynamic Risk scores recalculated on schedule |
| Governance | Approval workflow for new assets and merges (four-eyes), full audit trail, asset timeline |
| Operations | Hangfire scheduler, RabbitMQ event catalog + dead-letter queue, incident generation with ServiceNow/Jira integration, notifications (Email/Teams/Slack/Webhook/SMS) with escalation |
| Reporting | 11 report types as PDF / Excel / CSV |

## Technology

.NET 8 (Clean Architecture: Domain / Application / Infrastructure / Api / Workers) · React + TypeScript ·
PostgreSQL + EF Core · Redis · RabbitMQ · Hangfire · Serilog · Swagger/OpenAPI · JWT + Entra ID · Docker · Kubernetes/Helm.

## Quick start (Docker Compose)

```bash
# 1. Set real secrets (defaults exist for dev only)
export POSTGRES_PASSWORD=... RABBITMQ_PASSWORD=... JWT_SIGNING_KEY=<32+ chars> \
       ENCRYPTION_KEY=$(openssl rand -base64 32) ADMIN_PASSWORD=<initial admin password>

# 2. Start the stack
docker compose up -d --build

# 3. Open
#    Portal:            http://localhost:8090   (login: admin / $ADMIN_PASSWORD)
#    API + Swagger:     http://localhost:8080/swagger
#    RabbitMQ console:  http://localhost:15672
```

The API creates and seeds the schema on first start (`Database:AutoMigrate=true`).
For DBA-managed production installs run [db/001_schema.sql](db/001_schema.sql),
[db/002_seed.sql](db/002_seed.sql) and [db/003_v2_features.sql](db/003_v2_features.sql) instead.

## Production HTTPS and backups

Set `ESAR_DOMAIN` to a public DNS name, point its A record to the VM public IP, and allow inbound TCP **80** and **443** in the Azure NSG. Set strong values for `POSTGRES_PASSWORD`, `RABBITMQ_PASSWORD`, `JWT_SIGNING_KEY`, `ENCRYPTION_KEY`, and `ADMIN_PASSWORD`; never commit them. Then run:

```bash
docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file .env.production up -d --build
```

Caddy obtains and renews Let's Encrypt certificates automatically. The production overlay removes public PostgreSQL, Redis, RabbitMQ, API and frontend ports; remove the prior public NSG rule for TCP **8090** after switching. Without a public domain, use the base Compose file for local/test HTTP only.

The `postgres-backup` container creates a compressed UTC backup nightly at 02:00 and retains seven days in the `postgres-backups` named volume. Check failures with `docker compose logs postgres-backup`. Restore one backup without exposing the database port:

```bash
gunzip -c backup.sql.gz | docker compose exec -T postgres psql -U esar -d esar
```

Copy backups off the VM regularly, for example with `docker run --rm -v esar_postgres-backups:/data -v "$PWD":/out alpine cp -a /data/. /out/`. Test restores in a separate environment before relying on them for recovery.

## Repository layout

```
src/ESAR.Domain            Entities, enums (no dependencies)
src/ESAR.Application       Use cases: matching, merge, policy, compliance, scoring, approvals, CQRS
src/ESAR.Infrastructure    EF Core, Redis, RabbitMQ, connectors, security, reporting
src/ESAR.Api               REST API (JWT/RBAC, Swagger, versioning, rate limiting)
src/ESAR.Workers           Hangfire jobs + RabbitMQ consumers (discovery, compliance, scoring, cleanup)
src/frontend               React + TypeScript portal
tests/                     xUnit unit tests + Testcontainers integration tests
db/                        PostgreSQL DDL + seed scripts
deploy/                    Dockerfiles, Kubernetes manifests, Helm chart
docs/                      Architecture, database, API, guides, runbook, DR
```

## Documentation

- [Architecture](docs/architecture.md) — components, engines, diagrams, event catalog
- [Database design](docs/database.md) — ER diagram and table reference
- [API reference](docs/api.md) — endpoint summary (full OpenAPI at `/swagger`)
- [Installation guide](docs/installation.md) · [Administration guide](docs/administration.md)
- [User guide](docs/user-guide.md) · [Developer guide](docs/developer-guide.md)
- [Runbook](docs/runbook.md) · [Disaster recovery](docs/disaster-recovery.md)
