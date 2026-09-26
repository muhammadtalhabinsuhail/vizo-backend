-- ============================================================================
--  26  Purchase pricing, stock batches, logistics accounts, no PO status
-- ============================================================================
--
--  Applied to the local test copy only -- NOT yet run on live.
--
--  What the owner asked for (26 Sep), in one line each:
--    * A product's price is five boxes -- Cost, Duty, Fi Sabilillah (FS),
--      Margin 1, Margin 2 -- and the Sale price is their sum. No Margin %.
--    * A purchase order carries the same five boxes per line, saved on the
--      line exactly as entered, plus a reason for each extra box.
--    * Duty is owed to a LOGISTICS COMPANY, which is an account of its own the
--      admin can add, rename and delete; the duty box picks one.
--    * A purchase order has NO status and NO expected date. Creating it puts
--      the stock on the shelf at once, raises the supplier's bill and writes
--      the journal vouchers. No approval, no GRN.
--    * Every unit on a shelf must be traceable to the purchase order it came
--      from, wherever it has moved since -- so the admin can see what is left
--      of each old purchase, at what price, and average the ones he chooses.
--
--  THIRD NORMAL FORM, as asked:
--    * The five price parts of a purchase are attributes of the PO LINE.
--    * A "StockBatch" is one lot of one product: a PO line, or the single
--      OPENING lot every product with stock today starts from. It carries the
--      lot's unit price parts because the OPENING lot has no PO line to read
--      them from; for a PO lot they are written once, in the same transaction
--      as the line, and neither is ever edited (a PO cannot be edited).
--    * "StockBatchBalance" is how many of a lot sit at each location.
--      SUM over lots = "StockBalance"."Quantity" for every product/location,
--      always -- checked at the end of this script.
--    * "StockBatchMovement" says which lots each "StockMovement" touched, so
--      a dispatched unit can be traced back to its purchase order.
--
--  TWO SECTIONS. Section 1 is additive and safe while the OLD API is still
--  running. Section 2 drops the PO status/expected/approved columns and must
--  wait until the NEW API is deployed (the old build selects them) -- the same
--  order as 19_product_pricing.sql.
-- ============================================================================

BEGIN;

-- ─────────────────────────── SECTION 1 ───────────────────────────

-- 1a. Product: the two new price parts. "MarginPrice" is Margin 1 from now on.
ALTER TABLE "Product" ADD COLUMN IF NOT EXISTS "FsPrice"      numeric(14,2) NOT NULL DEFAULT 0;
ALTER TABLE "Product" ADD COLUMN IF NOT EXISTS "Margin2Price" numeric(14,2) NOT NULL DEFAULT 0;
DO $$ BEGIN
  ALTER TABLE "Product" ADD CONSTRAINT "Product_FsPrice_check" CHECK ("FsPrice" >= 0);
EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN
  ALTER TABLE "Product" ADD CONSTRAINT "Product_Margin2Price_check" CHECK ("Margin2Price" >= 0);
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

-- 1b. Accounts. Identity sequences in this schema have been found parked
--     behind the data before (HANDOFF trap 10) -- catch it up first.
SELECT setval(pg_get_serial_sequence('"Account"', 'AccountId'),
              (SELECT MAX("AccountId") FROM "Account"));

--  2150 Logistics Companies -- a GROUP under Current Liabilities. Each
--  company the admin adds becomes a child account 2151, 2152 ... that duty is
--  credited to (we owe them the duty they paid for us).
INSERT INTO "Account" ("AccountCode","AccountName","ParentAccountId","AccountTypeId","IsGroup")
SELECT '2150', 'Logistics Companies',
       (SELECT "AccountId" FROM "Account" WHERE "AccountCode" = '2100'), 11, TRUE
WHERE NOT EXISTS (SELECT 1 FROM "Account" WHERE "AccountCode" = '2150');

--  The three loadings. A purchase carries the goods at their full selling
--  value; FS, Margin 1 and Margin 2 are the parts of that value that are not
--  cost, so each is credited to its own reserve (the retail method's
--  "mark-up reserve"). Fi Sabilillah is money set aside for the cause.
INSERT INTO "Account" ("AccountCode","AccountName","ParentAccountId","AccountTypeId","IsGroup")
SELECT v.code, v.name, (SELECT "AccountId" FROM "Account" WHERE "AccountCode" = '2100'), 11, FALSE
FROM (VALUES ('2160','Fi Sabilillah Reserve'),
             ('2161','Margin 1 Reserve'),
             ('2162','Margin 2 Reserve')) AS v(code, name)
WHERE NOT EXISTS (SELECT 1 FROM "Account" a WHERE a."AccountCode" = v.code);

-- 1c. Purchase order: status and expected date stop being required (dropped
--     in section 2). The supplier's own bill number is optional on the form.
ALTER TABLE "PurchaseOrder" ALTER COLUMN "StatusId" DROP NOT NULL;
ALTER TABLE "PurchaseOrder" ADD COLUMN IF NOT EXISTS "SupplierBillNo" varchar(50);

-- 1d. Purchase order line: the price parts, the logistics company the duty is
--     owed to, and why each extra box was filled in.
ALTER TABLE "PurchaseOrderItem" ADD COLUMN IF NOT EXISTS "DutyPrice"    numeric(14,2) NOT NULL DEFAULT 0;
ALTER TABLE "PurchaseOrderItem" ADD COLUMN IF NOT EXISTS "FsPrice"      numeric(14,2) NOT NULL DEFAULT 0;
ALTER TABLE "PurchaseOrderItem" ADD COLUMN IF NOT EXISTS "Margin1Price" numeric(14,2) NOT NULL DEFAULT 0;
ALTER TABLE "PurchaseOrderItem" ADD COLUMN IF NOT EXISTS "Margin2Price" numeric(14,2) NOT NULL DEFAULT 0;
ALTER TABLE "PurchaseOrderItem" ADD COLUMN IF NOT EXISTS "DutyAccountId" integer;
ALTER TABLE "PurchaseOrderItem" ADD COLUMN IF NOT EXISTS "DutyNote"    varchar(300);
ALTER TABLE "PurchaseOrderItem" ADD COLUMN IF NOT EXISTS "FsNote"      varchar(300);
ALTER TABLE "PurchaseOrderItem" ADD COLUMN IF NOT EXISTS "Margin1Note" varchar(300);
ALTER TABLE "PurchaseOrderItem" ADD COLUMN IF NOT EXISTS "Margin2Note" varchar(300);
DO $$ BEGIN
  ALTER TABLE "PurchaseOrderItem" ADD CONSTRAINT "PurchaseOrderItem_parts_check"
    CHECK ("DutyPrice" >= 0 AND "FsPrice" >= 0 AND "Margin1Price" >= 0 AND "Margin2Price" >= 0);
EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN
  -- RESTRICT, not this schema's usual CASCADE: deleting a logistics company
  -- must never silently delete the purchase lines that owe it duty.
  ALTER TABLE "PurchaseOrderItem" ADD CONSTRAINT "fk_poi_duty_account"
    FOREIGN KEY ("DutyAccountId") REFERENCES "Account"("AccountId") ON DELETE RESTRICT;
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

-- 1e. Stock lots.
CREATE TABLE IF NOT EXISTS "StockBatch" (
  "BatchId"      integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
  "ProductId"    integer NOT NULL REFERENCES "Product"("ProductId") ON DELETE CASCADE,
  "PoItemId"     integer UNIQUE REFERENCES "PurchaseOrderItem"("PoItemId") ON DELETE RESTRICT,
  "BatchNo"      varchar(30)   NOT NULL,
  "BatchDate"    date          NOT NULL,
  "QtyReceived"  integer       NOT NULL,
  "UnitCost"     numeric(14,2) NOT NULL DEFAULT 0,
  "UnitDuty"     numeric(14,2) NOT NULL DEFAULT 0,
  "UnitFs"       numeric(14,2) NOT NULL DEFAULT 0,
  "UnitMargin1"  numeric(14,2) NOT NULL DEFAULT 0,
  "UnitMargin2"  numeric(14,2) NOT NULL DEFAULT 0,
  "CreatedAt"    timestamp without time zone NOT NULL
);
CREATE INDEX IF NOT EXISTS "ix_stockbatch_product" ON "StockBatch" ("ProductId", "BatchDate", "BatchId");

CREATE TABLE IF NOT EXISTS "StockBatchBalance" (
  "BatchId"    integer NOT NULL REFERENCES "StockBatch"("BatchId") ON DELETE CASCADE,
  "LocationId" integer NOT NULL REFERENCES "Location"("LocationId") ON DELETE RESTRICT,
  "Quantity"   integer NOT NULL,
  PRIMARY KEY ("BatchId", "LocationId")
);
CREATE INDEX IF NOT EXISTS "ix_stockbatchbalance_location" ON "StockBatchBalance" ("LocationId");

CREATE TABLE IF NOT EXISTS "StockBatchMovement" (
  "MovementId" integer NOT NULL REFERENCES "StockMovement"("MovementId") ON DELETE CASCADE,
  "BatchId"    integer NOT NULL REFERENCES "StockBatch"("BatchId") ON DELETE CASCADE,
  "Quantity"   integer NOT NULL,          -- signed, same sign as the movement
  PRIMARY KEY ("MovementId", "BatchId")
);
CREATE INDEX IF NOT EXISTS "ix_stockbatchmovement_batch" ON "StockBatchMovement" ("BatchId");

-- 1f. The OPENING lot: everything on the shelves today, one lot per product,
--     at the product's price as it stands. Negative balances (two exist:
--     product 13 at -1, product 32 at -975 in Karachi Warehouse) are carried
--     into the lot as they are, so the SUM invariant holds from row one.
INSERT INTO "StockBatch" ("ProductId","PoItemId","BatchNo","BatchDate","QtyReceived",
                          "UnitCost","UnitDuty","UnitFs","UnitMargin1","UnitMargin2","CreatedAt")
SELECT p."ProductId", NULL, 'OPENING', CURRENT_DATE,
       GREATEST(SUM(s."Quantity"), 0),
       p."CostPrice", p."DutyPrice", p."FsPrice", p."MarginPrice", p."Margin2Price",
       (now() AT TIME ZONE 'Asia/Karachi')
FROM "Product" p
JOIN "StockBalance" s ON s."ProductId" = p."ProductId" AND s."Quantity" <> 0
WHERE NOT EXISTS (SELECT 1 FROM "StockBatch" b WHERE b."ProductId" = p."ProductId")
GROUP BY p."ProductId";

INSERT INTO "StockBatchBalance" ("BatchId","LocationId","Quantity")
SELECT b."BatchId", s."LocationId", s."Quantity"
FROM "StockBatch" b
JOIN "StockBalance" s ON s."ProductId" = b."ProductId" AND s."Quantity" <> 0
WHERE b."BatchNo" = 'OPENING'
  AND NOT EXISTS (SELECT 1 FROM "StockBatchBalance" x WHERE x."BatchId" = b."BatchId");

-- 1g. Which journal vouchers a purchase order wrote, and for which part.
CREATE TABLE IF NOT EXISTS "PurchaseOrderEntry" (
  "PoId"      integer NOT NULL REFERENCES "PurchaseOrder"("PoId") ON DELETE CASCADE,
  "EntryId"   integer NOT NULL REFERENCES "JournalEntry"("EntryId") ON DELETE CASCADE,
  "Component" varchar(10) NOT NULL CHECK ("Component" IN ('GOODS','DUTY','FS','MARGIN1','MARGIN2')),
  PRIMARY KEY ("PoId", "EntryId")
);

-- The invariant: lots add up to the shelf, everywhere. Aborts the whole
-- migration if not.
DO $$
DECLARE bad integer;
BEGIN
  SELECT COUNT(*) INTO bad FROM (
    SELECT s."ProductId", s."LocationId", s."Quantity",
           COALESCE((SELECT SUM(bb."Quantity") FROM "StockBatchBalance" bb
                     JOIN "StockBatch" b ON b."BatchId" = bb."BatchId"
                     WHERE b."ProductId" = s."ProductId" AND bb."LocationId" = s."LocationId"), 0) AS lots
    FROM "StockBalance" s) t
  WHERE t."Quantity" <> t.lots;
  IF bad > 0 THEN RAISE EXCEPTION 'StockBatch invariant broken on % rows', bad; END IF;
END $$;

COMMIT;

-- ─────────────────────────── SECTION 2 ───────────────────────────
--  RUN ONLY AFTER THE NEW API IS DEPLOYED. The old build selects these.
--  "PurchaseOrderStatus" (the lookup table) is left in place: nothing reads
--  it any more, and dropping a lookup here cascades (HANDOFF trap 24).
--
-- BEGIN;
-- ALTER TABLE "PurchaseOrder" DROP COLUMN IF EXISTS "ExpectedDate";
-- ALTER TABLE "PurchaseOrder" DROP COLUMN IF EXISTS "StatusId";
-- ALTER TABLE "PurchaseOrder" DROP COLUMN IF EXISTS "ApprovedByUserId";
-- COMMIT;
