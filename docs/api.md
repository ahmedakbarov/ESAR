# ESAR — API Reference (v1)

Base URL: `/api/v1` · Auth: `Authorization: Bearer <JWT>` (obtain via `POST /auth/login`) ·
Full OpenAPI/Swagger: `GET /swagger`. Errors follow RFC 7807. All list endpoints support paging.

## Authentication
| Method | Path | Permission | Description |
|---|---|---|---|
| POST | `/auth/login` | — | Local login → JWT (roles + permission claims) |
| GET | `/auth/me` | any | Current identity, roles, permissions |

## Assets
| Method | Path | Permission |
|---|---|---|
| GET | `/assets` — filtering (`search`, `useRegex`, `assetType`, `status`, `lifecycleStatus`, `environment`, `criticality`, `complianceStatus`, `businessUnit`, `owner`, `source`, `ip`, `mac`, `os`, `software`, `cloudProvider`, `tagKey/tagValue`, `maxDataQualityScore`), sorting (`sortBy`, `sortDescending`), paging | `assets.read` |
| GET | `/assets/{id}` · `/assets/{id}/history` · `/assets/{id}/timeline` | `assets.read` |
| POST | `/assets` · PUT `/assets/{id}` · DELETE `/assets/{id}` | `assets.write` / `assets.delete` |
| POST | `/assets/merge` (immediate) | `assets.merge` |
| POST | `/assets/bulk` (import via full matching pipeline) · PUT `/assets/bulk` · POST `/assets/bulk-delete` | `assets.import` / `assets.write` / `assets.delete` |

## Matching
| Method | Path | Permission |
|---|---|---|
| GET | `/matching/review-queue` · `/matching/stats` · `/matching/rules` | `matching.read` |
| POST | `/matching/review-queue/{id}/approve` · `/reject` | `matching.review` |
| POST | `/matching/simulate` (dry run, explainable) | `matching.read` |
| PUT | `/matching/rules/{id}` (weight/order/enabled, bumps version) | `settings.manage` |

## Compliance & policies
| Method | Path | Permission |
|---|---|---|
| GET | `/compliance/summary` · `/compliance/failing/{control}` · `/compliance/remediation` | `compliance.read` |
| POST | `/compliance/assets/{assetId}/evaluate` | `compliance.read` |
| PATCH | `/compliance/records/{recordId}/remediation` (state/notes/assignee) | `compliance.manage` |
| GET | `/policies` | `compliance.read` |
| POST/PUT/DELETE | `/policies`, `/policies/{id}` | `policies.manage` |

## Relationships & approvals
| Method | Path | Permission |
|---|---|---|
| GET | `/relationships/asset/{assetId}` · `/relationships/asset/{assetId}/impact?depth=` · `/relationships/types` | `assets.read` |
| POST | `/relationships` · DELETE `/relationships/{id}` | `relationships.manage` |
| GET | `/approvals/pending` | `assets.read` |
| POST | `/approvals/{id}/approve` (optional metadata overrides) · `/approvals/{id}/reject` | `approvals.decide` |
| POST | `/approvals/request-merge` (four-eyes merge) | `assets.merge` |

## Connectors
| Method | Path | Permission |
|---|---|---|
| GET | `/connectors` · `/connectors/{id}` · `/connectors/types` · `/connectors/{id}/jobs` · `/connectors/{id}/metrics` | `connectors.read` |
| POST/PUT/DELETE | `/connectors`, `/connectors/{id}` (secrets auto-encrypted; `***` keeps stored value) | `connectors.manage` |
| POST | `/connectors/{id}/health-check` | `connectors.read` |
| POST | `/connectors/{id}/run?mode=Full|Incremental` (202 via bus, inline without bus) | `connectors.manage` |

## Operations
| Method | Path | Permission |
|---|---|---|
| GET | `/dashboard/summary`, `/assets-by-type`, `/assets-by-os`, `/assets-by-environment`, `/missing-controls`, `/asset-growth`, `/connector-health`, `/top-risks` | `assets.read` etc. |
| GET/PATCH | `/incidents`, `/incidents/{id}` | `incidents.read` / `incidents.manage` |
| GET/POST | `/reports`, `/reports/types`, `/reports/generate`, `/reports/{id}/download` | `reports.read` / `reports.generate` |
| GET | `/audit` (action/user/time filters) | `audit.read` |
| GET/PUT | `/notifications`, `/notifications/templates/{id}` | `notifications.manage` |

## Administration
| Method | Path | Permission |
|---|---|---|
| GET/POST/PUT | `/users`, `/users/{id}` | `users.manage` |
| GET/POST/PUT | `/roles`, `/roles/{id}`, `/roles/permissions` | `roles.manage` |
| GET/PUT | `/settings`, `/settings/{key}`, `/settings/source-priorities`, `/settings/source-priorities/{id}` | `settings.manage` |

Health probes (anonymous): `GET /health/live`, `GET /health/ready`.
