-- Asset groups: manually-curated collections of assets that a compliance policy can target
-- directly (via compliance_policies."AppliesToGroupsJson"). Idempotent and additive.

CREATE TABLE IF NOT EXISTS asset_groups (
    "Id"          uuid PRIMARY KEY,
    "Name"        varchar(128) NOT NULL,
    "Description" varchar(1024),
    "CreatedAt"   timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt"   timestamptz NOT NULL DEFAULT now(),
    "CreatedBy"   text,
    "UpdatedBy"   text
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_asset_groups_name ON asset_groups ("Name");

CREATE TABLE IF NOT EXISTS asset_group_members (
    "AssetGroupId" uuid NOT NULL REFERENCES asset_groups ("Id") ON DELETE CASCADE,
    "AssetId"      uuid NOT NULL REFERENCES assets ("Id") ON DELETE CASCADE,
    "AddedAt"      timestamptz NOT NULL DEFAULT now(),
    "AddedBy"      text NOT NULL DEFAULT 'system',
    PRIMARY KEY ("AssetGroupId", "AssetId")
);
CREATE INDEX IF NOT EXISTS ix_asset_group_members_asset ON asset_group_members ("AssetId");

ALTER TABLE compliance_policies
    ADD COLUMN IF NOT EXISTS "AppliesToGroupsJson" jsonb NOT NULL DEFAULT '[]';
