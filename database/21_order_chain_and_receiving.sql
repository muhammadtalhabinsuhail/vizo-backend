/* ═══════════════════════════════════════════════════════════════════════════
   21 — A SHORTER ORDER CHAIN, FOUR WAYS TO TAKE MONEY, AND ONE ORDER DELETED
   ═══════════════════════════════════════════════════════════════════════════

   Three unrelated things the owner asked for on 22 September, in one file
   because they ship with one API build.

   ─────────────────────── 1. THE CHAIN LOSES THREE STEPS ─────────────────────

   "remove statuses from entire project of order that are 'seen by warehouse'
    and 'on the way to order department' and 'packaging' and change status name
    from 'Received at Order Dept' to 'Processing in order dept'"

   Ten steps become seven:

       Draft · Submitted · Confirmed · Invoiced/Edit ·
       Processing in Order Dept · Dispatched · Delivered

   SEVEN LIVE ORDERS WERE SITTING IN THE STEPS BEING REMOVED — six at "On way
   to Order Dept" and one at "Packaging". They are moved to "Processing in
   Order Dept", which is where all three of those steps were heading anyway,
   and each one gets an activity-log row saying so, because an order that
   changes state on its own overnight is exactly the kind of thing somebody
   notices a week later and cannot explain.

   The two 2026-era leftovers PROCESSING and PACKED go too. Both were already
   dead (no orders, hidden from every dropdown) and both were the old
   pre-chain flow that the /packing screen drove. That screen is retired in
   this release: stock now leaves at DISPATCHED, and two screens taking the
   same stock off the same shelf is how a count goes wrong.

   The KEYS of the surviving statuses are untouched. Only names and sort order
   change, so nothing in the code has to be told.

   ────────────────────── 2. FOUR WAYS TO TAKE MONEY IN ───────────────────────

   "through the projects make receiving payments options that are only:
    Cash, Credit, Meezan, Faisal for receiving payment only options."

   "PaymentMethod" is one list for money in AND money out, so the screen that
   records a customer's payment was offering Petty Cash, Credit Note, JazzCash
   and Easypaisa beside Cash -- eight ways to file a receipt when the business
   has four.

   A flag, not a deletion: nothing is removed, because eight years of history
   points at those rows and an expense still gets paid out of Petty Cash. Money
   coming IN is limited to the four flagged, and the owner can flag another
   bank from the database without a deploy.

   Meezan and Faysal are added as bank methods. (Faysal Bank is spelt with a
   "y"; the owner wrote "Faisal", and the screen shows whatever this row says --
   change it here if you want the other spelling.)

   ────────────────────── 3. ONE ORDER AND ITS INVOICE GO ─────────────────────

   "from database delete order and invoice 'ORD-26-0171' and 'INV-26-8893'"

   ORD-26-0171 (delivered, PKR 3,299,890, Arsalan Siddiqui, one line of 100
   Redmi 14C) and the invoice raised against it on 17 September. Checked before
   writing this: no sales return, no stock movement, no delivery, no payment
   allocation and no stored PDF row hang off either of them, so nothing else
   changes shape when they go. The twelve activity-log rows that mention them
   go too -- a history of a document that no longer exists is a puzzle, not a
   record.

   THE INVOICE NUMBER IS NOT REISSUED. INV-26-8893 simply stops existing and
   the series stays where it is; re-using a bill number is worse than a gap.

   ─────────────────────────── DEPLOY ORDER ──────────────────────────────────

   Run this BEFORE or WITH the new API build.

     · Sections 1 and 3 are safe with the old build running.
     · Section 2 only ADDS a column and two rows.
     · The old build would still offer the three removed steps in its dropdown,
       and the API would refuse them, so do not leave a long gap.

   Safe to run twice.
   ═══════════════════════════════════════════════════════════════════════════ */


/* ═══════════ 1.  THE ORDER CHAIN ════════════════════════════════════════ */

DO $$
DECLARE
    processing_id INT;
    moved         INT := 0;
BEGIN
    SELECT "StatusId" INTO processing_id FROM "OrderStatus" WHERE "StatusKey" = 'AT_ORDER_DEPT';
    IF processing_id IS NULL THEN
        RAISE EXCEPTION 'AT_ORDER_DEPT is missing — the chain cannot be shortened. Nothing changed.';
    END IF;

    /* The orders standing in the steps that are going. */
    WITH doomed AS (
        SELECT "StatusId" FROM "OrderStatus"
         WHERE "StatusKey" IN ('SEEN_BY_WAREHOUSE', 'TO_ORDER_DEPT', 'PACKAGING', 'PROCESSING', 'PACKED')
    ), moved_rows AS (
        UPDATE "SalesOrder"
           SET "StatusId" = processing_id
         WHERE "StatusId" IN (SELECT "StatusId" FROM doomed)
        RETURNING "OrderId", "OrderNo"
    )
    INSERT INTO "ActivityLog" ("UserId", "ActionName", "EntityType", "EntityReference", "Detail", "SeverityId", "LoggedAt")
    SELECT NULL, 'ORDER_STATUS_MIGRATED', 'SalesOrder', m."OrderNo",
           'Moved to "Processing in Order Dept" by migration 21: the step it was in no longer exists.',
           2, now()::timestamp
      FROM moved_rows m;

    GET DIAGNOSTICS moved = ROW_COUNT;

    DELETE FROM "OrderStatus"
     WHERE "StatusKey" IN ('SEEN_BY_WAREHOUSE', 'TO_ORDER_DEPT', 'PACKAGING', 'PROCESSING', 'PACKED');

    /* The words on the step, and the order the seven are drawn in. */
    UPDATE "OrderStatus" SET "StatusName" = 'Processing in Order Dept' WHERE "StatusKey" = 'AT_ORDER_DEPT';

    UPDATE "OrderStatus" SET "SortOrder" = v."SortOrder"
      FROM (VALUES
            ('DRAFT', 1), ('SUBMITTED', 2), ('CONFIRMED', 3), ('INVOICED', 4),
            ('AT_ORDER_DEPT', 5), ('DISPATCHED', 6), ('DELIVERED', 7),
            ('DECLINED', 20), ('CREDIT_HOLD', 21), ('CANCELLED', 22), ('RETURNED', 23)
           ) AS v("StatusKey", "SortOrder")
     WHERE "OrderStatus"."StatusKey" = v."StatusKey";

    RAISE NOTICE 'Chain shortened to seven steps. % order(s) moved to Processing in Order Dept.', moved;
END $$;


/* ═══════════ 2.  MONEY COMING IN: CASH, CREDIT, MEEZAN, FAYSAL ══════════ */

ALTER TABLE "PaymentMethod"
    ADD COLUMN IF NOT EXISTS "IsForReceiving" BOOLEAN NOT NULL DEFAULT FALSE;

INSERT INTO "PaymentMethod" ("MethodKey", "MethodName", "MethodKind", "IsActive")
SELECT 'MEEZAN', 'Meezan', 'bank', TRUE
 WHERE NOT EXISTS (SELECT 1 FROM "PaymentMethod" WHERE "MethodKey" = 'MEEZAN');

INSERT INTO "PaymentMethod" ("MethodKey", "MethodName", "MethodKind", "IsActive")
SELECT 'FAISAL', 'Faisal', 'bank', TRUE
 WHERE NOT EXISTS (SELECT 1 FROM "PaymentMethod" WHERE "MethodKey" = 'FAISAL');

UPDATE "PaymentMethod"
   SET "IsForReceiving" = ("MethodKey" IN ('CASH', 'CREDIT', 'MEEZAN', 'FAISAL'));


/* ═══════════ 3.  ORD-26-0171 AND INV-26-8893 ════════════════════════════ */

DO $$
DECLARE
    order_id   INT;
    invoice_id INT;
BEGIN
    SELECT "OrderId"   INTO order_id   FROM "SalesOrder"   WHERE "OrderNo"   = 'ORD-26-0171';
    SELECT "InvoiceId" INTO invoice_id FROM "SalesInvoice"  WHERE "InvoiceNo" = 'INV-26-8893';

    IF order_id IS NULL AND invoice_id IS NULL THEN
        RAISE NOTICE 'ORD-26-0171 and INV-26-8893 are already gone.';
        RETURN;
    END IF;

    /* Refuse rather than cascade if anything real is hanging off them. All of
       these were zero when this was written; they are checked because a
       delete that quietly takes a credit note or a payment with it is the
       worst kind. */
    IF invoice_id IS NOT NULL THEN
        IF EXISTS (SELECT 1 FROM "SalesReturn" WHERE "InvoiceId" = invoice_id) THEN
            RAISE EXCEPTION 'INV-26-8893 has a sales return against it. Nothing changed.';
        END IF;
        IF EXISTS (SELECT 1 FROM "VoucherAllocation" WHERE "SalesInvoiceId" = invoice_id) THEN
            RAISE EXCEPTION 'INV-26-8893 has money allocated against it. Nothing changed.';
        END IF;
        IF EXISTS (SELECT 1 FROM "Delivery" WHERE "InvoiceId" = invoice_id) THEN
            RAISE EXCEPTION 'INV-26-8893 has a delivery booked against it. Nothing changed.';
        END IF;
    END IF;

    IF order_id IS NOT NULL AND EXISTS (SELECT 1 FROM "StockMovement" WHERE "ReferenceNo" = 'ORD-26-0171') THEN
        RAISE EXCEPTION 'ORD-26-0171 has stock movements against it. Put the stock back first. Nothing changed.';
    END IF;

    IF invoice_id IS NOT NULL THEN
        DELETE FROM "SalesInvoiceItem" WHERE "InvoiceId" = invoice_id;
        DELETE FROM "SalesInvoice"     WHERE "InvoiceId" = invoice_id;
        DELETE FROM "DocumentFile"     WHERE "DocKind" = 'sales-invoice' AND "DocNo" = 'INV-26-8893';
    END IF;

    IF order_id IS NOT NULL THEN
        DELETE FROM "Delivery"        WHERE "OrderId" = order_id;
        DELETE FROM "OrderChangeRequest" WHERE "OrderId" = order_id;
        DELETE FROM "SalesOrderItem"  WHERE "OrderId" = order_id;
        DELETE FROM "SalesOrder"      WHERE "OrderId" = order_id;
    END IF;

    DELETE FROM "ActivityLog"  WHERE "EntityReference" IN ('ORD-26-0171', 'INV-26-8893');
    DELETE FROM "Notification" WHERE "Url" LIKE '%/sales/orders/' || COALESCE(order_id, -1)
                                  OR "Url" LIKE '%/sales/invoices/' || COALESCE(invoice_id, -1);

    RAISE NOTICE 'ORD-26-0171 and INV-26-8893 deleted.';
END $$;


/* ═══════════ 4.  WHAT IT SHOULD LOOK LIKE AFTERWARDS ════════════════════ */

/*  Seven steps, in order, and nothing parked in a step that is gone:

      SELECT "StatusId", "StatusKey", "StatusName", "SortOrder",
             (SELECT count(*) FROM "SalesOrder" o WHERE o."StatusId" = s."StatusId") AS orders
        FROM "OrderStatus" s ORDER BY "SortOrder";

    Money in, and money out:

      SELECT "MethodKey", "MethodName", "IsForReceiving" FROM "PaymentMethod" ORDER BY "MethodId";

    And the order is gone:

      SELECT count(*) FROM "SalesOrder"   WHERE "OrderNo"   = 'ORD-26-0171';   -- 0
      SELECT count(*) FROM "SalesInvoice" WHERE "InvoiceNo" = 'INV-26-8893';   -- 0
*/
