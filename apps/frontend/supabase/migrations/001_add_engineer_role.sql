-- Phase 1: add ENGINEER role for internal NetWatch deployment
-- Run in Supabase SQL Editor if schema was created before this migration.

ALTER TABLE users DROP CONSTRAINT IF EXISTS users_role_check;
ALTER TABLE users ADD CONSTRAINT users_role_check
  CHECK (role IN ('OWNER', 'ADMIN', 'ENGINEER', 'VIEWER'));

ALTER TABLE team_invitations DROP CONSTRAINT IF EXISTS team_invitations_role_check;
ALTER TABLE team_invitations ADD CONSTRAINT team_invitations_role_check
  CHECK (role IN ('OWNER', 'ADMIN', 'ENGINEER', 'VIEWER'));
