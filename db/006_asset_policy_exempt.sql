-- ============================================================================
-- ESAR — schema increment 006
-- Adds assets."PolicyExempt": operator-set flag that excludes an asset from
-- compliance policy evaluation (ComplianceEngine short-circuits, score/status
-- reset to 0/Unknown) without deleting or unmatching it — the asset keeps
-- syncing normally, it just stops being checked against required controls.
--
-- Safe to run repeatedly. Apply in the SAME deploy window as the code that
-- reads this column (same reasoning as 005 — EF selects every mapped Asset
-- column on every query).
--   psql -f db/006_asset_policy_exempt.sql
-- ============================================================================

ALTER TABLE assets ADD COLUMN IF NOT EXISTS "PolicyExempt" boolean NOT NULL DEFAULT false;
