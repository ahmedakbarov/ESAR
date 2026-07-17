-- ============================================================================
-- Stale connector-job recovery.
-- A Running job whose process died (restart/kill/OOM) used to block its
-- connector forever because of ux_connector_jobs_one_running. The application
-- now auto-recovers (runner takeover + worker reaper); this script adds the
-- timeout setting and closes any jobs already stuck. Idempotent.
-- ============================================================================

INSERT INTO settings ("Id", "Key", "Value", "Description", "IsEncrypted", "CreatedAt", "UpdatedAt")
VALUES (gen_random_uuid(), 'connectors.staleJobTimeoutMinutes', '60',
        'Age in minutes after which a Running connector job is considered orphaned and auto-closed',
        false, now(), now())
ON CONFLICT ("Key") DO NOTHING;

-- One-off: close jobs already stuck in Running (5-minute grace for genuinely active runs).
UPDATE connector_jobs
SET "Status"       = 'Failed',
    "CompletedAt"  = now(),
    "UpdatedAt"    = now(),
    "ErrorMessage" = 'Closed as stale during upgrade — the owning process restarted mid-run'
WHERE "Status" = 'Running'
  AND COALESCE("StartedAt", "CreatedAt") < now() - interval '5 minutes';
