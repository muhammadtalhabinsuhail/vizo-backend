-- ============================================================================
--  37  A temporary password must be changed at the first sign-in
-- ============================================================================
--
--  Applied to the local test copy only -- NOT yet run on live.
--
--  Until 27 Sep every account created at Setup -> Users got the same password,
--  "Vizo@1234", typed into the web form, and nothing ever asked the person to
--  change it. The server now generates a random temporary password for a new
--  account (and when the Super Admin sets one on an existing account), shows
--  it to the admin once, and flags the account so the web app sends the person
--  to /setup straight after they sign in.
--
--    "MustChangePassword"        the flag.
--    "TemporaryPasswordHash"     the hash that WAS the temporary password. The
--                                flag counts only while "PasswordHash" still
--                                equals it, so a change through ANY screen
--                                (/setup, Security, the emailed reset code)
--                                releases the person -- see User.Custom.cs.
--    "TemporaryPasswordIssuedAt" when it was issued (Pakistan time).
--
--  Existing accounts are NOT flagged: nobody on live is on a password the new
--  code generated, and flagging them would send every user to /setup at once.
--  (If the owner wants the old shared "Vizo@1234" accounts forced to change,
--  that is a separate decision -- see NOTES-e1.md, OWNER MUST SEE.)
--
--  Deploy order: this BEFORE the API that reads it (the new model maps these
--  columns, so the old database would fail every query on "User").
--  Safe to run twice.
-- ============================================================================

ALTER TABLE "User" ADD COLUMN IF NOT EXISTS "MustChangePassword"        boolean NOT NULL DEFAULT false;
ALTER TABLE "User" ADD COLUMN IF NOT EXISTS "TemporaryPasswordHash"     varchar(100) NULL;
ALTER TABLE "User" ADD COLUMN IF NOT EXISTS "TemporaryPasswordIssuedAt" timestamp without time zone NULL;

-- Rollback (only before the API that uses them is deployed):
--   ALTER TABLE "User" DROP COLUMN IF EXISTS "TemporaryPasswordIssuedAt";
--   ALTER TABLE "User" DROP COLUMN IF EXISTS "TemporaryPasswordHash";
--   ALTER TABLE "User" DROP COLUMN IF EXISTS "MustChangePassword";
