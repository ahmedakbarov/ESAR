-- Match/merge safety upgrade. Idempotent and preserves all existing assets.

CREATE TABLE IF NOT EXISTS asset_identifiers (
    "Id"              uuid PRIMARY KEY,
    "AssetId"         uuid NOT NULL REFERENCES assets ("Id") ON DELETE CASCADE,
    "Namespace"       varchar(128) NOT NULL,
    "Value"           varchar(512) NOT NULL,
    "NormalizedValue" varchar(512) NOT NULL,
    "Source"           text NOT NULL,
    "FirstSeen"       timestamptz NOT NULL,
    "LastSeen"        timestamptz NOT NULL,
    "IsActive"        boolean NOT NULL DEFAULT true,
    "CreatedAt"       timestamptz NOT NULL,
    "UpdatedAt"       timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_asset_identifiers_lookup
    ON asset_identifiers ("Namespace", "NormalizedValue");
CREATE UNIQUE INDEX IF NOT EXISTS ux_asset_identifiers_observation
    ON asset_identifiers ("AssetId", "Namespace", "NormalizedValue", "Source");

ALTER TABLE asset_ips ADD COLUMN IF NOT EXISTS "FirstSeen" timestamptz;
UPDATE asset_ips SET "FirstSeen" = COALESCE("FirstSeen", "LastSeen", "CreatedAt", now());
ALTER TABLE asset_ips ALTER COLUMN "FirstSeen" SET NOT NULL;
ALTER TABLE asset_ips ALTER COLUMN "FirstSeen" SET DEFAULT now();
ALTER TABLE asset_ips ADD COLUMN IF NOT EXISTS "ValidTo" timestamptz;
ALTER TABLE asset_ips ADD COLUMN IF NOT EXISTS "IsActive" boolean NOT NULL DEFAULT true;

-- Backfill durable canonical identifiers already present in golden records.
INSERT INTO asset_identifiers
    ("Id", "AssetId", "Namespace", "Value", "NormalizedValue", "Source",
     "FirstSeen", "LastSeen", "IsActive", "CreatedAt", "UpdatedAt")
SELECT gen_random_uuid(), a."Id", 'AzureResourceId', a."CloudResourceId", lower(trim(a."CloudResourceId")),
       'Azure', a."FirstSeen", a."LastSeen", true, now(), now()
FROM assets a
WHERE a."CloudResourceId" IS NOT NULL AND trim(a."CloudResourceId") <> ''
ON CONFLICT DO NOTHING;

INSERT INTO asset_identifiers
    ("Id", "AssetId", "Namespace", "Value", "NormalizedValue", "Source",
     "FirstSeen", "LastSeen", "IsActive", "CreatedAt", "UpdatedAt")
SELECT gen_random_uuid(), a."Id", 'BiosUuid', a."BiosUuid", lower(trim(a."BiosUuid")),
       COALESCE((SELECT s."ConnectorType" FROM asset_sources s WHERE s."AssetId" = a."Id"
                 ORDER BY s."LastSeen" DESC LIMIT 1), 'ManualImport'),
       a."FirstSeen", a."LastSeen", true, now(), now()
FROM assets a
WHERE a."BiosUuid" IS NOT NULL AND trim(a."BiosUuid") <> ''
ON CONFLICT DO NOTHING;

INSERT INTO asset_identifiers
    ("Id", "AssetId", "Namespace", "Value", "NormalizedValue", "Source",
     "FirstSeen", "LastSeen", "IsActive", "CreatedAt", "UpdatedAt")
SELECT gen_random_uuid(), a."Id", 'SerialNumber', a."SerialNumber", upper(trim(a."SerialNumber")),
       COALESCE((SELECT s."ConnectorType" FROM asset_sources s WHERE s."AssetId" = a."Id"
                 ORDER BY s."LastSeen" DESC LIMIT 1), 'ManualImport'),
       a."FirstSeen", a."LastSeen", true, now(), now()
FROM assets a
WHERE a."SerialNumber" IS NOT NULL AND trim(a."SerialNumber") <> ''
ON CONFLICT DO NOTHING;

-- Replace ambiguous legacy namespaces. Existing source links remain untouched.
ALTER TABLE matching_rules ADD COLUMN IF NOT EXISTS "Version" integer NOT NULL DEFAULT 1;
UPDATE matching_rules SET "Enabled" = false, "Version" = "Version" + 1, "UpdatedAt" = now()
WHERE "Attribute" IN ('ObjectGuid', 'EndpointId')
  AND "Enabled" = true;

INSERT INTO matching_rules
    ("Id", "Name", "Attribute", "MatchType", "Weight", "Order", "Enabled", "Version",
     "CreatedAt", "UpdatedAt", "CreatedBy")
SELECT gen_random_uuid(), rule_name, attribute_name, 'Hard', 1.0, rule_order, true, 1, now(), now(), 'db:009'
FROM (VALUES
    ('AD Computer Object GUID', 'AdComputerObjectGuid', 60),
    ('Entra Device ID', 'EntraDeviceId', 61),
    ('Azure VM ID', 'AzureVmId', 62),
    ('Defender Machine ID', 'DefenderMachineId', 70),
    ('CrowdStrike Device ID', 'CrowdStrikeDeviceId', 71),
    ('SentinelOne Agent ID', 'SentinelOneAgentId', 72),
    ('Cortex Endpoint ID', 'CortexEndpointId', 73)
) AS rules(rule_name, attribute_name, rule_order)
WHERE NOT EXISTS (
    SELECT 1 FROM matching_rules existing WHERE existing."Attribute" = attribute_name
);
