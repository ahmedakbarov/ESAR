-- ============================================================================
-- ESAR reference data seed (idempotent).
-- The application seeds the same data on first start when Database:AutoMigrate=true;
-- this script exists for DBA-managed production installs.
-- The admin user is created by the application (bcrypt hash) using the
-- ESAR_ADMIN_INITIAL_PASSWORD environment variable.
-- ============================================================================

-- Matching rules (default priority order)
INSERT INTO matching_rules ("Id", "Name", "Attribute", "MatchType", "Weight", "Order", "Enabled", "CreatedAt", "UpdatedAt", "CreatedBy")
VALUES
  (gen_random_uuid(), 'Azure Resource ID', 'AzureResourceId', 'Hard', 1.0000, 10,  true, now(), now(), 'seed'),
  (gen_random_uuid(), 'AWS Instance ID',   'AwsInstanceId',   'Hard', 1.0000, 20,  true, now(), now(), 'seed'),
  (gen_random_uuid(), 'VMware UUID',       'VmwareUuid',      'Hard', 1.0000, 30,  true, now(), now(), 'seed'),
  (gen_random_uuid(), 'BIOS UUID',         'BiosUuid',        'Hard', 1.0000, 40,  true, now(), now(), 'seed'),
  (gen_random_uuid(), 'Serial Number',     'SerialNumber',    'Hard', 1.0000, 50,  true, now(), now(), 'seed'),
  (gen_random_uuid(), 'AD Object GUID',    'ObjectGuid',      'Hard', 1.0000, 60,  true, now(), now(), 'seed'),
  (gen_random_uuid(), 'EDR Endpoint ID',   'EndpointId',      'Hard', 1.0000, 70,  true, now(), now(), 'seed'),
  (gen_random_uuid(), 'MAC Address',       'MacAddress',      'Soft', 0.3500, 80,  true, now(), now(), 'seed'),
  (gen_random_uuid(), 'Hostname',          'Hostname',        'Soft', 0.4000, 90,  true, now(), now(), 'seed'),
  (gen_random_uuid(), 'IP Address',        'IpAddress',       'Soft', 0.1500, 100, true, now(), now(), 'seed'),
  (gen_random_uuid(), 'Operating System',  'OperatingSystem', 'Soft', 0.0500, 110, true, now(), now(), 'seed'),
  (gen_random_uuid(), 'Domain',            'Domain',          'Soft', 0.0500, 120, true, now(), now(), 'seed')
ON CONFLICT ("Name") DO NOTHING;

-- Global source priorities (lower = more authoritative)
INSERT INTO source_priorities ("Id", "ConnectorType", "Attribute", "Priority", "CreatedAt", "UpdatedAt")
VALUES
  (gen_random_uuid(), 'Azure', NULL, 10, now(), now()),
  (gen_random_uuid(), 'Aws', NULL, 20, now(), now()),
  (gen_random_uuid(), 'GoogleCloud', NULL, 30, now(), now()),
  (gen_random_uuid(), 'VmwareVCenter', NULL, 40, now(), now()),
  (gen_random_uuid(), 'HyperV', NULL, 50, now(), now()),
  (gen_random_uuid(), 'ActiveDirectory', NULL, 60, now(), now()),
  (gen_random_uuid(), 'EntraId', NULL, 70, now(), now()),
  (gen_random_uuid(), 'MicrosoftDefender', NULL, 80, now(), now()),
  (gen_random_uuid(), 'CortexXdr', NULL, 90, now(), now()),
  (gen_random_uuid(), 'CrowdStrike', NULL, 100, now(), now()),
  (gen_random_uuid(), 'SentinelOne', NULL, 110, now(), now()),
  (gen_random_uuid(), 'Sccm', NULL, 120, now(), now()),
  (gen_random_uuid(), 'Intune', NULL, 130, now(), now()),
  (gen_random_uuid(), 'Qualys', NULL, 140, now(), now()),
  (gen_random_uuid(), 'Rapid7', NULL, 150, now(), now()),
  (gen_random_uuid(), 'Tenable', NULL, 160, now(), now()),
  (gen_random_uuid(), 'Nessus', NULL, 170, now(), now()),
  (gen_random_uuid(), 'ServiceNowCmdb', NULL, 180, now(), now()),
  (gen_random_uuid(), 'MicrosoftSentinel', NULL, 200, now(), now()),
  (gen_random_uuid(), 'QRadar', NULL, 210, now(), now()),
  (gen_random_uuid(), 'Splunk', NULL, 220, now(), now()),
  (gen_random_uuid(), 'Elastic', NULL, 230, now(), now()),
  (gen_random_uuid(), 'Dns', NULL, 300, now(), now()),
  (gen_random_uuid(), 'Dhcp', NULL, 310, now(), now()),
  (gen_random_uuid(), 'ManualImport', NULL, 400, now(), now()),
  (gen_random_uuid(), 'GenericRest', NULL, 500, now(), now()),
  -- Attribute-level overrides
  (gen_random_uuid(), 'MicrosoftDefender', 'OperatingSystem', 15, now(), now()),
  (gen_random_uuid(), 'CrowdStrike', 'OperatingSystem', 16, now(), now()),
  (gen_random_uuid(), 'ServiceNowCmdb', 'OwnerName', 5, now(), now()),
  (gen_random_uuid(), 'ServiceNowCmdb', 'OwnerEmail', 5, now(), now()),
  (gen_random_uuid(), 'ServiceNowCmdb', 'Department', 5, now(), now()),
  (gen_random_uuid(), 'ServiceNowCmdb', 'BusinessUnit', 5, now(), now())
ON CONFLICT ("ConnectorType", "Attribute") DO NOTHING;

-- Platform settings
INSERT INTO settings ("Id", "Key", "Value", "Description", "IsEncrypted", "CreatedAt", "UpdatedAt")
VALUES
  (gen_random_uuid(), 'matching.autoMergeThreshold', '0.85', 'Soft-match score required for automatic merge', false, now(), now()),
  (gen_random_uuid(), 'matching.reviewThreshold', '0.60', 'Soft-match score required to queue for manual review', false, now(), now()),
  (gen_random_uuid(), 'lifecycle.staleAssetDays', '7', 'Days without telemetry before an asset is marked Offline', false, now(), now()),
  (gen_random_uuid(), 'lifecycle.decommissionAfterDays', '90', 'Days without telemetry before automatic decommission', false, now(), now()),
  (gen_random_uuid(), 'compliance.evidenceMaxAgeDays', '7', 'Max age of source evidence for compliance controls', false, now(), now()),
  (gen_random_uuid(), 'notifications.defaultRecipient', 'soc@example.com', 'Default notification recipient', false, now(), now()),
  (gen_random_uuid(), 'reports.outputDirectory', '/data/reports', 'Directory where generated reports are stored', false, now(), now())
ON CONFLICT ("Key") DO NOTHING;
