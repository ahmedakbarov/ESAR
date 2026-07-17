# ESAR — User Guide

## Signing in
Open the portal, sign in with your ESAR account (or corporate credentials when Entra ID is enabled).
What you can see and do depends on your role — ask an administrator for access.

## Dashboard
Fleet KPIs at a glance: total/active assets, critical assets, cloud footprint, average compliance,
non-compliant count, open incidents, pending match reviews, stale assets, 30-day growth trend,
OS distribution and missing-control hotspots.

## Assets
- **Search & filter** by hostname, IP, MAC, owner, serial, OS, installed software, type,
  environment, criticality, compliance, source, tags and cloud provider. Enable *regex* mode for
  pattern searches (e.g. `^srv-db\d+`).
- **Asset detail** shows four scores (Compliance, Health, Data Quality, Risk), general/hardware/
  cloud metadata, per-control compliance with evidence, interfaces, contributing sources with their
  external IDs, tags, relationships with one-click **impact analysis** (what breaks if this asset
  fails, what it depends on), and the full change history/timeline.
- **Re-evaluate compliance** runs the policy engine for that asset immediately.

## Compliance
Status distribution, per-control coverage matrix, and the list of assets failing any selected
control. Non-compliant items carry a remediation state (e.g. *Waiting for SIEM Onboarding*) that
analysts move forward as work progresses; *Risk Accepted* documents an approved exception.

## Matching
The review queue holds ambiguous correlations. Expand a row to read the explanation (which rule
matched, with what weight). **Merge** = same asset; **New asset** = different asset. The rules
table shows current weights and versions.

## Approvals
When activation approval is enabled, newly discovered assets and requested merges wait here.
Approve (optionally setting owner/business unit/criticality) or reject.

## Incidents
Automatically generated findings (missing SIEM/EDR/scanner, offline critical assets, duplicate
IPs, connector failures) with severity, linked asset and external ticket number. Resolve directly
or work them in ServiceNow/Jira.

## Reports
Pick a type (Asset Inventory, Compliance, Missing SIEM/EDR, Duplicates, Inactive, Cloud, Owners,
Business Units, Changes, Executive Summary) and format (Excel/CSV/PDF), generate, download.
