/* ═══════════════════════════════════════════════════════════════════════════
   19 — PRODUCT PRICING: DUTY AND MARGIN IN, OPENING COST OUT
   ═══════════════════════════════════════════════════════════════════════════

   A product's price used to be three numbers: an "opening cost" nobody could
   explain, a cost price, and a sale price. The business thinks in a different
   chain:

       cost price  +  duty  =  landed cost
       landed cost +  margin  =  sale price

   so the columns now say that.

   ─────────────────────────── WHAT IS STORED ────────────────────────────────

     "DutyPrice"    per unit, PKR. Customs duty, clearing, whatever is paid on
                    top of the supplier's price to get the goods through the
                    door.
     "MarginPrice"  per unit, PKR. What is added to the landed cost to arrive
                    at the sale price.

   The margin PERCENTAGE is not stored. It is margin / (cost + duty), which is
   always derivable from the three prices already on the row, and a percentage
   kept beside them is a second answer to a question the row already answers —
   the day the two disagree nobody can say which is right. The screen computes
   it; the user may TYPE either the amount or the percentage, and the other
   follows.

   "SalePrice" stays the authority. Every invoice, order and report reads it,
   and none of them should have to know how it was arrived at.

   ─────────────────────────── WHAT IS DROPPED ───────────────────────────────

   "OpeningCost". Removed on the owner's instruction — it duplicated "CostPrice"
   for every purpose anybody used it for, and it appeared on three screens and
   nowhere in a report. Nothing in the ledger, the invoices, the purchase side
   or any view reads it: checked against the live database before this was
   written (no view depends on "Product"; the only reference was the column's
   own CHECK constraint, which goes with it).

   ─────────────────────────── DEPLOY ORDER ──────────────────────────────────

   Section 1 is ADDITIVE and safe to run at any time, before or after the new
   API is deployed.

   Section 2 DROPS A COLUMN. Run it AFTER the new API is live. The previous
   build selects "OpeningCost" on every product query, and the moment the
   column disappears an API instance still running that build answers 500 on
   the product screens until it is replaced. The new build never touches it, so
   the column can sit there harmlessly (it is NOT NULL DEFAULT 0, so even an
   insert that leaves it out succeeds) for as long as it takes to deploy.

   Safe to run twice.
   ═══════════════════════════════════════════════════════════════════════════ */


/* ═══════════════ 1.  DUTY AND MARGIN  (additive, run any time) ═══════════════ */

ALTER TABLE "Product"
    ADD COLUMN IF NOT EXISTS "DutyPrice"   NUMERIC(14,2) NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS "MarginPrice" NUMERIC(14,2) NOT NULL DEFAULT 0;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'Product_DutyPrice_check') THEN
        ALTER TABLE "Product"
            ADD CONSTRAINT "Product_DutyPrice_check" CHECK ("DutyPrice" >= 0);
    END IF;
END $$;

/* NO CHECK ON "MarginPrice". A product sold at a loss is a real thing a shop
   does — clearance, a loss-leader, stock that has to move — and a constraint
   here would make the database refuse a price the owner chose on purpose. The
   form warns; it does not forbid. */

/* Every existing product is given the margin its prices already imply, so the
   three numbers on a row agree from the moment this runs:

       margin = sale - (cost + duty)        and duty starts at zero.

   Only rows where nothing has been recorded yet, so running this twice cannot
   overwrite a margin somebody has since chosen. Verified before writing: no
   product in the live catalogue is priced below its cost, so none of these
   comes out negative. */
UPDATE "Product"
   SET "MarginPrice" = "SalePrice" - ("CostPrice" + "DutyPrice")
 WHERE "MarginPrice" = 0
   AND "SalePrice" <> "CostPrice" + "DutyPrice";


/* ═══════════════ 2.  DROP OPENING COST  (run AFTER the new API is live) ═══════ */

ALTER TABLE "Product" DROP COLUMN IF EXISTS "OpeningCost";


/* ─────────────────────────── what you should see ─────────────────────────── */
--  SELECT "Sku", "CostPrice", "DutyPrice", "MarginPrice", "SalePrice"
--    FROM "Product" ORDER BY "ProductId" LIMIT 10;
--  -- MarginPrice = SalePrice - CostPrice - DutyPrice on every row.
--
--  SELECT column_name FROM information_schema.columns
--   WHERE table_name = 'Product' AND column_name = 'OpeningCost';
--  -- no rows once section 2 has run.
