-- 12_journal_reversal_link.sql
--
-- WHY THIS COLUMN EXISTS
--
-- Reversing a posted entry used to mark the original REVERSED and post a
-- mirror entry alongside it. Every statement in the app filters on
--
--     WHERE Entry.Status.StatusKey = 'POSTED'
--
-- so the original vanished from the trial balance while the mirror stayed in
-- it. The pair did not cancel -- it left the ledger holding the NEGATIVE of the
-- original. A 5,200 expense reversed came out as -5,200 rather than nothing.
--
-- Marking both sides REVERSED would balance, but it rewrites history: an
-- expense posted in July and reversed in August would disappear from July's
-- profit and loss, and July was already reported.
--
-- So the original stays POSTED, and this column records what undid it. Both
-- entries count, they cancel where they should, each period keeps the figures
-- it actually had, and the screen can still say "reversed by JV-26-0181".
--
-- Safe to run twice.

ALTER TABLE "JournalEntry"
    ADD COLUMN IF NOT EXISTS "ReversedByEntryId" INT NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'JournalEntry_ReversedByEntryId_fkey'
    ) THEN
        ALTER TABLE "JournalEntry"
            ADD CONSTRAINT "JournalEntry_ReversedByEntryId_fkey"
            FOREIGN KEY ("ReversedByEntryId") REFERENCES "JournalEntry" ("EntryId");
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS "IX_JournalEntry_ReversedByEntryId"
    ON "JournalEntry" ("ReversedByEntryId");

-- Repair the pairs already written the old way. A mirror is recognisable: its
-- ReferenceNo is the original's EntryNo and its narration opens with
-- "Reversal of". Point the original at it and put the original back to POSTED.

UPDATE "JournalEntry" original
SET "ReversedByEntryId" = mirror."EntryId",
    "StatusId"          = (SELECT "StatusId" FROM "PostingStatus" WHERE "StatusKey" = 'POSTED')
FROM "JournalEntry" mirror
WHERE mirror."ReferenceNo" = original."EntryNo"
  AND mirror."Narration" LIKE 'Reversal of %'
  AND original."StatusId" = (SELECT "StatusId" FROM "PostingStatus" WHERE "StatusKey" = 'REVERSED')
  AND original."ReversedByEntryId" IS NULL;

-- Verify:
--   SELECT o."EntryNo" AS original, m."EntryNo" AS reversed_by, s."StatusKey"
--   FROM "JournalEntry" o
--   JOIN "JournalEntry" m ON m."EntryId" = o."ReversedByEntryId"
--   JOIN "PostingStatus" s ON s."StatusId" = o."StatusId";
