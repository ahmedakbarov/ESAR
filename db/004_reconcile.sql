-- ============================================================================
-- ESAR — schema reconcile 004
-- Brings the hand-maintained SQL schema (001–003) back in line with the EF Core
-- model in Esar.Infrastructure (EsarDbContext + Domain/Entities). These columns
-- were added to the entities after 001/003 were written, so a production install
-- that runs only the SQL scripts (Database:AutoMigrate=false) was missing them —
-- the application would fail at runtime with "column does not exist".
--
-- Safe to run repeatedly: every statement is guarded with IF NOT EXISTS. The
-- DEFAULT clauses backfill existing rows; they mirror the entity defaults so the
-- SQL-provisioned schema is identical to the one EnsureCreated builds in dev.
--
-- Apply after 003_v2_features.sql:
--   psql -f db/004_reconcile.sql
-- ============================================================================

-- assets: data-quality engine outputs (Asset.DataQualityScore / DataQualityIssuesJson)
ALTER TABLE assets ADD COLUMN IF NOT EXISTS "DataQualityScore"      numeric(5,2) NOT NULL DEFAULT 100;
ALTER TABLE assets ADD COLUMN IF NOT EXISTS "DataQualityIssuesJson" jsonb        NOT NULL DEFAULT '[]';

-- asset_compliance: remediation workflow + originating policy
-- (AssetCompliance.RemediationState/RemediationNotes/RemediationAssignee/PolicyId).
-- RemediationState is an enum persisted as text; default matches RemediationState.None.
ALTER TABLE asset_compliance ADD COLUMN IF NOT EXISTS "RemediationState"    text NOT NULL DEFAULT 'None';
ALTER TABLE asset_compliance ADD COLUMN IF NOT EXISTS "RemediationNotes"    text;
ALTER TABLE asset_compliance ADD COLUMN IF NOT EXISTS "RemediationAssignee" text;
ALTER TABLE asset_compliance ADD COLUMN IF NOT EXISTS "PolicyId"            uuid;

-- matching_rules: rule versioning for audit/explainability (MatchingRule.Version)
ALTER TABLE matching_rules ADD COLUMN IF NOT EXISTS "Version" integer NOT NULL DEFAULT 1;
