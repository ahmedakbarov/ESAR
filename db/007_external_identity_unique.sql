-- ============================================================================
-- ESAR — schema increment 007
-- Prevents two ESAR accounts from silently claiming the same federated
-- identity (Entra ID objectId / AD objectGUID). Partial index — NULLs are
-- unlimited in Postgres, so this is a no-op for Local accounts and for
-- federated placeholder accounts an admin pre-created but that haven't
-- logged in (and linked ExternalObjectId) yet.
--
-- Safe to run repeatedly. Apply in the same deploy window as the code that
-- adds the Azure AD SSO / AD login endpoints.
--   psql -f db/007_external_identity_unique.sql
-- ============================================================================

CREATE UNIQUE INDEX IF NOT EXISTS ux_users_authprovider_externalobjectid
    ON users ("AuthProvider", "ExternalObjectId")
    WHERE "ExternalObjectId" IS NOT NULL;
