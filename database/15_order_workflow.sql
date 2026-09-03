-- 15_order_workflow.sql
--
-- The order lifecycle the business actually runs, a new role to work part of
-- it, and a way for a salesperson to ask permission to change an order.
--
-- Safe to run twice.

-- ═══════════════════════════════════════════════════════════════════════════
--  1. THE CHAIN
-- ═══════════════════════════════════════════════════════════════════════════
--
--   1  DRAFT                    sales writes it
--   2  SUBMITTED                sales sends it -- admin is asked to decide
--   3  CONFIRMED                admin says yes        (or 3b DECLINED)
--   4  INVOICED                 sales OR admin bills it
--   5  TO_ORDER_DEPT            warehouse keeper has the stock ready and moving
--   6  AT_ORDER_DEPT            order dept has it in hand
--   7  PACKAGING                order dept is packing
--   8  DISPATCHED               order dept sends it out
--   9  DELIVERED                sales confirms it arrived
--
-- StatusKey is varchar(20), which is why steps 5 and 6 are TO_ORDER_DEPT and
-- AT_ORDER_DEPT rather than spelled out. The StatusName carries the full
-- wording and that is what any screen shows.
--
-- SortOrder IS the step number. Everything off the chain -- DECLINED,
-- CREDIT_HOLD, CANCELLED, RETURNED -- sorts after it, and the two statuses this
-- workflow replaced (PROCESSING, PACKED) sort last of all. They are kept, not
-- deleted: one order is sitting on PACKED right now and deleting the row it
-- points at would break it.

INSERT INTO "OrderStatus" ("StatusKey", "StatusName", "SortOrder")
SELECT v."StatusKey", v."StatusName", v."SortOrder"
FROM (VALUES
    ('TO_ORDER_DEPT',   'On way to Order Dept',   5),
    ('AT_ORDER_DEPT',   'Received at Order Dept', 6),
    ('PACKAGING',              'Packaging',              7),
    ('DECLINED',               'Declined',              20)
) AS v("StatusKey", "StatusName", "SortOrder")
WHERE NOT EXISTS (
    SELECT 1 FROM "OrderStatus" s WHERE s."StatusKey" = v."StatusKey"
);

-- Put the existing rows where they belong in the new order.
UPDATE "OrderStatus" SET "SortOrder" =  1 WHERE "StatusKey" = 'DRAFT';
UPDATE "OrderStatus" SET "SortOrder" =  2 WHERE "StatusKey" = 'SUBMITTED';
UPDATE "OrderStatus" SET "SortOrder" =  3 WHERE "StatusKey" = 'CONFIRMED';
UPDATE "OrderStatus" SET "SortOrder" =  4 WHERE "StatusKey" = 'INVOICED';
UPDATE "OrderStatus" SET "SortOrder" =  8 WHERE "StatusKey" = 'DISPATCHED';
UPDATE "OrderStatus" SET "SortOrder" =  9 WHERE "StatusKey" = 'DELIVERED';

UPDATE "OrderStatus" SET "SortOrder" = 21 WHERE "StatusKey" = 'CREDIT_HOLD';
UPDATE "OrderStatus" SET "SortOrder" = 22 WHERE "StatusKey" = 'CANCELLED';
UPDATE "OrderStatus" SET "SortOrder" = 23 WHERE "StatusKey" = 'RETURNED';

-- Superseded by the chain above. Left in place for the rows still pointing at
-- them; nothing new should ever be set to either.
UPDATE "OrderStatus" SET "StatusName" = 'Processing (old)', "SortOrder" = 90 WHERE "StatusKey" = 'PROCESSING';
UPDATE "OrderStatus" SET "StatusName" = 'Packed (old)',     "SortOrder" = 91 WHERE "StatusKey" = 'PACKED';

-- ═══════════════════════════════════════════════════════════════════════════
--  2. THE WAREHOUSE KEEPER
-- ═══════════════════════════════════════════════════════════════════════════
--
-- Step 5 belongs to somebody: whoever picks the stock off the shelf and sends
-- it to the order department. That was nobody's job in this schema.

INSERT INTO "Role" ("RoleKey", "RoleName", "Description", "HomePath",
                   "IsStaffRole", "RequiresEmail", "IsSystem")
SELECT 'warehouse-keeper', 'Warehouse Keeper',
       'Picks order stock off the shelf and sends it to the order department.',
       '/dashboard', TRUE, TRUE, TRUE
WHERE NOT EXISTS (SELECT 1 FROM "Role" WHERE "RoleKey" = 'warehouse-keeper');

-- One new permission: the right to move an order along step 5.
INSERT INTO "Permission" ("PermissionKey", "Label", "GroupName")
SELECT 'orders.warehouse', 'Prepare and send order stock', 'Orders'
WHERE NOT EXISTS (SELECT 1 FROM "Permission" WHERE "PermissionKey" = 'orders.warehouse');

-- What the keeper starts with. The Super Admin can change any of it from
-- Setup > Roles afterwards -- this is a starting point, not a fixed list.
INSERT INTO "RolePermission" ("RoleId", "PermissionId")
SELECT r."RoleId", p."PermissionId"
FROM "Role" r
CROSS JOIN "Permission" p
WHERE r."RoleKey" = 'warehouse-keeper'
  AND p."PermissionKey" IN (
        'orders.view',        -- see the orders waiting on them
        'orders.warehouse',   -- move one to "on way to order dept"
        'stock.view',         -- see what is on the shelf
        'stock.transfer',     -- move stock between locations
        'delivery.view'       -- see where things went
  )
  AND NOT EXISTS (
      SELECT 1 FROM "RolePermission" rp
      WHERE rp."RoleId" = r."RoleId" AND rp."PermissionId" = p."PermissionId"
  );

-- Super Admin gets the new permission too, so the chain is never blocked
-- waiting for somebody else to be online.
INSERT INTO "RolePermission" ("RoleId", "PermissionId")
SELECT r."RoleId", p."PermissionId"
FROM "Role" r, "Permission" p
WHERE r."RoleKey" = 'super-admin'
  AND p."PermissionKey" = 'orders.warehouse'
  AND NOT EXISTS (
      SELECT 1 FROM "RolePermission" rp
      WHERE rp."RoleId" = r."RoleId" AND rp."PermissionId" = p."PermissionId"
  );

-- ═══════════════════════════════════════════════════════════════════════════
--  3. ASKING PERMISSION TO CHANGE AN ORDER
-- ═══════════════════════════════════════════════════════════════════════════
--
-- Only the Super Admin may edit or delete an order. A salesperson who needs one
-- changed files a request naming the order and the reason; the admin approves
-- it with a tick or refuses it with a cross, from their dashboard.
--
-- An APPROVED request is a one-shot key: it lets that person make that one
-- change, and is spent the moment they do. Without that it would be a standing
-- permission granted by accident.

CREATE TABLE IF NOT EXISTS "OrderChangeRequest" (
    "RequestId"      SERIAL PRIMARY KEY,
    "OrderId"        INT          NOT NULL,
    "RequestedByUserId" INT       NOT NULL,
    -- 'EDIT' or 'DELETE'
    "Kind"           VARCHAR(10)  NOT NULL,
    "Reason"         VARCHAR(500) NOT NULL,
    -- 'PENDING' | 'APPROVED' | 'DECLINED' | 'USED'
    "Status"         VARCHAR(10)  NOT NULL DEFAULT 'PENDING',
    "DecidedByUserId" INT         NULL,
    "DecidedAt"      TIMESTAMP    NULL,
    "DecisionNote"   VARCHAR(500) NULL,
    "CreatedAt"      TIMESTAMP    NOT NULL DEFAULT NOW(),

    CONSTRAINT "OrderChangeRequest_OrderId_fkey"
        FOREIGN KEY ("OrderId") REFERENCES "SalesOrder" ("OrderId") ON DELETE CASCADE,
    CONSTRAINT "OrderChangeRequest_RequestedBy_fkey"
        FOREIGN KEY ("RequestedByUserId") REFERENCES "User" ("UserId"),
    CONSTRAINT "OrderChangeRequest_DecidedBy_fkey"
        FOREIGN KEY ("DecidedByUserId") REFERENCES "User" ("UserId")
);

CREATE INDEX IF NOT EXISTS "IX_OrderChangeRequest_Status"
    ON "OrderChangeRequest" ("Status");
CREATE INDEX IF NOT EXISTS "IX_OrderChangeRequest_OrderId"
    ON "OrderChangeRequest" ("OrderId");

-- One open request per person per order per kind. Without this, clicking the
-- button twice puts two identical rows on the admin's dashboard.
CREATE UNIQUE INDEX IF NOT EXISTS "UX_OrderChangeRequest_Open"
    ON "OrderChangeRequest" ("OrderId", "RequestedByUserId", "Kind")
    WHERE "Status" = 'PENDING';

-- ═══════════════════════════════════════════════════════════════════════════
--  4. WHEN THE ADMIN WAS LAST REMINDED
-- ═══════════════════════════════════════════════════════════════════════════
--
-- Orders sitting on SUBMITTED are waiting on one person, and the reminder goes
-- out every six hours until they decide. This column is how the job knows it
-- has already asked, so a restart does not send the whole backlog again.

ALTER TABLE "SalesOrder"
    ADD COLUMN IF NOT EXISTS "ConfirmRemindedAt" TIMESTAMP NULL;

-- Verify:
--   SELECT "StatusKey","StatusName","SortOrder" FROM "OrderStatus" ORDER BY "SortOrder";
--   SELECT r."RoleName", p."PermissionKey" FROM "Role" r
--     JOIN "RolePermission" rp ON rp."RoleId"=r."RoleId"
--     JOIN "Permission" p ON p."PermissionId"=rp."PermissionId"
--    WHERE r."RoleKey"='warehouse-keeper' ORDER BY 2;
