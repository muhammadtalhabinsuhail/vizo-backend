/* ═══════════════════════════════════════════════════════════════════════════
   39 — CUSTOMER VISITS THAT CAN BE LOGGED, AND DELIVERIES THAT ARE CONFIRMED
        AND SETTLED FOR REAL
   ═══════════════════════════════════════════════════════════════════════════

   Applied to the local test copy only — NOT yet run on live.
   Idempotent: every statement is guarded (IF NOT EXISTS / NOT EXISTS), so it
   is safe to run twice. Run it BEFORE the new API goes live: the API reads
   every column added here, and a missing column answers 500 on the Visits and
   Delivery screens.

   ─────────────────────────── 1. CUSTOMER VISITS ─────────────────────────────

   "CustomerVisit" had who, whom, when, the outcome, a note and GPS -- but
   nothing in the API ever wrote a row, so the seven on live are seed data and
   the Visits screen could only ever show those seven. VisitsController now
   lets a rep log a visit from a phone. Two facts the form asks for had no
   column:

     NextFollowUpDate  when the rep means to go back. The whole point of a
                       "Followup" outcome is a date; without one it is a note
                       nobody is reminded of.
     LoggedAt          when the row was TYPED, as opposed to VisitedAt (when
                       the visit happened). A rep who logs yesterday's visits
                       this morning is normal; one who back-dates a month of
                       visits the night before the review is worth seeing.
     GpsAccuracyM      the phone's own accuracy radius for Latitude/Longitude.
                       A fix good to 2 km proves nothing about which shop the
                       rep was standing in, and the screen says so.

   ─────────────────────────── 2. THE SALES ROLE GETS visits.view ───────────

   The menu item "Customer Visits" hangs off visits.view, which Sales never
   held -- so the very people who make visits could not open the screen. The
   API scopes a rep to his own customers' visits (VisitsController); this only
   puts the item in his menu.

   ─────────────────────────── 3. DELIVERY CONFIRMATION ──────────────────────

     ReceivedBy        who signed for it at the shop -- the first question
                       anybody asks when a customer says "it never came".
     ConfirmedAt       when the confirmation was pressed (DeliveredDate is the
                       day it arrived, which may be earlier).

   ─────────────────────────── 4. COD SETTLEMENT ─────────────────────────────

   "Settle COD" used to flip IsCodSettled and nothing else: the money the
   courier handed over never reached the books, so the customer still owed it
   in his ledger and the bank never showed it. It now raises a CONFIRMED
   collection for the customer, posted as a receipt voucher (Dr the bank,
   Cr Accounts Receivable), and posts the courier's fee (Dr 5114 Delivery &
   Courier, Cr the same bank). The delivery remembers what it settled with:

     CodCollectionId   the collection (and through it the voucher) that
                       settled this COD. Null on the one seeded row that was
                       marked settled before any of this existed.
     CodSettledOn      the date the courier's money landed.
     CodFeeAmount      what the courier kept.
     CodFeeEntryId     the journal entry that posted that fee.
   ═══════════════════════════════════════════════════════════════════════════ */

BEGIN;

-- 1. Customer visits ---------------------------------------------------------
ALTER TABLE "CustomerVisit" ADD COLUMN IF NOT EXISTS "NextFollowUpDate" date NULL;
ALTER TABLE "CustomerVisit" ADD COLUMN IF NOT EXISTS "LoggedAt" timestamp without time zone NULL;
ALTER TABLE "CustomerVisit" ADD COLUMN IF NOT EXISTS "GpsAccuracyM" integer NULL;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_visit_gps_accuracy') THEN
        ALTER TABLE "CustomerVisit"
            ADD CONSTRAINT ck_visit_gps_accuracy CHECK ("GpsAccuracyM" IS NULL OR "GpsAccuracyM" >= 0);
    END IF;
END $$;

/* The screens read "this customer's visits, newest first" and "this rep's
   visits, newest first". */
CREATE INDEX IF NOT EXISTS ix_visit_customer_time ON "CustomerVisit" ("CustomerUserId", "VisitedAt" DESC);
CREATE INDEX IF NOT EXISTS ix_visit_rep_time      ON "CustomerVisit" ("SalesPersonUserId", "VisitedAt" DESC);

-- 2. Sales may open the Visits screen ----------------------------------------
INSERT INTO "RolePermission" ("RoleId", "PermissionId")
SELECT r."RoleId", p."PermissionId"
FROM "Role" r CROSS JOIN "Permission" p
WHERE r."RoleKey" = 'sales' AND p."PermissionKey" = 'visits.view'
  AND NOT EXISTS (SELECT 1 FROM "RolePermission" x
                  WHERE x."RoleId" = r."RoleId" AND x."PermissionId" = p."PermissionId");

-- 3. Delivery confirmation ----------------------------------------------------
ALTER TABLE "Delivery" ADD COLUMN IF NOT EXISTS "ReceivedBy" varchar(100) NULL;
ALTER TABLE "Delivery" ADD COLUMN IF NOT EXISTS "ConfirmedAt" timestamp without time zone NULL;

-- 4. COD settlement -----------------------------------------------------------
ALTER TABLE "Delivery" ADD COLUMN IF NOT EXISTS "CodCollectionId" integer NULL;
ALTER TABLE "Delivery" ADD COLUMN IF NOT EXISTS "CodSettledOn" date NULL;
ALTER TABLE "Delivery" ADD COLUMN IF NOT EXISTS "CodFeeAmount" numeric(14,2) NOT NULL DEFAULT 0;
ALTER TABLE "Delivery" ADD COLUMN IF NOT EXISTS "CodFeeEntryId" integer NULL;

DO $$
BEGIN
    /* SET NULL, not CASCADE: HANDOFF trap 24 -- a cascading FK would delete
       the delivery if somebody ever removed the collection. */
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_delivery_cod_collection') THEN
        ALTER TABLE "Delivery" ADD CONSTRAINT fk_delivery_cod_collection
            FOREIGN KEY ("CodCollectionId") REFERENCES "Collection"("CollectionId") ON DELETE SET NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_delivery_cod_fee_entry') THEN
        ALTER TABLE "Delivery" ADD CONSTRAINT fk_delivery_cod_fee_entry
            FOREIGN KEY ("CodFeeEntryId") REFERENCES "JournalEntry"("EntryId") ON DELETE SET NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_delivery_cod_fee') THEN
        ALTER TABLE "Delivery" ADD CONSTRAINT ck_delivery_cod_fee CHECK ("CodFeeAmount" >= 0);
    END IF;
END $$;

COMMIT;
