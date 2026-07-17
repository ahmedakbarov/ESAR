# ESAR — Operations Runbook

## Health & observability
- Probes: `GET /health/live` (process), `GET /health/ready` (includes PostgreSQL).
- Logs: structured Serilog (console JSON-friendly + rolling files under `logs/`); correlate by `traceId`.
- Queues: RabbitMQ console → exchange `esar.events`, work queue `esar.workers`, DLQ `esar.dead-letter`.
- Job engine: Hangfire tables in PostgreSQL (`hangfire.*`); job history also in `connector_jobs`.

## Common incidents

### API returns 5xx / readiness failing
1. `kubectl logs deploy/esar-api` — look for Postgres connectivity or config errors
   (`Jwt:SigningKey`, `Security:EncryptionKey` must be set).
2. Check PostgreSQL availability and connection pool saturation.
3. Redis being down is **not** fatal (cache degrades to misses) — but check latency warnings.

### Connector keeps failing
1. Portal → Connectors → health check; read `LastHealthMessage`.
2. `GET /connectors/{id}/jobs` — the `Log` field has per-run details; typical causes: expired
   client secret, API permission removed, rate-limit ceiling, TLS inspection.
3. Fix credentials (resubmitting `***` keeps stored secrets; enter a new value to rotate).
4. A `ConnectorFailure` incident auto-resolves on the next successful run.

### Messages piling up in esar.dead-letter
1. Inspect a few messages (RabbitMQ console → Get messages) — the routing key tells you the consumer.
2. Fix the root cause (usually a poison payload or a down dependency), then shovel the queue
   back to `esar.events` (RabbitMQ shovel plugin) or purge after triage.

### Matching produced a wrong merge
1. Asset detail → Change History shows every merged field with old values; `match_records`
   contains the decision + explanation JSON.
2. Create the missing asset via `POST /assets`, then move mis-attributed sources by re-running
   the connector after raising `matching.autoMergeThreshold` or disabling the offending rule.
3. Use the review queue rather than auto-merge for that source class until tuned.

### Review queue is flooded
- Lower `matching.reviewThreshold` (more auto-creates) or raise rule weights that distinguish
  the colliding assets (typically MAC weight up, IP weight down in DHCP-heavy networks).

### Compliance sweep too slow / DB load
- The sweep processes assets in batches of 200; check for missing indexes after schema drift,
  and confirm the `asset-scoring` and `compliance-evaluate-all` jobs are not overlapping
  (stagger their crons).

## Routine maintenance

| Task | Frequency |
|---|---|
| Review pending approvals & match review queue | daily |
| Verify connector metrics (success rate ≥ 95%) | weekly |
| Rotate connector credentials & JWT signing key | per policy (key rotation invalidates active sessions) |
| PostgreSQL vacuum/analyze, backup restore drill | weekly / quarterly |
| Purge check: `connector_jobs`/notifications 30d, events 90d (automatic 03:00 job) | monitor |

## Scaling
- API: HPA on CPU (2→8 replicas); stateless.
- Workers: increase replicas and `Hangfire:WorkerCount`; discovery queue is separate (`discovery`)
  so heavy syncs do not starve compliance jobs.
- PostgreSQL: partitioning candidates at very high volume: `asset_events`, `audit_logs`, `asset_history`.
