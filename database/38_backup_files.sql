-- ============================================================================
--  38  Backups that are real: the file, what went into it, and why it failed
-- ============================================================================
--
--  Applied to the local test copy only -- NOT yet run on live.
--
--  Until 27 Sep "Run Backup Now" inserted a RUNNING row into "BackupHistory"
--  and nothing ever finished it, Download was permanently disabled, and the
--  five rows already in the table ("MinIO Primary", 1.2 GB, sha256:a8f9...)
--  were seed data describing backups that were never taken.
--
--  The API now takes a real logical backup: every table in the public schema
--  streamed with COPY ... TO STDOUT (FORMAT csv, HEADER) into one .zip, with a
--  manifest.json (row counts, the migration level detected) and a RESTORE.txt.
--  The zip is kept here, in its own table, and downloaded from Setup -> Backups.
--
--  WHY IN THE DATABASE AND NOT CLOUDINARY. The file holds everything --
--  customers, ledgers, password hashes -- so it must never sit at a public
--  URL, and Cloudinary also refuses to deliver .zip files by default on these
--  accounts (the same restriction as PDFs, HANDOFF trap 11). The whole
--  database is a few MB, so a handful of zips here costs little. The API keeps
--  only the newest few files (Backup:KeepFiles, default 7) and drops the bytes
--  of older ones, keeping their history row. A copy inside the database it
--  came from does not survive losing that database -- the screen says so, and
--  the point of the Download button is to put the file somewhere else.
--
--  1. "BackupFile" -- one row per stored zip, 1:1 with "BackupHistory".
--     Separate from "BackupHistory" so listing the history never reads a
--     megabyte per row, and so the backup can leave this one table out of
--     itself (otherwise every backup would contain all the previous ones).
--  2. "BackupHistory" gains "TableCount", "RowTotal", "ErrorMessage".
--  3. The pre-existing rows are deleted: every one of them predates this
--     migration, has no file, and describes a backup that never happened.
--     Identified as "TableCount IS NULL" -- a run of the new code always
--     writes it, even when it fails -- so running this twice deletes nothing
--     real.
--
--  Safe to run twice.
-- ============================================================================

CREATE TABLE IF NOT EXISTS "BackupFile" (
    "BackupId"    integer      NOT NULL,
    "FileName"    varchar(120) NOT NULL,
    "Content"     bytea        NOT NULL,
    "SizeBytes"   bigint       NOT NULL,
    "CreatedAt"   timestamp without time zone NOT NULL,
    CONSTRAINT "BackupFile_pkey" PRIMARY KEY ("BackupId"),
    CONSTRAINT "fk_backupfile_history" FOREIGN KEY ("BackupId")
        REFERENCES "BackupHistory" ("BackupId") ON DELETE CASCADE
);

ALTER TABLE "BackupHistory" ADD COLUMN IF NOT EXISTS "TableCount"   integer      NULL;
ALTER TABLE "BackupHistory" ADD COLUMN IF NOT EXISTS "RowTotal"     bigint       NULL;
ALTER TABLE "BackupHistory" ADD COLUMN IF NOT EXISTS "ErrorMessage" varchar(500) NULL;

DELETE FROM "BackupHistory" b
 WHERE b."TableCount" IS NULL
   AND NOT EXISTS (SELECT 1 FROM "BackupFile" f WHERE f."BackupId" = b."BackupId");

-- Rollback:
--   DROP TABLE IF EXISTS "BackupFile";
--   ALTER TABLE "BackupHistory" DROP COLUMN IF EXISTS "ErrorMessage";
--   ALTER TABLE "BackupHistory" DROP COLUMN IF EXISTS "RowTotal";
--   ALTER TABLE "BackupHistory" DROP COLUMN IF EXISTS "TableCount";
--   (the five seed rows are not restored -- they described nothing real)
