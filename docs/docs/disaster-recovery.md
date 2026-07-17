# ESAR — Disaster Recovery Guide

## What must be protected

| Asset | Criticality | Method |
|---|---|---|
| PostgreSQL (`esar` DB incl. Hangfire schema) | **Critical — the system of record** | Continuous WAL archiving + nightly base backups (e.g. pgBackRest); PITR retention ≥ 14 days |
| `Security:EncryptionKey`, `Jwt:SigningKey` | **Critical** | Secret manager with versioning/escrow. Without the encryption key, stored connector credentials are unrecoverable and must be re-entered. |
| Reports volume (`/data/reports`) | Low | Optional snapshot — reports are regenerable |
| Redis / RabbitMQ | Low | Pure cache / transient transport — rebuild empty; consumers reconcile via scheduled jobs |
| Configuration (Helm values, manifests, pipelines) | High | Git (this repository) |

RPO target: ≤ 15 min (WAL shipping). RTO target: ≤ 1 h with the procedure below.

## Recovery procedure (total loss of environment)

1. **Provision infrastructure**: Kubernetes cluster (or Docker host), PostgreSQL, Redis, RabbitMQ.
2. **Restore secrets** from the secret manager (encryption key MUST be the original).
3. **Restore PostgreSQL** to the latest point in time; verify:
   `SELECT count(*) FROM assets;` and `SELECT max("Timestamp") FROM audit_logs;`
4. **Deploy ESAR** with the same image tags (`helm upgrade --install ... --set image.tag=<known-good>`),
   `Database:AutoMigrate=false`.
5. **Verify**: `/health/ready` 200, portal login, one asset detail renders, Swagger loads.
6. **Re-establish messaging**: exchanges/queues are declared automatically by the workers on start;
   confirm bindings in the RabbitMQ console.
7. **Trigger reconciliation**: run each connector once (`POST /connectors/{id}/run?mode=Full`) —
   ingestion is idempotent by `(connector, externalId)`, so replays are safe and refresh `LastSeen`.
8. **Validate governance state**: pending approvals and the review queue survive with the database;
   re-run one compliance evaluation as a smoke test.

## Partial failures

| Scenario | Action |
|---|---|
| Postgres primary lost | Fail over to replica; ESAR reconnects (EF retry-on-failure). |
| Redis lost | None required — cache misses only; recreate the instance. |
| RabbitMQ lost | Recreate; workers redeclare topology; event consumers catch up via the scheduled jobs that reconcile state. |
| One bad deployment | `helm rollback esar <rev>`; DB schema scripts are additive/idempotent, so a previous app version keeps working. |
| Corrupted golden data (bad merge wave) | PITR the database to just before the connector run (`connector_jobs.StartedAt`), then replay connectors. |

## Drills
Quarterly: restore the latest backup into a staging namespace, deploy the current chart against it,
run the integration test suite pointed at the restored API, and document time-to-green.
