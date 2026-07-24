-- ============================================================================
-- ESAR — Enterprise Security Asset Registry
-- PostgreSQL 16 schema (mirrors the EF Core model in Esar.Infrastructure)
--
-- Production installs: run this script, then 002_seed.sql, and set
-- Database:AutoMigrate=false in the application configuration.
-- Development installs: the application creates/seeds the schema itself.
-- ============================================================================

CREATE TABLE IF NOT EXISTS assets (
    "Id"                    uuid PRIMARY KEY,
    "Hostname"              varchar(255) NOT NULL,
    "NormalizedHostname"    varchar(255) NOT NULL DEFAULT '',
    "Fqdn"                  text,
    "Domain"                text,
    "OperatingSystem"       text,
    "OsVersion"             text,
    "AssetType"             text NOT NULL,
    "Status"                text NOT NULL,
    "LifecycleStatus"       text NOT NULL,
    "Environment"           text NOT NULL,
    "Criticality"           text NOT NULL,
    "OwnerName"             text,
    "OwnerEmail"            text,
    "Department"            text,
    "BusinessUnit"          text,
    "Location"              text,
    "Classification"        text,
    "SerialNumber"          text,
    "BiosUuid"              text,
    "Manufacturer"          text,
    "Model"                 text,
    "CloudProvider"         text,
    "CloudResourceId"       text,
    "CloudRegion"           text,
    "CloudSubscriptionId"   text,
    "CloudAccountId"        text,
    "HealthScore"           integer NOT NULL DEFAULT 100,
    "ComplianceScore"       numeric(5,2) NOT NULL DEFAULT 0,
    "ComplianceStatus"      text NOT NULL,
    "FirstSeen"             timestamptz NOT NULL,
    "LastSeen"              timestamptz NOT NULL,
    "IsDeleted"             boolean NOT NULL DEFAULT false,
    "MergedIntoAssetId"     uuid,
    "AttributeSourcesJson"  jsonb NOT NULL DEFAULT '{}',
    "CreatedAt"             timestamptz NOT NULL,
    "UpdatedAt"             timestamptz NOT NULL,
    "CreatedBy"             text,
    "UpdatedBy"             text
);
CREATE INDEX IF NOT EXISTS ix_assets_normalized_hostname ON assets ("NormalizedHostname");
CREATE INDEX IF NOT EXISTS ix_assets_serial ON assets ("SerialNumber");
CREATE INDEX IF NOT EXISTS ix_assets_bios_uuid ON assets ("BiosUuid");
CREATE INDEX IF NOT EXISTS ix_assets_cloud_resource ON assets ("CloudResourceId");
CREATE INDEX IF NOT EXISTS ix_assets_status ON assets ("Status", "IsDeleted");
CREATE INDEX IF NOT EXISTS ix_assets_last_seen ON assets ("LastSeen");

CREATE TABLE IF NOT EXISTS asset_sources (
    "Id"              uuid PRIMARY KEY,
    "AssetId"         uuid NOT NULL REFERENCES assets ("Id") ON DELETE CASCADE,
    "ConnectorType"   text NOT NULL,
    "ExternalId"      varchar(512) NOT NULL,
    "SourceHostname"  text,
    "RawData"         jsonb,
    "FirstSeen"       timestamptz NOT NULL,
    "LastSeen"        timestamptz NOT NULL,
    "IsAuthoritative" boolean NOT NULL DEFAULT false,
    "CreatedAt"       timestamptz NOT NULL,
    "UpdatedAt"       timestamptz NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_asset_sources_connector_external
    ON asset_sources ("ConnectorType", "ExternalId");
CREATE INDEX IF NOT EXISTS ix_asset_sources_asset ON asset_sources ("AssetId");

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

CREATE TABLE IF NOT EXISTS asset_ips (
    "Id"         uuid PRIMARY KEY,
    "AssetId"    uuid NOT NULL REFERENCES assets ("Id") ON DELETE CASCADE,
    "IpAddress"  varchar(64) NOT NULL,
    "MacAddress" varchar(17),
    "Network"    text,
    "IsPrimary"  boolean NOT NULL DEFAULT false,
    "Source"     text NOT NULL,
    "LastSeen"   timestamptz NOT NULL,
    "FirstSeen"  timestamptz NOT NULL,
    "ValidTo"    timestamptz,
    "IsActive"   boolean NOT NULL DEFAULT true,
    "CreatedAt"  timestamptz NOT NULL,
    "UpdatedAt"  timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_asset_ips_ip ON asset_ips ("IpAddress");
CREATE INDEX IF NOT EXISTS ix_asset_ips_mac ON asset_ips ("MacAddress");
CREATE INDEX IF NOT EXISTS ix_asset_ips_asset ON asset_ips ("AssetId");

CREATE TABLE IF NOT EXISTS asset_tags (
    "Id"        uuid PRIMARY KEY,
    "AssetId"   uuid NOT NULL REFERENCES assets ("Id") ON DELETE CASCADE,
    "Key"       varchar(128) NOT NULL,
    "Value"     varchar(1024) NOT NULL DEFAULT '',
    "Source"    text NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    "UpdatedAt" timestamptz NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_asset_tags_asset_key ON asset_tags ("AssetId", "Key");

CREATE TABLE IF NOT EXISTS asset_history (
    "Id"        uuid PRIMARY KEY,
    "AssetId"   uuid NOT NULL REFERENCES assets ("Id") ON DELETE CASCADE,
    "FieldName" varchar(128) NOT NULL,
    "OldValue"  text,
    "NewValue"  text,
    "ChangedBy" text NOT NULL DEFAULT 'system',
    "Source"    text,
    "ChangedAt" timestamptz NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    "UpdatedAt" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_asset_history_asset_time ON asset_history ("AssetId", "ChangedAt");

CREATE TABLE IF NOT EXISTS asset_software (
    "Id"          uuid PRIMARY KEY,
    "AssetId"     uuid NOT NULL REFERENCES assets ("Id") ON DELETE CASCADE,
    "Name"        varchar(512) NOT NULL,
    "Version"     text,
    "Vendor"      text,
    "InstallDate" timestamptz,
    "Source"      text NOT NULL,
    "LastSeen"    timestamptz NOT NULL,
    "CreatedAt"   timestamptz NOT NULL,
    "UpdatedAt"   timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_asset_software_lookup ON asset_software ("AssetId", "Name", "Source");

CREATE TABLE IF NOT EXISTS asset_compliance (
    "Id"             uuid PRIMARY KEY,
    "AssetId"        uuid NOT NULL REFERENCES assets ("Id") ON DELETE CASCADE,
    "Control"        text NOT NULL,
    "Status"         text NOT NULL,
    "Details"        text,
    "EvidenceSource" text,
    "CheckedAt"      timestamptz NOT NULL,
    "CreatedAt"      timestamptz NOT NULL,
    "UpdatedAt"      timestamptz NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_asset_compliance_asset_control
    ON asset_compliance ("AssetId", "Control");

CREATE TABLE IF NOT EXISTS asset_events (
    "Id"         uuid PRIMARY KEY,
    "AssetId"    uuid NOT NULL REFERENCES assets ("Id") ON DELETE CASCADE,
    "EventType"  text NOT NULL,
    "Payload"    jsonb,
    "Source"     text,
    "OccurredAt" timestamptz NOT NULL,
    "CreatedAt"  timestamptz NOT NULL,
    "UpdatedAt"  timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_asset_events_asset_time ON asset_events ("AssetId", "OccurredAt");

CREATE TABLE IF NOT EXISTS asset_risk (
    "Id"                      uuid PRIMARY KEY,
    "AssetId"                 uuid NOT NULL REFERENCES assets ("Id") ON DELETE CASCADE,
    "RiskScore"               numeric(6,2) NOT NULL DEFAULT 0,
    "VulnerabilitiesCritical" integer NOT NULL DEFAULT 0,
    "VulnerabilitiesHigh"     integer NOT NULL DEFAULT 0,
    "VulnerabilitiesMedium"   integer NOT NULL DEFAULT 0,
    "VulnerabilitiesLow"      integer NOT NULL DEFAULT 0,
    "ExposureScore"           numeric(6,2) NOT NULL DEFAULT 0,
    "LastCalculatedAt"        timestamptz NOT NULL,
    "CreatedAt"               timestamptz NOT NULL,
    "UpdatedAt"               timestamptz NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_asset_risk_asset ON asset_risk ("AssetId");

CREATE TABLE IF NOT EXISTS connectors (
    "Id"                 uuid PRIMARY KEY,
    "Name"               varchar(128) NOT NULL,
    "Type"               text NOT NULL,
    "Enabled"            boolean NOT NULL DEFAULT true,
    "CronSchedule"       text NOT NULL DEFAULT '0 */4 * * *',
    "Priority"           integer NOT NULL DEFAULT 100,
    "SettingsJson"       jsonb NOT NULL DEFAULT '{}',
    "DefaultSyncMode"    text NOT NULL,
    "MaxRetries"         integer NOT NULL DEFAULT 3,
    "RateLimitPerMinute" integer NOT NULL DEFAULT 300,
    "LastRunAt"          timestamptz,
    "LastRunStatus"      text,
    "IsHealthy"          boolean NOT NULL DEFAULT true,
    "LastHealthMessage"  text,
    "CreatedAt"          timestamptz NOT NULL,
    "UpdatedAt"          timestamptz NOT NULL,
    "CreatedBy"          text,
    "UpdatedBy"          text
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_connectors_name ON connectors ("Name");

CREATE TABLE IF NOT EXISTS connector_jobs (
    "Id"               uuid PRIMARY KEY,
    "ConnectorId"      uuid NOT NULL REFERENCES connectors ("Id") ON DELETE CASCADE,
    "Status"           text NOT NULL,
    "SyncMode"         text NOT NULL,
    "StartedAt"        timestamptz,
    "CompletedAt"      timestamptz,
    "AssetsDiscovered" integer NOT NULL DEFAULT 0,
    "AssetsCreated"    integer NOT NULL DEFAULT 0,
    "AssetsUpdated"    integer NOT NULL DEFAULT 0,
    "AssetsFailed"     integer NOT NULL DEFAULT 0,
    "RetryCount"       integer NOT NULL DEFAULT 0,
    "ErrorMessage"     text,
    "Log"              jsonb,
    "TriggeredBy"      text NOT NULL DEFAULT 'scheduler',
    "CreatedAt"        timestamptz NOT NULL,
    "UpdatedAt"        timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_connector_jobs_connector_time ON connector_jobs ("ConnectorId", "CreatedAt");

CREATE TABLE IF NOT EXISTS source_priorities (
    "Id"            uuid PRIMARY KEY,
    "ConnectorType" text NOT NULL,
    "Attribute"     varchar(128),
    "Priority"      integer NOT NULL,
    "CreatedAt"     timestamptz NOT NULL,
    "UpdatedAt"     timestamptz NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_source_priorities ON source_priorities ("ConnectorType", "Attribute");

CREATE TABLE IF NOT EXISTS matching_rules (
    "Id"        uuid PRIMARY KEY,
    "Name"      varchar(128) NOT NULL,
    "Attribute" varchar(128) NOT NULL,
    "MatchType" text NOT NULL,
    "Weight"    numeric(5,4) NOT NULL,
    "Order"     integer NOT NULL,
    "Enabled"   boolean NOT NULL DEFAULT true,
    "CreatedAt" timestamptz NOT NULL,
    "UpdatedAt" timestamptz NOT NULL,
    "CreatedBy" text,
    "UpdatedBy" text
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_matching_rules_name ON matching_rules ("Name");

CREATE TABLE IF NOT EXISTS match_records (
    "Id"                uuid PRIMARY KEY,
    "SourceConnector"   text NOT NULL,
    "ExternalId"        text NOT NULL,
    "CandidateHostname" text,
    "MatchedAssetId"    uuid REFERENCES assets ("Id") ON DELETE SET NULL,
    "CreatedAssetId"    uuid,
    "ConfidenceScore"   numeric(5,4) NOT NULL DEFAULT 0,
    "MatchType"         text,
    "Decision"          text NOT NULL,
    "ExplanationJson"   jsonb NOT NULL DEFAULT '[]',
    "CandidateJson"     jsonb,
    "ReviewedBy"        text,
    "ReviewedAt"        timestamptz,
    "ReviewComment"     text,
    "CreatedAt"         timestamptz NOT NULL,
    "UpdatedAt"         timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_match_records_source ON match_records ("SourceConnector", "ExternalId");
CREATE INDEX IF NOT EXISTS ix_match_records_decision ON match_records ("Decision");

CREATE TABLE IF NOT EXISTS users (
    "Id"                  uuid PRIMARY KEY,
    "Username"            varchar(128) NOT NULL,
    "Email"               varchar(320) NOT NULL,
    "DisplayName"         text NOT NULL DEFAULT '',
    "PasswordHash"        text,
    "AuthProvider"        text NOT NULL,
    "ExternalObjectId"    text,
    "IsActive"            boolean NOT NULL DEFAULT true,
    "MfaEnabled"          boolean NOT NULL DEFAULT false,
    "LastLoginAt"         timestamptz,
    "FailedLoginAttempts" integer NOT NULL DEFAULT 0,
    "LockedOutUntil"      timestamptz,
    "CreatedAt"           timestamptz NOT NULL,
    "UpdatedAt"           timestamptz NOT NULL,
    "CreatedBy"           text,
    "UpdatedBy"           text
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_users_username ON users ("Username");
CREATE UNIQUE INDEX IF NOT EXISTS ux_users_email ON users ("Email");

CREATE TABLE IF NOT EXISTS roles (
    "Id"          uuid PRIMARY KEY,
    "Name"        varchar(64) NOT NULL,
    "Description" text,
    "IsSystem"    boolean NOT NULL DEFAULT false,
    "CreatedAt"   timestamptz NOT NULL,
    "UpdatedAt"   timestamptz NOT NULL,
    "CreatedBy"   text,
    "UpdatedBy"   text
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_roles_name ON roles ("Name");

CREATE TABLE IF NOT EXISTS permissions (
    "Id"          uuid PRIMARY KEY,
    "Code"        varchar(128) NOT NULL,
    "Description" text,
    "CreatedAt"   timestamptz NOT NULL,
    "UpdatedAt"   timestamptz NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_permissions_code ON permissions ("Code");

CREATE TABLE IF NOT EXISTS user_roles (
    "UserId" uuid NOT NULL REFERENCES users ("Id") ON DELETE CASCADE,
    "RoleId" uuid NOT NULL REFERENCES roles ("Id") ON DELETE CASCADE,
    PRIMARY KEY ("UserId", "RoleId")
);

CREATE TABLE IF NOT EXISTS role_permissions (
    "RoleId"       uuid NOT NULL REFERENCES roles ("Id") ON DELETE CASCADE,
    "PermissionId" uuid NOT NULL REFERENCES permissions ("Id") ON DELETE CASCADE,
    PRIMARY KEY ("RoleId", "PermissionId")
);

CREATE TABLE IF NOT EXISTS audit_logs (
    "Id"         uuid PRIMARY KEY,
    "UserId"     uuid,
    "UserName"   text NOT NULL DEFAULT 'system',
    "Action"     text NOT NULL,
    "EntityType" text,
    "EntityId"   text,
    "Details"    jsonb,
    "IpAddress"  text,
    "UserAgent"  text,
    "Timestamp"  timestamptz NOT NULL,
    "CreatedAt"  timestamptz NOT NULL,
    "UpdatedAt"  timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_audit_logs_time ON audit_logs ("Timestamp");
CREATE INDEX IF NOT EXISTS ix_audit_logs_entity ON audit_logs ("EntityType", "EntityId");

CREATE TABLE IF NOT EXISTS notifications (
    "Id"                uuid PRIMARY KEY,
    "Channel"           text NOT NULL,
    "Recipient"         text NOT NULL,
    "Subject"           text NOT NULL DEFAULT '',
    "Body"              text NOT NULL DEFAULT '',
    "Status"            text NOT NULL,
    "RetryCount"        integer NOT NULL DEFAULT 0,
    "SentAt"            timestamptz,
    "Error"             text,
    "RelatedEntityType" text,
    "RelatedEntityId"   text,
    "CreatedAt"         timestamptz NOT NULL,
    "UpdatedAt"         timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_notifications_status ON notifications ("Status", "CreatedAt");

CREATE TABLE IF NOT EXISTS notification_templates (
    "Id"              uuid PRIMARY KEY,
    "Name"            varchar(128) NOT NULL,
    "Channel"         text NOT NULL,
    "SubjectTemplate" text NOT NULL DEFAULT '',
    "BodyTemplate"    text NOT NULL DEFAULT '',
    "Enabled"         boolean NOT NULL DEFAULT true,
    "CreatedAt"       timestamptz NOT NULL,
    "UpdatedAt"       timestamptz NOT NULL,
    "CreatedBy"       text,
    "UpdatedBy"       text
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_notification_templates_name ON notification_templates ("Name");

CREATE TABLE IF NOT EXISTS escalation_rules (
    "Id"                   uuid PRIMARY KEY,
    "MinSeverity"          text NOT NULL,
    "EscalateAfterMinutes" integer NOT NULL DEFAULT 60,
    "Channel"              text NOT NULL,
    "Recipient"            text NOT NULL DEFAULT '',
    "Enabled"              boolean NOT NULL DEFAULT true,
    "CreatedAt"            timestamptz NOT NULL,
    "UpdatedAt"            timestamptz NOT NULL,
    "CreatedBy"            text,
    "UpdatedBy"            text
);

CREATE TABLE IF NOT EXISTS incidents (
    "Id"               uuid PRIMARY KEY,
    "Type"             text NOT NULL,
    "Severity"         text NOT NULL,
    "Status"           text NOT NULL,
    "Title"            text NOT NULL DEFAULT '',
    "Description"      text NOT NULL DEFAULT '',
    "AssetId"          uuid REFERENCES assets ("Id") ON DELETE SET NULL,
    "ConnectorId"      uuid,
    "ExternalTicketId" text,
    "ExternalSystem"   text,
    "AssignedTo"       text,
    "ResolvedAt"       timestamptz,
    "EscalatedAt"      timestamptz,
    "DedupKey"         varchar(256) NOT NULL,
    "CreatedAt"        timestamptz NOT NULL,
    "UpdatedAt"        timestamptz NOT NULL,
    "CreatedBy"        text,
    "UpdatedBy"        text
);
CREATE INDEX IF NOT EXISTS ix_incidents_dedup ON incidents ("DedupKey", "Status");
CREATE INDEX IF NOT EXISTS ix_incidents_status_severity ON incidents ("Status", "Severity");

CREATE TABLE IF NOT EXISTS reports (
    "Id"             uuid PRIMARY KEY,
    "Name"           text NOT NULL DEFAULT '',
    "Type"           text NOT NULL,
    "Format"         text NOT NULL,
    "ParametersJson" jsonb,
    "Status"         text NOT NULL,
    "FilePath"       text,
    "GeneratedAt"    timestamptz,
    "GeneratedBy"    text,
    "Error"          text,
    "CreatedAt"      timestamptz NOT NULL,
    "UpdatedAt"      timestamptz NOT NULL,
    "CreatedBy"      text,
    "UpdatedBy"      text
);

CREATE TABLE IF NOT EXISTS settings (
    "Id"          uuid PRIMARY KEY,
    "Key"         varchar(256) NOT NULL,
    "Value"       text NOT NULL DEFAULT '',
    "Description" text,
    "IsEncrypted" boolean NOT NULL DEFAULT false,
    "UpdatedBy"   text,
    "CreatedAt"   timestamptz NOT NULL,
    "UpdatedAt"   timestamptz NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_settings_key ON settings ("Key");
