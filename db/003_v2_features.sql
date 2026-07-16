-- ============================================================================
-- ESAR v2 — Enterprise Cyber Asset Management upgrade
-- Adds: relationship graph, security-baseline policies, approval workflow,
-- compliance remediation workflow, data-quality columns, rule versioning.
-- Idempotent: safe to run on both fresh and v1 databases.
-- ============================================================================

-- Asset: data quality columns
ALTER TABLE assets ADD COLUMN IF NOT EXISTS "DataQualityScore" numeric(5,2) NOT NULL DEFAULT 100;
ALTER TABLE assets ADD COLUMN IF NOT EXISTS "DataQualityIssuesJson" jsonb NOT NULL DEFAULT '[]';

-- Compliance: remediation workflow columns
ALTER TABLE asset_compliance ADD COLUMN IF NOT EXISTS "RemediationState" text NOT NULL DEFAULT 'None';
ALTER TABLE asset_compliance ADD COLUMN IF NOT EXISTS "RemediationNotes" text;
ALTER TABLE asset_compliance ADD COLUMN IF NOT EXISTS "RemediationAssignee" text;
ALTER TABLE asset_compliance ADD COLUMN IF NOT EXISTS "PolicyId" uuid;

-- Matching rules: versioning
ALTER TABLE matching_rules ADD COLUMN IF NOT EXISTS "Version" integer NOT NULL DEFAULT 1;

-- Relationship graph
CREATE TABLE IF NOT EXISTS asset_relationships (
    "Id"            uuid PRIMARY KEY,
    "SourceAssetId" uuid NOT NULL REFERENCES assets ("Id") ON DELETE CASCADE,
    "TargetAssetId" uuid NOT NULL REFERENCES assets ("Id") ON DELETE CASCADE,
    "Type"          text NOT NULL,
    "Description"   text,
    "Source"        text NOT NULL DEFAULT 'ManualImport',
    "IsActive"      boolean NOT NULL DEFAULT true,
    "LastSeen"      timestamptz NOT NULL,
    "CreatedAt"     timestamptz NOT NULL,
    "UpdatedAt"     timestamptz NOT NULL,
    "CreatedBy"     text,
    "UpdatedBy"     text
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_asset_relationships
    ON asset_relationships ("SourceAssetId", "TargetAssetId", "Type");
CREATE INDEX IF NOT EXISTS ix_asset_relationships_target ON asset_relationships ("TargetAssetId");

-- Security baseline policies
CREATE TABLE IF NOT EXISTS compliance_policies (
    "Id"                        uuid PRIMARY KEY,
    "Name"                      varchar(128) NOT NULL,
    "Description"               text,
    "Enabled"                   boolean NOT NULL DEFAULT true,
    "Priority"                  integer NOT NULL DEFAULT 100,
    "AppliesToAssetTypesJson"   jsonb NOT NULL DEFAULT '[]',
    "AppliesToEnvironmentsJson" jsonb NOT NULL DEFAULT '[]',
    "MinCriticality"            text,
    "RequiredControlsJson"      jsonb NOT NULL DEFAULT '[]',
    "MandatoryControlsJson"     jsonb NOT NULL DEFAULT '[]',
    "Version"                   integer NOT NULL DEFAULT 1,
    "CreatedAt"                 timestamptz NOT NULL,
    "UpdatedAt"                 timestamptz NOT NULL,
    "CreatedBy"                 text,
    "UpdatedBy"                 text
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_compliance_policies_name ON compliance_policies ("Name");

-- Approval workflow
CREATE TABLE IF NOT EXISTS approval_requests (
    "Id"              uuid PRIMARY KEY,
    "Type"            text NOT NULL,
    "Status"          text NOT NULL DEFAULT 'Pending',
    "AssetId"         uuid REFERENCES assets ("Id") ON DELETE SET NULL,
    "PayloadJson"     jsonb NOT NULL DEFAULT '{}',
    "RequestedBy"     text NOT NULL DEFAULT 'system',
    "Justification"   text,
    "DecidedBy"       text,
    "DecidedAt"       timestamptz,
    "DecisionComment" text,
    "CreatedAt"       timestamptz NOT NULL,
    "UpdatedAt"       timestamptz NOT NULL,
    "CreatedBy"       text,
    "UpdatedBy"       text
);
CREATE INDEX IF NOT EXISTS ix_approval_requests_status ON approval_requests ("Status", "CreatedAt");

-- New settings
INSERT INTO settings ("Id", "Key", "Value", "Description", "IsEncrypted", "CreatedAt", "UpdatedAt")
VALUES
  (gen_random_uuid(), 'approval.requireForNewAssets', 'false',
   'When true, newly discovered assets stay in Planned lifecycle until an owner approves them', false, now(), now()),
  (gen_random_uuid(), 'dataquality.alertBelowScore', '50',
   'Publish DataQualityDegraded event when an asset scores below this', false, now(), now())
ON CONFLICT ("Key") DO NOTHING;

-- Default security-baseline policies
INSERT INTO compliance_policies ("Id", "Name", "Description", "Enabled", "Priority",
    "AppliesToAssetTypesJson", "AppliesToEnvironmentsJson", "RequiredControlsJson",
    "MandatoryControlsJson", "CreatedAt", "UpdatedAt", "CreatedBy")
VALUES
  (gen_random_uuid(), 'Windows/Linux Server Baseline', 'Full control set for server operating systems',
   true, 10,
   '["WindowsServer","LinuxServer","PhysicalServer","VirtualMachine"]', '[]',
   '["SiemLogSource","Edr","Antivirus","VulnerabilityScanner","MonitoringAgent","BackupAgent","PatchStatus","DiskEncryption","AssetClassification"]',
   '["SiemLogSource","Edr","VulnerabilityScanner"]', now(), now(), 'seed'),
  (gen_random_uuid(), 'Cloud Instance Baseline', 'Cloud workloads baseline',
   true, 20,
   '["CloudInstance","Container","KubernetesNode"]', '[]',
   '["SiemLogSource","Edr","VulnerabilityScanner","MonitoringAgent","PatchStatus","AssetClassification"]',
   '["SiemLogSource","VulnerabilityScanner"]', now(), now(), 'seed'),
  (gen_random_uuid(), 'Workstation Baseline', 'End-user devices baseline',
   true, 30,
   '["Workstation"]', '[]',
   '["Edr","Antivirus","PatchStatus","DiskEncryption","AssetClassification"]',
   '["Edr","Antivirus"]', now(), now(), 'seed'),
  (gen_random_uuid(), 'Network Device Baseline', 'Network gear baseline (agentless)',
   true, 40,
   '["NetworkDevice","Firewall","LoadBalancer","Switch","Router"]', '[]',
   '["SiemLogSource","BackupAgent","PatchStatus","AssetClassification"]',
   '["SiemLogSource"]', now(), now(), 'seed'),
  (gen_random_uuid(), 'Database & Storage Baseline', 'Data platform baseline',
   true, 50,
   '["Database","StorageSystem"]', '[]',
   '["SiemLogSource","BackupAgent","MonitoringAgent","DiskEncryption","AssetClassification"]',
   '["SiemLogSource","BackupAgent"]', now(), now(), 'seed')
ON CONFLICT ("Name") DO NOTHING;

-- New permissions
INSERT INTO permissions ("Id", "Code", "CreatedAt", "UpdatedAt")
VALUES
  (gen_random_uuid(), 'compliance.manage', now(), now()),
  (gen_random_uuid(), 'policies.manage', now(), now()),
  (gen_random_uuid(), 'relationships.manage', now(), now()),
  (gen_random_uuid(), 'approvals.decide', now(), now())
ON CONFLICT ("Code") DO NOTHING;
