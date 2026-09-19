/* ═══════════════════════════════════════════════════════════════════════════
   20 — IN TRANSIT GOES, BILLING AND RETURNS MOVE TO THE BACK OFFICE,
        AND A RETURN NO LONGER NEEDS AN INVOICE
   ═══════════════════════════════════════════════════════════════════════════

   Four things the owner asked for on 21 September, in one file because they
   ship together with one API build.

   ──────────────────────────── 1. NO "IN TRANSIT" SHELF ──────────────────────

   "In Transit" (LOC-05) was a location of its own. It should never have been
   one: goods on a van belong to neither end of a transfer, which is exactly
   what the transfer's own IN_TRANSIT *status* says, and the code has always
   agreed — CreateTransfer takes the units off the source shelf and adds them to
   the destination only when somebody receives them. Nothing has written to that
   location since the seed.

   What was sitting on it, checked before this was written:

     · 240 units (120 × VIZO Linko VC101, 120 × VIZO Maxo VC202) left there by
       the seeded transfer TRF-26-0014. That transfer was RECEIVED into Shop 2
       on 25 August and those same 240 units were added there, so the copy on
       In Transit was counted twice in any figure that adds locations up.
     · Two matching TRANSFER_IN movements, from the same seed.
     · One order (ORD-26-0158) and its invoice (INV-26-8878), whose location was
       set to In Transit by whoever raised them.

   THE ORDER AND THE INVOICE ARE MOVED FIRST, ON PURPOSE. Every foreign key into
   "Location" is ON DELETE CASCADE, so deleting the row without moving them
   would have deleted a real order and a real invoice along with it. They go to
   Karachi Order Department (LOC-02), which is the default location and where
   that order would be served from — the owner's choice.

   The location KIND is dropped too, so nobody can create a second one from
   Administration → Locations.

   ──────────────────────── 2. SALES NO LONGER BILLS ──────────────────────────

   "invoiced/Edit ka status done krna ka right sales person se lelo ... wo
   accountant ya super admin krsakta hai"

   So `invoices.create` comes off the Sales role. They keep `invoices.view`:
   a rep must still be able to open and print the bill for their own order.

   The order desk keeps `invoices.create` — the counter raises its own invoice
   the moment a walk-in pays, and that is a different thing from billing an
   order that is going through the chain. The API refuses THAT to anybody but
   the accountant and the owner, by role, in OrderWorkflow.MayInvoice.

   ─────────────────── 3. SALES RETURNS ARE THE BACK OFFICE'S ─────────────────

   "sales person apna panel se koi bhi sales return nhi krsakta ... sales return
   only admin and accountant hi krsakta hn"

   `returns.sales` comes off Sales AND off the order desk, leaving it with the
   accountant and the Super Admin. The API also checks the role on every return
   endpoint, so re-ticking the box in Setup does not quietly re-open it.

   ─────────────────── 4. A RETURN WITHOUT A SINGLE INVOICE ───────────────────

   "sales return multiple orders se ho sakti hai"

   "SalesReturn"."InvoiceId" becomes nullable. A return raised from now on is
   against the CUSTOMER: what may come back is everything they have ever been
   billed for, less what has already come back, and the ceiling is recomputed
   in the API inside the writing transaction. The seven returns raised before
   this keep their invoice and go on reading as they always did.

   ─────────────────────────── DEPLOY ORDER ──────────────────────────────────

   RUN THIS BEFORE OR WITH THE NEW API BUILD — not long after.

     · Sections 1, 2 and 3 are safe with the old build running: the old build
       never reads the In Transit location by id, and a permission taken away
       simply refuses a screen.
     · Section 4 only RELAXES a constraint, so the old build cannot notice it.
     · Section 5 renames a status NAME. The old build shows the new wording
       immediately; nothing matches on it — every comparison in the code is on
       "StatusKey" ('INVOICED'), which does not change.

   Everybody signs out and back in afterwards, as with migration 18:
   permissions travel inside the JWT and a token lasts eight hours.

   Safe to run twice.
   ═══════════════════════════════════════════════════════════════════════════ */


/* ═══════════ 1.  THE "IN TRANSIT" LOCATION GOES ═══════════════════════════ */

DO $$
DECLARE
    transit_id  INT;
    order_dept  INT;
    moved_docs  INT := 0;
    dropped_qty INT := 0;
    leftovers   INT;
BEGIN
    SELECT "LocationId" INTO transit_id
      FROM "Location"
     WHERE "LocationCode" = 'LOC-05' OR "LocationName" = 'In Transit'
     LIMIT 1;

    IF transit_id IS NULL THEN
        RAISE NOTICE 'In Transit is already gone — nothing to do.';
    ELSE
        /* Where the order and the invoice go instead. LOC-02 by code, falling
           back to whichever location is flagged default, so this still works on
           a database whose codes were renamed. */
        SELECT "LocationId" INTO order_dept
          FROM "Location"
         WHERE "LocationCode" = 'LOC-02' AND "LocationId" <> transit_id
         LIMIT 1;

        IF order_dept IS NULL THEN
            SELECT "LocationId" INTO order_dept
              FROM "Location"
             WHERE "IsDefault" AND "LocationId" <> transit_id
             LIMIT 1;
        END IF;

        IF order_dept IS NULL THEN
            RAISE EXCEPTION 'No location to move In Transit''s documents to. Nothing changed.';
        END IF;

        UPDATE "SalesOrder"   SET "LocationId" = order_dept WHERE "LocationId" = transit_id;
        GET DIAGNOSTICS moved_docs = ROW_COUNT;
        UPDATE "SalesInvoice" SET "LocationId" = order_dept WHERE "LocationId" = transit_id;

        /* Anything else that could be pointing at it. All of these were zero on
           the live database when this was written; they are here so the file is
           safe on a copy where they are not. */
        UPDATE "ActivityLog"     SET "LocationId"        = NULL       WHERE "LocationId"        = transit_id;
        UPDATE "Party"           SET "DefaultLocationId" = NULL       WHERE "DefaultLocationId" = transit_id;
        UPDATE "User"            SET "PrimaryLocationId" = NULL       WHERE "PrimaryLocationId" = transit_id;
        UPDATE "SalesReturnItem" SET "RestockLocationId" = NULL       WHERE "RestockLocationId" = transit_id;
        UPDATE "GoodsReceipt"    SET "LocationId" = order_dept WHERE "LocationId" = transit_id;
        UPDATE "PurchaseOrder"   SET "LocationId" = order_dept WHERE "LocationId" = transit_id;
        UPDATE "PurchaseReturn"  SET "LocationId" = order_dept WHERE "LocationId" = transit_id;
        UPDATE "SalesReturn"     SET "LocationId" = order_dept WHERE "LocationId" = transit_id;
        UPDATE "StockAdjustment" SET "LocationId" = order_dept WHERE "LocationId" = transit_id;
        UPDATE "Expense"         SET "LocationId" = order_dept WHERE "LocationId" = transit_id;
        UPDATE "JournalEntry"    SET "LocationId" = order_dept WHERE "LocationId" = transit_id;
        UPDATE "Voucher"         SET "LocationId" = order_dept WHERE "LocationId" = transit_id;
        DELETE FROM "UserLocation" WHERE "LocationId" = transit_id;

        /* A transfer that still has In Transit at either end would be a real
           transfer whose paperwork points at a shelf that is about to vanish,
           and there is no honest automatic answer to that — so stop rather than
           guess. There are none on the live database. */
        SELECT count(*) INTO leftovers
          FROM "StockTransfer"
         WHERE "FromLocationId" = transit_id OR "ToLocationId" = transit_id;
        IF leftovers > 0 THEN
            RAISE EXCEPTION
                '% stock transfer(s) still point at In Transit. Re-point them by hand first; nothing changed.',
                leftovers;
        END IF;

        /* The duplicate stock, and the movements that put it there. */
        SELECT COALESCE(sum("Quantity"), 0) INTO dropped_qty
          FROM "StockBalance" WHERE "LocationId" = transit_id;

        DELETE FROM "StockMovement" WHERE "LocationId" = transit_id;
        DELETE FROM "StockBalance"  WHERE "LocationId" = transit_id;

        DELETE FROM "Location" WHERE "LocationId" = transit_id;

        RAISE NOTICE 'In Transit removed. % document(s) moved to location %, % duplicate unit(s) dropped.',
            moved_docs, order_dept, dropped_qty;
    END IF;

    /* The kind itself, so the New Location form cannot offer it again. Only
       when nothing is using it — on another database somebody may have made a
       second transit location, and dropping the kind would take it with them
       (the FK is ON DELETE CASCADE). */
    IF EXISTS (SELECT 1 FROM "LocationKind" WHERE "KindKey" = 'transit') THEN
        SELECT count(*) INTO leftovers
          FROM "Location" l JOIN "LocationKind" k ON k."KindId" = l."KindId"
         WHERE k."KindKey" = 'transit';

        IF leftovers = 0 THEN
            DELETE FROM "LocationKind" WHERE "KindKey" = 'transit';
            RAISE NOTICE 'The "In Transit" location type is gone.';
        ELSE
            RAISE NOTICE '% location(s) are still of type In Transit — the type is kept.', leftovers;
        END IF;
    END IF;
END $$;


/* ═══════════ 2.  SALES NO LONGER CUTS INVOICES ═══════════════════════════ */

DELETE FROM "RolePermission" rp
 USING "Role" r, "Permission" p
 WHERE rp."RoleId"       = r."RoleId"
   AND rp."PermissionId" = p."PermissionId"
   AND r."RoleKey"       = 'sales'
   AND p."PermissionKey" = 'invoices.create';


/* ═══════════ 3.  SALES RETURNS ARE ADMIN AND ACCOUNTS ONLY ═══════════════ */

DELETE FROM "RolePermission" rp
 USING "Role" r, "Permission" p
 WHERE rp."RoleId"       = r."RoleId"
   AND rp."PermissionId" = p."PermissionId"
   AND r."RoleKey"       IN ('sales', 'order-dept')
   AND p."PermissionKey" = 'returns.sales';

/* And make sure the two who DO hold it really do. Both already did when this
   was written; this is here so a database that lost one is repaired rather than
   left with a screen nobody can open. */
INSERT INTO "RolePermission" ("RoleId", "PermissionId")
SELECT r."RoleId", p."PermissionId"
  FROM "Role" r, "Permission" p
 WHERE r."RoleKey" IN ('super-admin', 'accountant')
   AND p."PermissionKey" = 'returns.sales'
   AND NOT EXISTS (SELECT 1 FROM "RolePermission" x
                    WHERE x."RoleId" = r."RoleId" AND x."PermissionId" = p."PermissionId");


/* ═══════════ 4.  A RETURN NEED NOT BE AGAINST ONE INVOICE ════════════════ */

ALTER TABLE "SalesReturn" ALTER COLUMN "InvoiceId" DROP NOT NULL;


/* ═══════════ 5.  THE STEP IS CALLED "INVOICED/EDIT" ══════════════════════ */

/* The KEY stays 'INVOICED' — every comparison in the API and the browser is on
   the key, and renaming it would be a rename of the workflow itself. Only the
   words people read change, and they change in one place because the chain
   strip on the order screen reads its labels from this table. */
UPDATE "OrderStatus"
   SET "StatusName" = 'Invoiced/Edit'
 WHERE "StatusKey" = 'INVOICED' AND "StatusName" <> 'Invoiced/Edit';


/* ═══════════ 6.  WHAT IT SHOULD LOOK LIKE AFTERWARDS ═════════════════════ */

/*  Locations — five, and no In Transit:

      SELECT l."LocationId", l."LocationCode", l."LocationName", k."KindKey", l."IsActive"
        FROM "Location" l JOIN "LocationKind" k ON k."KindId" = l."KindId"
       ORDER BY 1;

    Nothing left on the transit shelf (0 rows, and no such location):

      SELECT * FROM "StockBalance" WHERE "LocationId" NOT IN (SELECT "LocationId" FROM "Location");

    Who may raise a return, and who may bill an order (super-admin and
    accountant for the first; sales must not appear in either):

      SELECT r."RoleKey", p."PermissionKey"
        FROM "RolePermission" rp
        JOIN "Role" r       ON r."RoleId"       = rp."RoleId"
        JOIN "Permission" p ON p."PermissionId" = rp."PermissionId"
       WHERE p."PermissionKey" IN ('returns.sales', 'invoices.create')
       ORDER BY 2, 1;

    The column is nullable now:

      SELECT is_nullable FROM information_schema.columns
       WHERE table_name = 'SalesReturn' AND column_name = 'InvoiceId';      -- YES

    And the step reads as it should:

      SELECT "StatusKey", "StatusName" FROM "OrderStatus" WHERE "StatusKey" = 'INVOICED';
*/
