-- ============================================================================
-- ESAR — schema increment 008
-- Adds users."IsProtected": marks the seeded break-glass "admin" account so it
-- can't be deactivated, deleted, role-changed or password-reset by other
-- admins via the management API (the owner can still rotate its own password
-- through self-service change-password). Backfills the existing seeded admin.
--
-- Safe to run repeatedly. Apply in the same deploy window as the code that
-- reads this column.
--   psql -f db/008_user_protected.sql
-- ============================================================================

ALTER TABLE users ADD COLUMN IF NOT EXISTS "IsProtected" boolean NOT NULL DEFAULT false;

-- Backfill: the bootstrap local admin created on first startup.
UPDATE users SET "IsProtected" = true
WHERE "Username" = 'admin' AND "AuthProvider" = 'Local';
