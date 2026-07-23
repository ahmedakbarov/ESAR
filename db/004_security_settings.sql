-- ============================================================================
-- ESAR security settings seed.
-- Idempotent: safe to run on existing databases; current edited values are kept.
-- ============================================================================

INSERT INTO settings ("Id", "Key", "Value", "Description", "IsEncrypted", "CreatedAt", "UpdatedAt")
VALUES
  (gen_random_uuid(), 'security.password.minLength', '12',
   'Minimum password length for local accounts and password reset operations', false, now(), now()),
  (gen_random_uuid(), 'security.login.maxFailedAttempts', '5',
   'Maximum failed login attempts before a local account is locked', false, now(), now()),
  (gen_random_uuid(), 'security.login.lockoutMinutes', '15',
   'Minutes a local account remains locked after too many failed login attempts', false, now(), now()),
  (gen_random_uuid(), 'security.session.tokenLifetimeMinutes', '60',
   'JWT access-token lifetime in minutes for authenticated sessions', false, now(), now()),
  (gen_random_uuid(), 'security.session.idleTimeoutMinutes', '30',
   'Idle-session timeout in minutes used by the portal UX', false, now(), now()),
  (gen_random_uuid(), 'security.audit.retentionDays', '180',
   'Number of days to retain security and administration audit events', false, now(), now())
ON CONFLICT ("Key") DO NOTHING;
