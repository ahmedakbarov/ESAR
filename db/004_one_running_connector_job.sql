-- Production upgrade: prevents concurrent ingestion of the same connector.
-- Run only after closing any stale Running job records (see deployment runbook).
CREATE UNIQUE INDEX IF NOT EXISTS ux_connector_jobs_one_running
    ON connector_jobs ("ConnectorId")
    WHERE "Status" = 'Running';
