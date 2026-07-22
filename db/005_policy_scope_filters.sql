-- ============================================================================
-- ESAR — schema increment 005
-- Adds scope-filter columns to compliance_policies: connector type, tags,
-- hostname glob patterns, IP CIDR ranges, cloud subscription id. Empty array
-- means unconstrained (same semantics as the pre-existing
-- AppliesToAssetTypesJson / AppliesToEnvironmentsJson columns).
--
-- Safe to run repeatedly (ADD COLUMN IF NOT EXISTS). Apply after
-- 004_reconcile.sql, in the SAME deploy window as the application code that
-- reads these columns — EF selects every mapped CompliancePolicy column on
-- every GET /policies call, not just calls that use the new filters, so a
-- code deploy without this migration breaks the endpoint entirely.
--   psql -f db/005_policy_scope_filters.sql
-- ============================================================================

ALTER TABLE compliance_policies ADD COLUMN IF NOT EXISTS "AppliesToConnectorsJson"       jsonb NOT NULL DEFAULT '[]';
ALTER TABLE compliance_policies ADD COLUMN IF NOT EXISTS "AppliesToTagsJson"             jsonb NOT NULL DEFAULT '[]';
ALTER TABLE compliance_policies ADD COLUMN IF NOT EXISTS "AppliesToHostnamePatternsJson" jsonb NOT NULL DEFAULT '[]';
ALTER TABLE compliance_policies ADD COLUMN IF NOT EXISTS "AppliesToIpRangesJson"         jsonb NOT NULL DEFAULT '[]';
ALTER TABLE compliance_policies ADD COLUMN IF NOT EXISTS "AppliesToSubscriptionsJson"    jsonb NOT NULL DEFAULT '[]';
