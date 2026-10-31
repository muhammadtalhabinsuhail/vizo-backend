-- ============================================================================
--  47  When a rep was last reminded to hand in a collection
-- ============================================================================
--
--  The owner, 1 Nov: a salesperson records a payment collection
--  (SalesCollectionsController) and, until the accountant or the Super Admin
--  confirms it, keeps being reminded to hand the money in
--  (CollectionReminderService). "Collection"."RemindedAt" is when that last
--  happened -- a column rather than a timer so a restart neither forgets the
--  schedule nor sends a burst of duplicates.
--
--  NULL on every older collection: those still AWAITING are reminded on the
--  service's first sweep.
--
--  The API reads this column, so run it BEFORE (or with) the API deploy.
--  Additive and idempotent.
--
--  Rollback:
--    ALTER TABLE "Collection" DROP COLUMN IF EXISTS "RemindedAt";
-- ============================================================================

BEGIN;

ALTER TABLE "Collection" ADD COLUMN IF NOT EXISTS "RemindedAt" timestamp without time zone NULL;

COMMIT;
