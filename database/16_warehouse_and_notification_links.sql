/* ═══════════════════════════════════════════════════════════════════════════
   16 — THE WAREHOUSE'S OWN STEP, AND NOTIFICATIONS THAT GO SOMEWHERE
   ═══════════════════════════════════════════════════════════════════════════

   Three things, all small, all needed together.

   1. A NEW STEP: "Seen by Warehouse".

      The warehouse keeper's two actions were asked for by name: once an order
      is INVOICED they may mark it "Seen by Warehouse" and then "On way to
      Order Dept", and nothing else. The second half already existed; the
      first did not, so an order went from the office straight to "on its way"
      with no record that anybody had actually looked at it.

      The chain becomes ten steps:

        1 Draft            2 Submitted        3 Confirmed     4 Invoiced
        5 Seen by Warehouse                   6 On way to Order Dept
        7 Received at Order Dept              8 Packaging
        9 Dispatched      10 Delivered

   2. A URL ON EVERY NOTIFICATION.

      The bell has always stored a title and a body and nothing else. The link
      existed — every call site passes one — but it was only ever handed to
      Web Push, so a notification read in the bell was a dead end: it told you
      an order had been confirmed and left you to go and find it.

      "Notification"."Url" is where that link now lives, so clicking a row in
      the bell opens the thing it is about.

   3. The warehouse keeper can see invoices.

      They are told to check the invoice against what they are picking. They
      could not open one.

   Safe to run twice.
   ═══════════════════════════════════════════════════════════════════════════ */


/* ─────────────────── 1. the new status, and the renumbering ─────────────── */

INSERT INTO "OrderStatus" ("StatusKey", "StatusName", "SortOrder")
SELECT 'SEEN_BY_WAREHOUSE', 'Seen by Warehouse', 5
WHERE NOT EXISTS (
    SELECT 1 FROM "OrderStatus" WHERE "StatusKey" = 'SEEN_BY_WAREHOUSE'
);

/* Everything after it shifts down one. SortOrder only decides the order the
   status list is READ in — no foreign key points at it — so renumbering is
   safe, and leaving a gap would put the new step in the wrong place on every
   dropdown. */
UPDATE "OrderStatus" SET "SortOrder" = 6  WHERE "StatusKey" = 'TO_ORDER_DEPT';
UPDATE "OrderStatus" SET "SortOrder" = 7  WHERE "StatusKey" = 'AT_ORDER_DEPT';
UPDATE "OrderStatus" SET "SortOrder" = 8  WHERE "StatusKey" = 'PACKAGING';
UPDATE "OrderStatus" SET "SortOrder" = 9  WHERE "StatusKey" = 'DISPATCHED';
UPDATE "OrderStatus" SET "SortOrder" = 10 WHERE "StatusKey" = 'DELIVERED';


/* ─────────────────── 2. where a notification points ────────────────────── */

/* Nullable on purpose. Not everything worth telling somebody about has a page
   to open — a backup finishing, for instance — and a row with no link should
   simply not be clickable rather than lead somewhere useless.

   300 characters matches "Body". These are in-app paths like
   /sales/orders/42, never absolute URLs; keeping them relative means the same
   row works on localhost, on staging and in production. */
ALTER TABLE "Notification"
    ADD COLUMN IF NOT EXISTS "Url" VARCHAR(300) NULL;


/* ─────────────────── 3. the keeper can open an invoice ─────────────────── */

INSERT INTO "RolePermission" ("RoleId", "PermissionId")
SELECT r."RoleId", p."PermissionId"
FROM "Role" r
CROSS JOIN "Permission" p
WHERE r."RoleKey" = 'warehouse-keeper'
  AND p."PermissionKey" = 'invoices.view'
  AND NOT EXISTS (
      SELECT 1 FROM "RolePermission" rp
      WHERE rp."RoleId" = r."RoleId" AND rp."PermissionId" = p."PermissionId"
  );

/* Deliberately NOT granted: invoices.create, orders.create, orders.approve.
   The keeper reads an order and moves it two steps. They do not write one,
   bill one, or change one. */


/* ─────────────────────────── what you should see ────────────────────────── */
--  SELECT "StatusKey", "StatusName", "SortOrder"
--    FROM "OrderStatus" WHERE "SortOrder" <= 10 ORDER BY "SortOrder";
--
--  SELECT p."PermissionKey"
--    FROM "RolePermission" rp
--    JOIN "Role" r ON r."RoleId" = rp."RoleId"
--    JOIN "Permission" p ON p."PermissionId" = rp."PermissionId"
--   WHERE r."RoleKey" = 'warehouse-keeper'
--   ORDER BY 1;
