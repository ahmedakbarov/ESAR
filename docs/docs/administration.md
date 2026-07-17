# ESAR — Administration Guide

## 1. RBAC

Built-in roles: **Administrator** (everything), **SecurityAnalyst** (operate assets, matching,
compliance workflow, approvals, incidents, reports), **Auditor** (read-only + audit), **Viewer**.
Custom roles: Portal → Users & Roles, or `POST /roles` with permission codes. Permissions are
enforced per-endpoint (`assets.read`, `policies.manage`, `approvals.decide`, …).

Local accounts require 12+ character passwords; 5 failed logins lock the account for 15 minutes.
Entra ID users authenticate with tenant tokens when `EntraId:*` is configured; MFA is enforced by
the identity provider's conditional access.

## 2. Connectors

Create via portal or API. Type-specific settings (values whose key contains
`secret/password/token/apikey/key` are AES-256-GCM encrypted at rest, shown as `***`, and kept
when you resubmit `***`):

| Type | Settings |
|---|---|
| Azure | `tenantId`, `clientId`, `clientSecret` (Reader + Resource Graph) |
| EntraId / Intune / MicrosoftDefender | `tenantId`, `clientId`, `clientSecret` (Graph `Device.Read.All`, `DeviceManagementManagedDevices.Read.All`, Defender `Machine.Read.All`) |
| ActiveDirectory | `server`, `port`, `baseDn`, `username`, `password`, `useSsl` |
| VmwareVCenter | `baseUrl`, `username`, `password` |
| CrowdStrike | `baseUrl`, `clientId`, `clientSecret` |
| SentinelOne | `baseUrl`, `apiToken` |
| Tenable | `accessKey`, `secretKey` |
| Qualys | `baseUrl` (gateway), `username`, `password` |
| ServiceNowCmdb | `instanceUrl`, `username`, `password`, `table` (default `cmdb_ci_computer`) |
| GenericRest | `url`, `authHeader`, `itemsPath`, `idField`, `hostnameField`, `osField`, `ipField`, `macField`, `serialField` |

Each connector has: cron schedule (applied within 1 minute, no restart), Full/Incremental default
mode, retry count, rate limit/minute, health check button, execution history and
`/connectors/{id}/metrics` (success rate, throughput, durations). A failed run opens a
`ConnectorFailure` incident and sends the `connector-failure` notification; the next success
auto-resolves it.

## 3. Matching administration

- **Rules** (Matching page): adjust weights/order/enable — versioned, cached 5 minutes.
- **Thresholds**: `matching.autoMergeThreshold` (default 0.85), `matching.reviewThreshold` (0.60).
- **Review queue**: *Merge* applies the candidate to the matched asset; *New asset* creates a
  separate golden record. Every decision is audited with its explanation JSON.
- **Simulation**: `POST /matching/simulate` with a candidate payload — no writes.
- **Source priorities** (Settings page): lower number wins; attribute-level rows override globals.

## 4. Policies & compliance workflow

Policies (Governance → Policies) define required + mandatory controls per asset scope. First
matching policy by priority wins; unmatched assets use the full default baseline. After editing
policies, the next compliance sweep (every 2h) re-evaluates; force one asset via
`POST /compliance/assets/{id}/evaluate`.

Remediation workflow per failing control: auto-assigned states (WaitingSiemOnboarding,
WaitingEdrInstallation, WaitingAgentInstallation, WaitingOwnerApproval, PendingReview) →
operators move items via `PATCH /compliance/records/{id}/remediation` (state, notes, assignee).
`RiskAccepted` exempts the control from score/status (documented exception).

## 5. Approval workflow

Set `approval.requireForNewAssets=true` to hold newly discovered assets in **Planned** lifecycle
until approved (Operations → Approvals). Approvers can set owner/BU/criticality inline.
Rejection quarantines the asset. Merges can be routed through approval with
`POST /approvals/request-merge` (four-eyes).

## 6. Scheduler (Hangfire) defaults

| Job | Cron |
|---|---|
| Per-connector discovery | connector's own cron |
| Compliance sweep | `0 */2 * * *` |
| Lifecycle (stale/offline/decommission) | `30 * * * *` |
| Data-quality / health / risk scoring | `15 */6 * * *` |
| Duplicate-IP detection | `45 */12 * * *` |
| Incident escalation | `*/15 * * * *` |
| Notification dispatch | every minute |
| Retention cleanup | `0 3 * * *` |

## 7. Settings reference

| Key | Default | Meaning |
|---|---|---|
| `matching.autoMergeThreshold` / `matching.reviewThreshold` | 0.85 / 0.60 | Soft-match thresholds |
| `lifecycle.staleAssetDays` / `lifecycle.decommissionAfterDays` | 7 / 90 | Offline / retire windows |
| `compliance.evidenceMaxAgeDays` | 7 | Max age of source evidence |
| `approval.requireForNewAssets` | false | Activation gate |
| `dataquality.alertBelowScore` | 50 | DataQualityDegraded event threshold |
| `notifications.defaultRecipient` | soc@example.com | Fallback recipient |
| `reports.outputDirectory` | /data/reports | Report storage (shared volume) |

## 8. Notifications & incidents

Templates (`{{placeholders}}`) per channel; escalation rules re-notify unacknowledged incidents
after N minutes. ServiceNow/Jira ticket creation is enabled via the `ServiceNow`/`Jira`
configuration sections; the created ticket id is linked on the incident.
