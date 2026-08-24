/* ===========================================================================
   ADVANCE POS SYSTEM (AdvPOS) -- seed data
   ---------------------------------------------------------------------------
   Every row below is the frontend's own mock data, transcribed. Where the
   frontend derived a figure in JavaScript (per-location stock, invoice
   totals, order lines) the derivation was carried out and the RESULT stored,
   because a database keeps facts, not the code that made them.

   Row counts: five or more records in every transactional and master table.
   A handful of lookup tables hold their complete real value set instead --
   there are exactly three collection statuses and four return conditions in
   this business, and inventing a fifth would put a lie in the reference data.
   Each such table is marked below with its true size.

   Load order: 01_schema.sql -> 02_seed.sql -> 03_sequence_reset.sql
   =========================================================================== */

/* ===========================================================================
   SECTION 1 -- LOOKUP DATA
   =========================================================================== */

INSERT INTO "SeverityLevel" (severity_id, severity_key, severity_name) VALUES
    (1, 'info',    'Information'),
    (2, 'success', 'Success'),
    (3, 'warning', 'Warning'),
    (4, 'danger',  'Danger'),
    (5, 'muted',   'Neutral');

/* Four staff roles that sign in, and three party roles the Super Admin opens
   on somebody's behalf. requires_email is what makes the difference: it is
   the parent value the "User" table's CHECK reads. */
INSERT INTO "Role" (role_id, role_key, role_name, description, home_path, is_staff_role, requires_email, is_system) VALUES
    (1, 'super-admin',       'Super Admin',         'Full access - every module, plus users, setup and backup.',      '/dashboard', TRUE,  TRUE,  TRUE),
    (2, 'accountant',        'Accountant',          'Purchases, money in/out, ledgers and financial statements.',     '/dashboard', TRUE,  TRUE,  TRUE),
    (3, 'order-dept',        'Order Department',    'Order queue, packing, stock, transfers and dispatch.',           '/dashboard', TRUE,  TRUE,  TRUE),
    (4, 'sales',             'Sales',               'Take customer orders, track their status, follow up payments.',  '/dashboard', TRUE,  TRUE,  TRUE),
    (5, 'customer',          'Customer',            'A shop that buys from us. Opened by the Super Admin.',           '/',          FALSE, FALSE, TRUE),
    (6, 'supplier',          'Supplier',            'A business we buy from. Opened by the Super Admin.',             '/',          FALSE, FALSE, TRUE),
    (7, 'customer-supplier', 'Customer & Supplier', 'Buys from us and supplies us. Rare, but they exist.',            '/',          FALSE, FALSE, TRUE);

INSERT INTO "Permission" (permission_id, permission_key, label, group_name) VALUES
    (1,  'orders.view',      'See customer orders',        'Sales'),
    (2,  'orders.create',    'Take a customer order',      'Sales'),
    (3,  'orders.approve',   'Approve & pack orders',      'Sales'),
    (4,  'invoices.view',    'See sale invoices',          'Sales'),
    (5,  'invoices.create',  'Make a sale invoice',        'Sales'),
    (6,  'returns.sales',    'Handle sales returns',       'Sales'),
    (7,  'sales.direct',     'Sell over the counter',      'Sales'),
    (8,  'customers.view',   'See customers',              'Sales'),
    (9,  'customers.manage', 'Add & edit customers',       'Sales'),
    (10, 'customers.tax',    'Fill customer tax details',  'Sales'),
    (11, 'limits.manage',    'Set credit limits',          'Sales'),
    (12, 'visits.view',      'See customer visits',        'Sales'),
    (13, 'purchases.view',   'See purchases',              'Purchases'),
    (14, 'purchases.manage', 'Make purchase documents',    'Purchases'),
    (15, 'receipts.stock',   'Receive stock',              'Purchases'),
    (16, 'suppliers.manage', 'Add & edit suppliers',       'Purchases'),
    (17, 'stock.view',       'See stock',                  'Stock'),
    (18, 'stock.transfer',   'Move stock between locations','Stock'),
    (19, 'stock.correct',    'Correct stock',              'Stock'),
    (20, 'products.manage',  'Add & edit items',           'Stock'),
    (21, 'cost.view',        'See cost price',             'Stock'),
    (22, 'money.view',       'See money in / out',         'Money'),
    (23, 'money.manage',     'Record money in / out',      'Money'),
    (24, 'ledger.view',      'See ledgers & accounts',     'Money'),
    (25, 'ledger.manage',    'Make manual entries',        'Money'),
    (26, 'statements.view',  'See financial statements',   'Money'),
    (27, 'expenses.manage',  'Record expenses',            'Money'),
    (28, 'delivery.view',    'See deliveries',             'Delivery'),
    (29, 'delivery.manage',  'Book & update deliveries',   'Delivery'),
    (30, 'claims.view',      'See claims',                 'Claims'),
    (31, 'claims.receive',   'Take claims from customers', 'Claims'),
    (32, 'claims.settle',    'Send & settle with supplier','Claims'),
    (33, 'reports.view',     'See reports',                'Reports'),
    (34, 'reports.full',     'See profit & cost reports',  'Reports'),
    (35, 'setup.manage',     'Change setup & settings',    'Administration'),
    (36, 'users.manage',     'Add & edit users',           'Administration'),
    (37, 'backup.manage',    'Backup & restore',           'Administration'),
    (38, 'activity.view',    'See activity history',       'Administration'),
    (39, 'records.delete',   'Delete records',             'Administration');

INSERT INTO "Province" (province_id, province_name) VALUES
    (1, 'Sindh'),
    (2, 'Punjab'),
    (3, 'KPK'),
    (4, 'Balochistan'),
    (5, 'Islamabad Capital'),
    (6, 'AJK'),
    (7, 'Gilgit-Baltistan');

INSERT INTO "LocationKind" (kind_id, kind_key, kind_name) VALUES
    (1, 'warehouse',  'Warehouse'),
    (2, 'shop',       'Shop'),
    (3, 'department', 'Department'),
    (4, 'claim',      'Claim / Damaged'),
    (5, 'transit',    'In Transit');

INSERT INTO "PartyCategory" (category_id, category_key, category_name) VALUES
    (1, 'RETAILER',     'Retailer'),
    (2, 'WHOLESALER',   'Wholesaler'),
    (3, 'DISTRIBUTOR',  'Distributor'),
    (4, 'MANUFACTURER', 'Manufacturer'),
    (5, 'AGENT',        'Agent');

/* Complete set: three policies exist. */
INSERT INTO "CreditHoldPolicy" (policy_id, policy_key, policy_name, description) VALUES
    (1, 'NONE',  'No limit check', 'Orders go through whatever the balance is.'),
    (2, 'WARN',  'Warn only',      'Order is flagged for the owner but is not stopped.'),
    (3, 'BLOCK', 'Block',          'Order is held until the customer comes back under limit.');

INSERT INTO "PaymentMethod" (method_id, method_key, method_name, method_kind, is_active) VALUES
    (1, 'CASH',        'Cash',        'cash',   TRUE),
    (2, 'BANK',        'Bank',        'bank',   TRUE),
    (3, 'JAZZCASH',    'JazzCash',    'wallet', TRUE),
    (4, 'EASYPAISA',   'Easypaisa',   'wallet', TRUE),
    (5, 'CREDIT',      'Credit',      'credit', TRUE),
    (6, 'CHEQUE',      'Cheque',      'bank',   TRUE),
    (7, 'CREDIT_NOTE', 'Credit Note', 'credit', TRUE),
    (8, 'PETTY_CASH',  'Petty Cash',  'cash',   TRUE);

INSERT INTO "OrderStatus" (status_id, status_key, status_name, sort_order) VALUES
    (1,  'DRAFT',       'Draft',        1),
    (2,  'SUBMITTED',   'Submitted',    2),
    (3,  'CREDIT_HOLD', 'Limit Cross',  3),
    (4,  'CONFIRMED',   'Confirmed',    4),
    (5,  'PROCESSING',  'Processing',   5),
    (6,  'PACKED',      'Packed',       6),
    (7,  'DISPATCHED',  'Dispatched',   7),
    (8,  'INVOICED',    'Invoiced',     8),
    (9,  'DELIVERED',   'Delivered',    9),
    (10, 'CANCELLED',   'Cancelled',   10),
    (11, 'RETURNED',    'Returned',    11);

/* Serves sale invoices and purchase invoices alike. */
INSERT INTO "InvoiceStatus" (status_id, status_key, status_name) VALUES
    (1, 'DRAFT',   'Draft'),
    (2, 'ISSUED',  'Issued'),
    (3, 'POSTED',  'Posted'),
    (4, 'PARTIAL', 'Part paid'),
    (5, 'PAID',    'Paid'),
    (6, 'OVERDUE', 'Overdue'),
    (7, 'VOID',    'Void');

/* Complete set: four states, shared by sales and purchase returns. */
INSERT INTO "ReturnStatus" (status_id, status_key, status_name) VALUES
    (1, 'DRAFT',    'Draft'),
    (2, 'APPROVED', 'Approved'),
    (3, 'POSTED',   'Posted'),
    (4, 'REJECTED', 'Rejected');

INSERT INTO "PurchaseOrderStatus" (status_id, status_key, status_name) VALUES
    (1, 'DRAFT',              'Draft'),
    (2, 'PENDING_APPROVAL',   'Pending Approval'),
    (3, 'APPROVED',           'Approved'),
    (4, 'PARTIALLY_RECEIVED', 'Partially Received'),
    (5, 'RECEIVED',           'Received'),
    (6, 'CANCELLED',          'Cancelled'),
    (7, 'CLOSED',             'Closed');

INSERT INTO "PostingStatus" (status_id, status_key, status_name) VALUES
    (1, 'DRAFT',      'Draft'),
    (2, 'POSTED',     'Posted'),
    (3, 'REVERSED',   'Reversed'),
    (4, 'REJECTED',   'Rejected'),
    (5, 'CANCELLED',  'Cancelled'),
    (6, 'RECONCILED', 'Reconciled');

INSERT INTO "TransferStatus" (status_id, status_key, status_name) VALUES
    (1, 'DRAFT',            'Draft'),
    (2, 'PENDING_APPROVAL', 'Pending Approval'),
    (3, 'APPROVED',         'Approved'),
    (4, 'IN_TRANSIT',       'In Transit'),
    (5, 'RECEIVED',         'Received'),
    (6, 'REJECTED',         'Rejected');

INSERT INTO "DeliveryStatus" (status_id, status_key, status_name, is_open) VALUES
    (1, 'NOT_DISPATCHED',     'Not dispatched',     TRUE),
    (2, 'BOOKED',             'Booked',             TRUE),
    (3, 'AWAITING',           'Sent - unconfirmed', TRUE),
    (4, 'IN_TRANSIT',         'In transit',         TRUE),
    (5, 'OUT_FOR_DELIVERY',   'Out for delivery',   TRUE),
    (6, 'DELIVERED',          'Delivered',          FALSE),
    (7, 'FAILED',             'Failed',             TRUE),
    (8, 'RETURNED_TO_SENDER', 'Returned to sender', FALSE);

INSERT INTO "ClaimStage" (stage_id, stage_key, stage_name, is_open) VALUES
    (1, 'RECEIVED',    'In claim stock', TRUE),
    (2, 'SENT',        'With supplier',  TRUE),
    (3, 'REPLACED',    'Replaced',       FALSE),
    (4, 'CREDITED',    'Credited',       FALSE),
    (5, 'REJECTED',    'Refused',        FALSE),
    (6, 'WRITTEN_OFF', 'Written off',    FALSE);

INSERT INTO "ClaimReason" (reason_id, reason_key, reason_name, usually_accepted) VALUES
    (1, 'dead',        'Dead on arrival',     TRUE),
    (2, 'not-working', 'Stopped working',     TRUE),
    (3, 'weak',        'Weak / low backup',   TRUE),
    (4, 'damaged',     'Physically damaged',  FALSE),
    (5, 'burnt',       'Burnt',               FALSE),
    (6, 'wrong-item',  'Wrong item supplied', TRUE),
    (7, 'short',       'Short in packing',    TRUE);

/* Complete set: three things can happen to the customer at the counter. */
INSERT INTO "ClaimOutcome" (outcome_id, outcome_key, outcome_name) VALUES
    (1, 'REPLACED_NOW', 'Replaced on the spot'),
    (2, 'CREDIT_NOTE',  'Credit given'),
    (3, 'WAITING',      'Customer waiting');

/* Complete set: money the rep took is awaiting, confirmed, or it bounced. */
INSERT INTO "CollectionStatus" (status_id, status_key, status_name) VALUES
    (1, 'AWAITING',  'Awaiting confirmation'),
    (2, 'CONFIRMED', 'Confirmed'),
    (3, 'BOUNCED',   'Bounced');

INSERT INTO "AdjustmentReason" (reason_id, reason_key, reason_name) VALUES
    (1, 'PHYSICAL_COUNT', 'Physical count discrepancy'),
    (2, 'DAMAGED',        'Damaged in handling'),
    (3, 'EXPIRED',        'Expired stock write-off'),
    (4, 'FOUND',          'Found extra stock'),
    (5, 'WRITE_OFF',      'Write-off'),
    (6, 'OTHER',          'Other');

INSERT INTO "MovementType" (movement_type_id, type_key, type_name) VALUES
    (1, 'PURCHASE',        'Stock received'),
    (2, 'SALE',            'Sold'),
    (3, 'TRANSFER_OUT',    'Transferred out'),
    (4, 'TRANSFER_IN',     'Transferred in'),
    (5, 'ADJUSTMENT',      'Stock correction'),
    (6, 'SALE_RETURN',     'Sales return'),
    (7, 'PURCHASE_RETURN', 'Purchase return');

/* Complete set: four conditions a returned piece can be in. */
INSERT INTO "ReturnCondition" (condition_id, condition_key, condition_name, is_resalable) VALUES
    (1, 'RESALABLE', 'Resalable', TRUE),
    (2, 'DAMAGED',   'Damaged',   FALSE),
    (3, 'EXPIRED',   'Expired',   FALSE),
    (4, 'MISSING',   'Missing',   FALSE);

/* Complete set: four ways a shop visit can end. */
INSERT INTO "VisitOutcome" (outcome_id, outcome_key, outcome_name) VALUES
    (1, 'ORDER_PLACED',      'Order Placed'),
    (2, 'NO_ORDER',          'No Order'),
    (3, 'FOLLOWUP',          'Followup'),
    (4, 'PAYMENT_COLLECTED', 'Payment Collected');

INSERT INTO "VoucherType" (voucher_type_id, type_code, type_name, is_receipt) VALUES
    (1, 'CR', 'Cash Receipt',    TRUE),
    (2, 'CP', 'Cash Payment',    FALSE),
    (3, 'BR', 'Bank Receipt',    TRUE),
    (4, 'BP', 'Bank Payment',    FALSE),
    (5, 'WR', 'Wallet Receipt',  TRUE),
    (6, 'WP', 'Wallet Payment',  FALSE),
    (7, 'JV', 'Journal Voucher', FALSE);

INSERT INTO "JournalEntryType" (entry_type_id, type_key, type_name) VALUES
    (1, 'SALE',     'Sale'),
    (2, 'RECEIPT',  'Receipt'),
    (3, 'PURCHASE', 'Purchase'),
    (4, 'PAYMENT',  'Payment'),
    (5, 'EXPENSE',  'Expense'),
    (6, 'JOURNAL',  'Manual entry'),
    (7, 'TRANSFER', 'Stock transfer');

/* Complete set: three kinds of backup, three outcomes. */
INSERT INTO "BackupType" (backup_type_id, type_key, type_name) VALUES
    (1, 'FULL',        'Full'),
    (2, 'INCREMENTAL', 'Incremental'),
    (3, 'MANUAL',      'Manual');

INSERT INTO "BackupStatus" (status_id, status_key, status_name) VALUES
    (1, 'SUCCESS', 'Success'),
    (2, 'FAILED',  'Failed'),
    (3, 'RUNNING', 'Running');


/* ===========================================================================
   SECTION 2 -- COMPANY, GEOGRAPHY, LOCATIONS, SETUP
   =========================================================================== */

INSERT INTO "City" (city_id, city_name, province_id) VALUES
    (1,  'Karachi',    1),
    (2,  'Lahore',     2),
    (3,  'Islamabad',  5),
    (4,  'Quetta',     4),
    (5,  'Multan',     2),
    (6,  'Faisalabad', 2),
    (7,  'Peshawar',   3),
    (8,  'Hyderabad',  1),
    (9,  'Sialkot',    2),
    (10, 'Rawalpindi', 2),
    (11, 'Sukkur',     1);

/* Single tenant: one company row is the whole truth here. */
INSERT INTO "Company" (company_id, company_name, legal_name, address_line, city_id, country, phone, email, ntn, strn, fiscal_year_start_month, currency_code, currency_symbol, foreign_rate) VALUES
    (1, 'VIZO Pakistan', 'VIZO Trading Company', 'Kohinoor Market, Saddar', 1, 'Pakistan', '021 3241 2345', 'info@vizo.com.pk', '4270118-9', '17-00-8823-115-46', 10, 'PKR', 'PKR', 1.0000);

INSERT INTO "AppSetting" (setting_id, setting_group, setting_key, setting_value, description) VALUES
    (1,  'stock',    'stock.negativeStock',            'warn',                'Block a sale that would push stock below zero, or just warn.'),
    (2,  'stock',    'stock.lowStockAlerts',           'true',                'Warn when an item drops to its minimum quantity.'),
    (3,  'stock',    'stock.showPacking',              'true',                'Show packet (carton) columns beside piece quantities.'),
    (4,  'stock',    'stock.multipleBarcodes',         'true',                'Let one item carry several barcodes.'),
    (5,  'sales',    'sales.perInvoiceLimitDefault',   '150000.00',           'Cap on a single invoice value, PKR. 0 = no cap.'),
    (6,  'sales',    'sales.ledgerLimitDefault',       '500000.00',           'Cap on a customer total outstanding balance, PKR.'),
    (7,  'sales',    'sales.creditDaysDefault',        '15',                  'Default payment terms in days.'),
    (8,  'sales',    'sales.onLimitCross',             'warn',                'Stop the order, or warn, when a limit is crossed.'),
    (9,  'sales',    'sales.requireOrderApproval',     'true',                'Require an approval step between order and invoice.'),
    (10, 'sales',    'sales.trackSalesman',            'true',                'Attribute each invoice to a salesman.'),
    (11, 'sales',    'sales.whatsappShare',            'true',                'Show a Share on WhatsApp action on orders and invoices.'),
    (12, 'delivery', 'delivery.enabled',               'true',                'Deliveries are booked with third-party courier companies.'),
    (13, 'delivery', 'delivery.defaultCourierId',      '1',                   'Courier pre-selected on a new booking.'),
    (14, 'delivery', 'delivery.chargeCustomer',        'false',               'Charge the booking fee to the customer on the invoice.'),
    (15, 'delivery', 'delivery.trackCod',              'true',                'Track cash-on-delivery settlement from the courier.'),
    (16, 'delivery', 'delivery.requireProof',          'false',               'Ask for a delivery photo or signature.'),
    (17, 'claim',    'claim.windowDays',               '180',                 'Days after purchase a claim is still accepted.'),
    (18, 'claim',    'claim.remindSupplierAfterDays',  '14',                  'Chase the supplier once a claim has sat this long.'),
    (19, 'claim',    'claim.remindEveryHours',         '48',                  'Then keep asking this often.'),
    (20, 'claim',    'claim.remindUnsentAfterDays',    '3',                   'Nudge the desk if claims sit in stock unsent this long.'),
    (21, 'claim',    'claim.replaceUpfront',           'true',                'Hand the shop a replacement before the supplier settles.'),
    (22, 'claim',    'claim.writeOffAccount',          'Warranty & Claims',   'Rejected claims are written off here.');

/* in_charge_user_id is filled by the UPDATE further down -- the people do not
   exist yet, and they in turn point back at a home location. */
INSERT INTO "Location" (location_id, location_code, location_name, kind_id, city_id, address_line, in_charge_user_id, is_active, is_default, exclude_from_sellable) VALUES
    (1, 'LOC-01', 'Warehouse',        1, 1, 'Kohinoor Market, Saddar, Karachi', NULL, TRUE, FALSE, FALSE),
    (2, 'LOC-02', 'Order Department', 3, 1, 'Kohinoor Market, Saddar, Karachi', NULL, TRUE, TRUE,  FALSE),
    (3, 'LOC-03', 'Shop 2',           2, 1, 'Saddar Mobile Plaza, Karachi',     NULL, TRUE, FALSE, FALSE),
    (4, 'LOC-04', 'Claim Stock',      4, 1, 'Kohinoor Market, Saddar, Karachi', NULL, TRUE, FALSE, TRUE),
    (5, 'LOC-05', 'In Transit',       5, 1, 'Between locations',                NULL, TRUE, FALSE, TRUE);

INSERT INTO "DocumentSeries" (series_id, series_key, label, prefix, include_year, padding, next_number) VALUES
    (1,  'sales.order',      'Customer Order',    'ORD', TRUE, 4, 143),
    (2,  'sales.invoice',    'Sale Invoice',      'INV', TRUE, 4, 8868),
    (3,  'sales.return',     'Sales Return',      'SR',  TRUE, 4, 41),
    (4,  'purchase.order',   'Order to Supplier', 'PO',  TRUE, 4, 62),
    (5,  'purchase.receipt', 'Stock Received',    'GRN', TRUE, 4, 90),
    (6,  'purchase.invoice', 'Purchase Invoice',  'PI',  TRUE, 4, 2029),
    (7,  'purchase.return',  'Purchase Return',   'PR',  TRUE, 4, 9),
    (8,  'stock.transfer',   'Stock Transfer',    'TRF', TRUE, 4, 3671),
    (9,  'stock.correction', 'Stock Correction',  'ADJ', TRUE, 4, 24),
    (10, 'money.received',   'Money Received',    'RV',  TRUE, 4, 512),
    (11, 'money.paid',       'Money Paid',        'PV',  TRUE, 4, 388),
    (12, 'manual.entry',     'Manual Entry',      'JV',  TRUE, 4, 180),
    (13, 'delivery.run',     'Delivery',          'DLV', TRUE, 4, 218);


/* ===========================================================================
   SECTION 3 -- PEOPLE
   Role -> User -> Employee / Party
   Users 1-10 are staff. User 11 is the service account that posts automatic
   entries. Users 12-32 are the parties.

   Look at the email column: every staff row has one, and most parties do not.
   That is the rule from "Role".requires_email doing its job -- try adding a
   Sales user with a NULL email and the CHECK will refuse it.
   =========================================================================== */

INSERT INTO "User" (user_id, role_id, requires_email, full_name, email, phone, password_hash, primary_location_id, is_active, created_at) VALUES
    /* ---- staff: e-mail mandatory and unique ---- */
    (1,  1, TRUE,  'Umer Memon',    'admin@advpos.pk',     '0300 7287607', '$2b$12$PLACEHOLDER.REPLACE.ON.FIRST.LOGIN.aaaaaaaaaaaaaaaaaaaaaa', 1, TRUE,  '2025-08-01'),
    (2,  2, TRUE,  'Hassan Raza',   'accounts@advpos.pk',  '0321 1234567', '$2b$12$PLACEHOLDER.REPLACE.ON.FIRST.LOGIN.aaaaaaaaaaaaaaaaaaaaaa', 1, TRUE,  '2025-08-01'),
    (3,  2, TRUE,  'Nadia Hussain', 'nadia@vizo.com.pk',   '0301 8901234', '$2b$12$PLACEHOLDER.REPLACE.ON.FIRST.LOGIN.aaaaaaaaaaaaaaaaaaaaaa', 2, TRUE,  '2025-08-01'),
    (4,  3, TRUE,  'Bilal Ahmed',   'order@advpos.pk',     '0333 3456789', '$2b$12$PLACEHOLDER.REPLACE.ON.FIRST.LOGIN.aaaaaaaaaaaaaaaaaaaaaa', 2, TRUE,  '2025-08-01'),
    (5,  3, TRUE,  'Junaid Akhtar', 'junaid@vizo.com.pk',  '0314 9012345', '$2b$12$PLACEHOLDER.REPLACE.ON.FIRST.LOGIN.aaaaaaaaaaaaaaaaaaaaaa', 2, TRUE,  '2025-08-01'),
    (6,  3, TRUE,  'Ahmed Riaz',    'ahmed@vizo.com.pk',   '0317 5678901', '$2b$12$PLACEHOLDER.REPLACE.ON.FIRST.LOGIN.aaaaaaaaaaaaaaaaaaaaaa', 1, TRUE,  '2025-08-01'),
    (7,  4, TRUE,  'Zara Malik',    'sales@advpos.pk',     '0307 6789012', '$2b$12$PLACEHOLDER.REPLACE.ON.FIRST.LOGIN.aaaaaaaaaaaaaaaaaaaaaa', 3, TRUE,  '2025-08-01'),
    (8,  4, TRUE,  'Imran Iqbal',   'imran@vizo.com.pk',   '0334 7890123', '$2b$12$PLACEHOLDER.REPLACE.ON.FIRST.LOGIN.aaaaaaaaaaaaaaaaaaaaaa', 3, TRUE,  '2025-08-01'),
    (9,  4, TRUE,  'Sara Khan',     'sara@vizo.com.pk',    '0322 2345678', '$2b$12$PLACEHOLDER.REPLACE.ON.FIRST.LOGIN.aaaaaaaaaaaaaaaaaaaaaa', 2, TRUE,  '2025-08-01'),
    (10, 4, TRUE,  'Asad Ali',      'asad@vizo.com.pk',    '0303 1234567', '$2b$12$PLACEHOLDER.REPLACE.ON.FIRST.LOGIN.aaaaaaaaaaaaaaaaaaaaaa', 2, FALSE, '2025-08-01'),
    (11, 1, TRUE,  'AdvPOS System', 'system@advpos.pk',    NULL,           '$2b$12$PLACEHOLDER.REPLACE.ON.FIRST.LOGIN.aaaaaaaaaaaaaaaaaaaaaa', 2, TRUE,  '2025-08-01'),

    /* ---- customers: e-mail optional, and most have none ---- */
    (12, 5, FALSE, 'Hafeez Center Shop #28',      'info@hafeezshop28.pk', '0300 4567890', NULL, 2, TRUE, '2025-08-01'),
    (13, 5, FALSE, 'Mobile Zone Lahore',          NULL,                   '0321 1234567', NULL, 2, TRUE, '2025-08-01'),
    (14, 5, FALSE, 'Saddar Mobile Plaza',         'saddarmobile@gmail.com','0333 9876543',NULL, 1, TRUE, '2025-08-01'),
    (15, 5, FALSE, 'Blue Area Distributors',      NULL,                   '0345 6789012', NULL, 3, TRUE, '2025-08-01'),
    (16, 5, FALSE, 'Cellular World KHI',          NULL,                   '0317 8901234', NULL, 1, TRUE, '2025-08-01'),
    (17, 5, FALSE, 'Faisal Mobile Mart',          NULL,                   '0322 3344556', NULL, 2, TRUE, '2025-08-01'),
    (18, 5, FALSE, 'Quetta Cellular',             NULL,                   '0307 5566778', NULL, 1, TRUE, '2026-08-14'),
    (19, 5, FALSE, 'Mobilink Connect Lahore',     NULL,                   '0301 2233445', NULL, 2, TRUE, '2025-08-01'),
    (20, 5, FALSE, 'Mobile Mart Multan',          NULL,                   '0334 6677889', NULL, 2, TRUE, '2025-08-01'),
    (21, 5, FALSE, 'Star Communications',         NULL,                   '0300 7788990', NULL, 2, TRUE, '2025-08-01'),
    (22, 5, FALSE, 'Pak Mobile Centre',           NULL,                   '0314 8899001', NULL, 3, TRUE, '2025-08-01'),
    (23, 5, FALSE, 'Margalla Distributors',       NULL,                   '0345 1122334', NULL, 3, TRUE, '2025-08-01'),
    (24, 5, FALSE, 'Eden Mobile Hyderabad',       NULL,                   '0341 5566778', NULL, 1, TRUE, '2025-08-01'),
    (25, 5, FALSE, 'Universal Mobile Sialkot',    NULL,                   '0307 4455667', NULL, 2, TRUE, '2025-08-01'),
    (26, 5, FALSE, 'Galaxy Phones & Accessories', NULL,                   '0333 7788990', NULL, 3, TRUE, '2025-08-01'),

    /* ---- suppliers: same rule, e-mail optional ---- */
    (27, 6, FALSE, 'China Mobile Plaza Trading',  'sales@cmp-trading.cn',  '+86 138 0012 3456', NULL, 1, TRUE, '2025-08-01'),
    (28, 6, FALSE, 'Shenzhen Electronics Hub',    'info@sz-electronics.cn','+86 139 2233 4455', NULL, 1, TRUE, '2025-08-01'),
    (29, 6, FALSE, 'Karachi Wholesale Cells',     NULL,                    '0321 4455667',      NULL, 1, TRUE, '2025-08-01'),
    (30, 6, FALSE, 'Pak Accessories Imports',     NULL,                    '0301 5566778',      NULL, 1, TRUE, '2025-08-01'),
    (31, 6, FALSE, 'Audio Tech International',    NULL,                    '+86 137 6677 8899', NULL, 2, TRUE, '2025-08-01'),

    /* ---- buys from us and supplies us ---- */
    (32, 7, FALSE, 'Tech Bazaar Pvt Ltd',         NULL,                    '0322 9988776',      NULL, 2, TRUE, '2025-08-01');

INSERT INTO "Employee" (user_id, employee_code, is_locked, joined_on, last_login_at) VALUES
    (1,  'EMP-001', FALSE, '2025-08-01', '2026-08-15 09:58:00'),
    (2,  'EMP-002', FALSE, '2025-08-01', '2026-08-15 09:48:00'),
    (3,  'EMP-003', FALSE, '2025-08-01', '2026-08-14 10:15:00'),
    (4,  'EMP-004', FALSE, '2025-08-01', '2026-08-15 09:00:00'),
    (5,  'EMP-005', FALSE, '2025-08-01', '2026-08-15 06:00:00'),
    (6,  'EMP-006', FALSE, '2025-08-01', '2026-08-14 17:30:00'),
    (7,  'EMP-007', FALSE, '2025-08-01', '2026-08-15 09:30:00'),
    (8,  'EMP-008', FALSE, '2025-08-01', '2026-08-15 08:00:00'),
    (9,  'EMP-009', FALSE, '2025-08-01', '2026-08-15 09:55:00'),
    (10, 'EMP-010', TRUE,  '2025-08-01', '2026-08-10 11:20:00'),
    (11, 'EMP-000', FALSE, '2025-08-01', NULL);

INSERT INTO "Party" (user_id, party_code, legal_name, display_name, category_id, city_id, address_line, alt_phone, industry, ntn, strn, cnic, credit_limit, credit_days, hold_policy_id, opening_balance, sales_person_user_id, default_location_id, rating, notes) VALUES
    (12, 'VZ-C-0001', 'Hafeez Center Shop #28',      'Hafeez Center Shop #28',      2, 2, 'Hafeez Center, Gulberg', NULL, 'Mobile retail', '1234567-8', NULL, NULL,  500000.00, 30, 2,   245000.00,  9, 2, 'A', NULL),
    (13, 'VZ-C-0002', 'Mobile Zone Lahore',          'Mobile Zone Lahore',          1, 2, 'Hall Road',              NULL, 'Mobile retail', NULL,        NULL, NULL,  200000.00, 15, 3,   212400.00,  9, 2, 'C', 'Over limit and slow to pay.'),
    (14, 'VZ-C-0003', 'Saddar Mobile Plaza',         'Saddar Mobile Plaza',         1, 1, 'Saddar',                 NULL, 'Mobile retail', '9876543-2', NULL, NULL,  150000.00, 30, 2,    32750.00,  2, 1, 'B', NULL),
    (15, 'VZ-C-0004', 'Blue Area Distributors',      'Blue Area Distributors',      3, 3, 'Blue Area',              NULL, 'Distribution',  '5678901-2', NULL, NULL, 1500000.00, 45, 2,   884000.00,  4, 3, 'A', NULL),
    (16, 'VZ-C-0005', 'Cellular World KHI',          'Cellular World KHI',          2, 1, 'Tariq Road',             NULL, 'Mobile retail', '3456789-0', NULL, NULL,  800000.00, 30, 2,   156200.00,  2, 1, 'B', NULL),
    (17, 'VZ-C-0006', 'Faisal Mobile Mart',          'Faisal Mobile Mart',          1, 2, 'Gulshan Ravi',           NULL, 'Mobile retail', NULL,        NULL, NULL,  100000.00, 15, 2,    18400.00,  9, 2, 'B', NULL),
    (18, 'VZ-C-0007', 'Quetta Cellular',             'Quetta Cellular',             1, 4, 'Jinnah Road',            NULL, 'Mobile retail', NULL,        NULL, NULL,   50000.00, 15, 2,        0.00,  2, 1, 'B', 'Opened by the rep on 14 Aug 2026.'),
    (19, 'VZ-C-0008', 'Mobilink Connect Lahore',     'Mobilink Connect Lahore',     2, 2, 'Hall Road',              NULL, 'Mobile retail', NULL,        NULL, NULL,  600000.00, 30, 2,   425000.00,  9, 2, 'A', NULL),
    (20, 'VZ-C-0009', 'Mobile Mart Multan',          'Mobile Mart Multan',          1, 5, 'Hussain Agahi',          NULL, 'Mobile retail', NULL,        NULL, NULL,   75000.00, 15, 2,    64500.00,  4, 2, 'C', 'Two invoices 45+ days overdue.'),
    (21, 'VZ-C-0010', 'Star Communications',         'Star Communications',         3, 6, 'Kotwali Road',           NULL, 'Distribution',  '2233445-6', NULL, NULL, 1200000.00, 45, 2,   985000.00,  9, 2, 'A', NULL),
    (22, 'VZ-C-0011', 'Pak Mobile Centre',           'Pak Mobile Centre',           1, 7, 'Saddar Road',            NULL, 'Mobile retail', NULL,        NULL, NULL,  100000.00, 15, 2,    28000.00,  4, 3, 'B', NULL),
    (23, 'VZ-C-0012', 'Margalla Distributors',       'Margalla Distributors',       3, 3, 'F-10 Markaz',            NULL, 'Distribution',  '7788990-1', NULL, NULL, 1000000.00, 45, 2,   218000.00,  4, 3, 'A', NULL),
    (24, 'VZ-C-0013', 'Eden Mobile Hyderabad',       'Eden Mobile Hyderabad',       1, 8, 'Auto Bhan Road',         NULL, 'Mobile retail', NULL,        NULL, NULL,   75000.00, 15, 2,    12000.00,  2, 1, 'B', NULL),
    (25, 'VZ-C-0014', 'Universal Mobile Sialkot',    'Universal Mobile Sialkot',    2, 9, 'Kashmir Road',           NULL, 'Mobile retail', NULL,        NULL, NULL,  400000.00, 30, 2,   195000.00,  9, 2, 'B', NULL),
    (26, 'VZ-C-0015', 'Galaxy Phones & Accessories', 'Galaxy Phones & Accessories', 1, 10,'Commercial Market',      NULL, 'Mobile retail', NULL,        NULL, NULL,  150000.00, 30, 2,    88500.00,  4, 3, 'B', NULL),
    (27, 'VZ-S-0001', 'China Mobile Plaza Trading',  'China Mobile Plaza',          4, 1, 'Electronics Market',     NULL, 'Manufacturing', '9999991-1', NULL, NULL,       0.00,  0, 1, -1850000.00, NULL, 1, 'A', 'Settles claims quickly.'),
    (28, 'VZ-S-0002', 'Shenzhen Electronics Hub',    'Shenzhen Electronics',        4, 1, 'Electronics Market',     NULL, 'Manufacturing', '9999992-2', NULL, NULL,       0.00,  0, 1, -1240000.00, NULL, 1, 'A', NULL),
    (29, 'VZ-S-0003', 'Karachi Wholesale Cells',     'Karachi Wholesale Cells',     2, 1, 'Saddar',                 NULL, 'Wholesale',     '9999993-3', NULL, NULL,       0.00,  0, 1,  -480000.00, NULL, 1, 'B', 'Refuses physical-damage claims.'),
    (30, 'VZ-S-0004', 'Pak Accessories Imports',     'Pak Accessories',             2, 1, 'Saddar',                 NULL, 'Wholesale',     '9999994-4', NULL, NULL,       0.00,  0, 1,  -320000.00, NULL, 1, 'B', NULL),
    (31, 'VZ-S-0005', 'Audio Tech International',    'Audio Tech',                  4, 2, 'Hall Road',              NULL, 'Manufacturing', '9999995-5', NULL, NULL,       0.00,  0, 1,  -950000.00, NULL, 2, 'A', NULL),
    (32, 'VZ-B-0001', 'Tech Bazaar Pvt Ltd',         'Tech Bazaar',                 2, 2, 'Hall Road',              NULL, 'Wholesale',     '8888888-1', NULL, NULL,  300000.00, 30, 2,  -145000.00,  9, 2, 'B', 'Buys from us and supplies returned or excess stock.');

/* Now the people exist, so each location can name who runs it. */
UPDATE "Location" SET in_charge_user_id = 4 WHERE location_id = 1;
UPDATE "Location" SET in_charge_user_id = 5 WHERE location_id = 2;
UPDATE "Location" SET in_charge_user_id = 7 WHERE location_id = 3;
UPDATE "Location" SET in_charge_user_id = 6 WHERE location_id = 4;

/* Super Admin holds every capability. */
INSERT INTO "RolePermission" (role_id, permission_id)
SELECT 1, permission_id FROM "Permission";

INSERT INTO "RolePermission" (role_id, permission_id)
SELECT 2, permission_id FROM "Permission" WHERE permission_key IN (
    'orders.view', 'invoices.view', 'invoices.create', 'returns.sales',
    'customers.view', 'customers.manage', 'customers.tax', 'limits.manage',
    'visits.view', 'purchases.view', 'purchases.manage', 'suppliers.manage',
    'stock.view', 'cost.view', 'money.view', 'money.manage', 'ledger.view',
    'ledger.manage', 'statements.view', 'expenses.manage', 'delivery.view',
    'claims.view', 'reports.view', 'reports.full', 'activity.view',
    'records.delete');

INSERT INTO "RolePermission" (role_id, permission_id)
SELECT 3, permission_id FROM "Permission" WHERE permission_key IN (
    'orders.view', 'orders.create', 'orders.approve', 'invoices.view',
    'invoices.create', 'returns.sales', 'sales.direct', 'customers.view',
    'customers.manage', 'visits.view', 'purchases.view', 'receipts.stock',
    'stock.view', 'stock.transfer', 'stock.correct', 'products.manage',
    'cost.view', 'delivery.view', 'delivery.manage', 'claims.view',
    'claims.receive', 'claims.settle', 'reports.view');

/* Deliberately narrow. Tax registration and credit limits are left out on
   purpose: a limit the person selling against it can raise is not a limit. */
INSERT INTO "RolePermission" (role_id, permission_id)
SELECT 4, permission_id FROM "Permission" WHERE permission_key IN (
    'orders.view', 'orders.create', 'customers.view', 'customers.manage',
    'reports.view');

INSERT INTO "UserLocation" (user_id, location_id) VALUES
    (1, 1), (1, 2), (1, 3),
    (2, 1), (2, 2), (2, 3),
    (3, 2),
    (4, 1), (4, 2),
    (5, 2),
    (6, 1), (6, 4),
    (7, 3),
    (8, 3),
    (9, 2),
    (10, 2);

INSERT INTO "UserPreference" (user_id, pref_key, pref_value) VALUES
    (1, 'notify.inApp',    'true'),
    (1, 'notify.email',    'true'),
    (1, 'notify.whatsapp', 'true'),
    (1, 'notify.push',     'false'),
    (2, 'notify.inApp',    'true'),
    (2, 'notify.email',    'true'),
    (4, 'notify.inApp',    'true'),
    (4, 'notify.whatsapp', 'true'),
    (7, 'notify.inApp',    'true'),
    (7, 'notify.whatsapp', 'true');

/* Jan to Mar are closed; Apr onwards are open, which is why the Year End
   screen only offers Apr 2026 for closing. */
INSERT INTO "FiscalPeriod" (period_id, period_name, period_year, period_month, start_date, end_date, is_closed, closed_by_user_id, closed_at) VALUES
    (1, 'Jan 2026', 2026, 1, '2026-01-01', '2026-01-31', TRUE,  2,    '2026-02-03'),
    (2, 'Feb 2026', 2026, 2, '2026-02-01', '2026-02-28', TRUE,  2,    '2026-03-04'),
    (3, 'Mar 2026', 2026, 3, '2026-03-01', '2026-03-31', TRUE,  2,    '2026-04-05'),
    (4, 'Apr 2026', 2026, 4, '2026-04-01', '2026-04-30', FALSE, NULL, NULL),
    (5, 'May 2026', 2026, 5, '2026-05-01', '2026-05-31', FALSE, NULL, NULL),
    (6, 'Jun 2026', 2026, 6, '2026-06-01', '2026-06-30', FALSE, NULL, NULL),
    (7, 'Jul 2026', 2026, 7, '2026-07-01', '2026-07-31', FALSE, NULL, NULL),
    (8, 'Aug 2026', 2026, 8, '2026-08-01', '2026-08-31', FALSE, NULL, NULL);


/* ===========================================================================
   SECTION 4 -- CARRIERS AND DELIVERY CHANNELS
   =========================================================================== */

INSERT INTO "Courier" (courier_id, courier_name, short_name, contact_person, phone, cod_settlement_days, booking_charge, cod_fee_percent, tracking_url_template, is_active) VALUES
    (1,  'TCS Courier',           'TCS',      'Rashid Mehmood', '021 111 123 456', 7,  220.00, 1.50, 'https://www.tcsexpress.com/track/{tracking}', TRUE),
    (2,  'Leopards Courier',      'Leopards', 'Adnan Siddiqui', '021 111 300 786', 5,  180.00, 1.20, 'https://leopardscourier.com/track/{tracking}', TRUE),
    (3,  'M&P Express',           'M&P',      'Kamran Tariq',   '021 111 202 202', 10, 200.00, 1.40, 'https://mulphilog.com/track/{tracking}',      TRUE),
    (4,  'Trax Logistics',        'Trax',     'Sana Iqbal',     '021 111 872 900', 4,  165.00, 1.00, 'https://trax.pk/track/{tracking}',            TRUE),
    (5,  'BlueEx',                'BlueEx',   'Hamza Sheikh',   '021 111 258 339', 6,  190.00, 1.30, 'https://blue-ex.com/track/{tracking}',        FALSE),
    (6,  'Own Rider',             'Rider',    'In-house',       '0300 7287607',    0,    0.00, 0.00, NULL,                                          TRUE),
    (7,  'PostEx',                'PostEx',   'Support Desk',   '021 111 767 839', 3,  175.00, 1.10, 'https://postex.pk/track/{tracking}',          TRUE),
    (8,  'Pak International Cargo','PIC',     'Shakeel Ahmed',  '021 3277 4411',   0,  350.00, 0.00, NULL,                                          TRUE),
    (9,  'Rehman Cargo',          'Rehman',   'Abdul Rehman',   '021 3277 9080',   0,  300.00, 0.00, NULL,                                          TRUE),
    (10, 'Mehran Railway Cargo',  'Mehran',   'Ghulam Nabi',    '021 3277 6612',   0,  280.00, 0.00, NULL,                                          TRUE),
    (11, 'NLC',                   'NLC',      'Freight Desk',   '051 111 111 652', 0,  850.00, 0.00, NULL,                                          TRUE),
    (12, 'Daewoo Cargo',          'Daewoo',   'Cargo Desk',     '042 111 007 008', 0,  780.00, 0.00, NULL,                                          TRUE),
    (13, 'Sales Rep',             'Rep',      'In-house',       '0307 6789012',    0,    0.00, 0.00, NULL,                                          TRUE);

/* confirmed_by_role_id is who gets chased for the confirmation: the rep who
   handed it over himself, or the back office for everything else. */
INSERT INTO "DeliveryChannel" (channel_id, channel_key, channel_name, description, confirmed_by_role_id, remind_after_days, remind_every_hours, auto_confirm, requires_bilty, is_active) VALUES
    (1, 'local',     'Karachi - own team',  'Karachi stock handed to the city own sales rep, delivered by hand.', 4, 0, 6,  FALSE, FALSE, TRUE),
    (2, 'online',    'Online courier',      'Booked with a courier that has its own tracking portal.',            3, 2, 24, FALSE, FALSE, TRUE),
    (3, 'cargo',     'Local cargo',         'Goods transport companies. Confirmed by phone with the customer.',   3, 2, 24, FALSE, TRUE,  TRUE),
    (4, 'logistics', 'Heavy - logistics',   'Bulk consignments by freight. The bilty receipt is the proof.',      3, 4, 24, FALSE, TRUE,  TRUE);

INSERT INTO "ChannelCarrier" (channel_id, courier_id) VALUES
    (1, 6), (1, 13),
    (2, 7), (2, 1), (2, 2), (2, 3), (2, 4),
    (3, 8), (3, 9), (3, 10),
    (4, 8), (4, 11), (4, 12);




/* ===========================================================================
   SECTION 5 -- CATALOGUE
   =========================================================================== */

INSERT INTO "Category" (category_id, category_name, parent_category_id, is_active) VALUES
    (1,  'Accessories', NULL, TRUE),
    (2,  'Earbuds',     1,    TRUE),
    (3,  'Handfree',    1,    TRUE),
    (4,  'Speakers',    1,    TRUE),
    (5,  'Power',       NULL, TRUE),
    (6,  'Chargers',    5,    TRUE),
    (7,  'Power Banks', 5,    TRUE),
    (8,  'Batteries',   5,    TRUE),
    (9,  'Cables',      NULL, TRUE),
    (10, 'Type-C',      9,    TRUE),
    (11, 'Lightning',   9,    TRUE),
    (12, 'Micro-USB',   9,    TRUE),
    (13, 'Bluetooth',   NULL, TRUE),
    (14, 'LED Bulbs',   NULL, TRUE);

/* The two-digit code is the same one that opens the item code:
   05 (China) gives 05050906. */
INSERT INTO "Brand" (brand_id, brand_code, brand_name, description, is_active) VALUES
    (1, '01', 'Samsung',   'Fits Samsung handsets',   TRUE),
    (2, '02', 'Motorola',  'Fits Motorola handsets',  TRUE),
    (3, '03', 'L.G',       'Fits LG handsets',        TRUE),
    (4, '04', 'iPhone',    'Fits Apple handsets',     TRUE),
    (5, '05', 'China',     'Fits China-set handsets', TRUE),
    (6, '06', 'Universal', 'Works with any handset',  TRUE);

INSERT INTO "Product" (product_id, sku, product_name, description, category_id, brand_id, packing, min_qty, max_qty, opening_cost, cost_price, sale_price, tax_rate_percent, hide_stock, is_active, image_url, created_at) VALUES
    (1,  '05050781', 'VIZO Titan T9 Wireless Earbuds - Black',      'TWS earbuds with ENC, 30-hour playtime',            2,  6, 10, 200, 2400,  545.00,  580.00,  980.00, 18.00, FALSE, TRUE, NULL, '2025-09-15'),
    (2,  '05050776', 'VIZO Titan T9 Wireless Earbuds - White',      'TWS earbuds with ENC, 30-hour playtime',            2,  6, 10, 200, 2400,  545.00,  580.00,  980.00, 18.00, FALSE, TRUE, NULL, '2025-09-15'),
    (3,  '05050777', 'VIZO Titan T15 Pro ANC Earbuds',              'Active noise cancellation, hi-res audio',           2,  6, 6,  100, 1200, 1391.00, 1480.00, 2480.00, 18.00, FALSE, TRUE, NULL, '2025-09-15'),
    (4,  '05050779', 'VIZO Titan AirPro Earbuds',                   'Half-in-ear design, premium sound',                 2,  4, 10, 150, 1800,  677.00,  720.00, 1280.00, 18.00, FALSE, TRUE, NULL, '2025-09-15'),
    (5,  '05050863', 'VIZO Kung Fu X2 Neckband',                    'Neckband handfree, magnetic buds',                  3,  6, 12, 150, 1800,  583.00,  620.00, 1050.00, 18.00, FALSE, TRUE, NULL, '2025-09-15'),
    (6,  '05050871', 'VIZO Blaze Pro V65 Handfree',                 'Type-C wired handfree, premium build',              3,  5, 20, 300, 3600,  136.00,  145.00,  285.00, 18.00, FALSE, TRUE, NULL, '2025-09-15'),
    (7,  '05050841', 'VIZO PowerX 10000mAh Power Bank - Black',     '10000mAh, dual output, fast charging',              7,  6, 8,  100, 1200, 1203.00, 1280.00, 2180.00, 18.00, FALSE, TRUE, NULL, '2025-09-15'),
    (8,  '05050842', 'VIZO PowerX 20000mAh Power Bank - Black',     '20000mAh, PD 22.5W, three outputs',                 7,  6, 6,  80,   960, 2143.00, 2280.00, 3680.00, 18.00, FALSE, TRUE, NULL, '2025-09-15'),
    (9,  '05050843', 'VIZO PowerX MagSafe 5000mAh Wireless',        'Magnetic wireless charging, 5000mAh',               7,  4, 6,  50,   600, 1861.00, 1980.00, 3280.00, 18.00, FALSE, TRUE, NULL, '2025-09-15'),
    (10, '05050893', 'VIZO Hyper PD VPD45W Charger',                '45W PD wall charger',                               6,  6, 24, 500, 6000,  169.00,  180.00,  340.00, 18.00, FALSE, TRUE, NULL, '2025-09-15'),
    (11, '05050745', 'VIZO 29DI Itel Battery',                      'Replacement battery, Itel handsets',                8,  5, 20, 120, 1440,  329.00,  350.00,  620.00, 18.00, FALSE, TRUE, NULL, '2025-09-15'),
    (12, '05050785', 'VIZO G530 Samsung Battery',                   'Replacement battery, Samsung G530',                 8,  1, 20, 200, 2400,  244.00,  260.00,  480.00, 18.00, FALSE, TRUE, NULL, '2025-09-15'),
    (13, '05050774', 'VIZO I10 Battery',                            'Replacement battery - stock discrepancy, needs counting', 8, 4, 20, 100, 1200, 255.00, 271.00, 520.00, 18.00, FALSE, TRUE, NULL, '2025-09-15'),
    (14, '05050751', 'VIZO I251 Battery',                           'Replacement battery, iPhone 5-series',              8,  4, 20, 100, 1200,  209.00,  222.00,  430.00, 18.00, FALSE, TRUE, NULL, '2025-09-15'),
    (15, '05050810', 'VIZO VSP Bluetooth Speaker Mini - Red',       'Portable Bluetooth speaker, 5W',                    4,  6, 12, 150, 1800,  357.00,  380.00,  680.00, 18.00, FALSE, TRUE, NULL, '2025-09-15'),
    (16, '05050811', 'VIZO VSP Bluetooth Speaker Mini - Blue',      'Portable Bluetooth speaker, 5W',                    4,  6, 12, 150, 1800,  357.00,  380.00,  680.00, 18.00, FALSE, TRUE, NULL, '2025-09-15'),
    (17, '05050812', 'VIZO VSP Pro X1 Soundbar 30W',                '30W soundbar with subwoofer',                       4,  6, 4,  40,   480, 2331.00, 2480.00, 4280.00, 18.00, FALSE, TRUE, NULL, '2025-09-15'),
    (18, '05050813', 'VIZO VSP Cube Y Yellow Mini Speaker',         'Compact cube design, 8W output',                    4,  6, 12, 80,   960,  451.00,  480.00,  880.00, 18.00, FALSE, TRUE, NULL, '2025-09-15'),
    (19, '05050885', 'VIZO Linko VC101 Type-C Cable',               'Braided Type-C cable, 5A',                         10,  5, 24, 400, 4800,   89.00,   95.00,  195.00, 18.00, FALSE, TRUE, NULL, '2025-09-15'),
    (20, '05050886', 'VIZO Maxo VC202 Micro V8 Cable',              'Standard Micro-USB cable',                         12,  5, 24, 500, 6000,   61.00,   65.00,  140.00, 18.00, FALSE, TRUE, NULL, '2025-09-15'),
    (21, '05050887', 'VIZO Lightning Cable 1.5m (MFi)',             'MFi-certified Lightning cable',                    11,  4, 20, 200, 2400,  268.00,  285.00,  580.00, 18.00, FALSE, TRUE, NULL, '2025-09-15'),
    (22, '05050888', 'VIZO Type-C Data Cable 3.0m',                 'Long Type-C cable, braided',                       10,  5, 24, 300, 3600,  136.00,  145.00,  295.00, 18.00, FALSE, TRUE, NULL, '2025-09-15'),
    (23, '05050889', 'VIZO OTG Adapter Type-C',                     'OTG adapter, Type-C to USB-A',                     10,  6, 20, 100, 1200,  136.00,  145.00,  295.00, 18.00, FALSE, TRUE, NULL, '2025-09-15'),
    (24, '05050901', 'VIZO VOLT 65W GaN Type-C Charger (PD)',       'Universal GaN charger, 65W PD',                     6,  6, 10, 100, 1200, 1391.00, 1480.00, 2480.00, 18.00, FALSE, TRUE, NULL, '2025-09-15'),
    (25, '05050902', 'VIZO VOLT 30W Dual Port Charger',             'USB-A + Type-C, fast charging',                     6,  6, 12, 200, 2400,  545.00,  580.00,  980.00, 18.00, FALSE, TRUE, NULL, '2025-09-15'),
    (26, '05050904', 'VIZO VOLT Car Charger 45W',                   'Dual-port car charger, PD 45W',                     6,  6, 12, 100, 1200,  451.00,  480.00,  840.00, 18.00, FALSE, TRUE, NULL, '2025-09-15'),
    (27, '05050906', 'VIZO Clamp V6000 Charger 2026',               'Wall charger with clamp holder',                    6,  5, 20, 250, 3000,  216.00,  230.00,  420.00, 18.00, FALSE, TRUE, NULL, '2025-09-15'),
    (28, '05050895', 'VIZO Glasspods VR7070 Bluetooth',             'Bluetooth audio glasses',                          13,  5, 6,  60,   720, 1692.00, 1800.00, 2900.00, 18.00, FALSE, TRUE, NULL, '2025-09-15'),
    (29, '05050896', 'VIZO Bluetooth FM Transmitter for Car',       'Car FM transmitter, hands-free',                   13,  6, 10, 40,   480,  357.00,  380.00,  720.00, 18.00, FALSE, TRUE, NULL, '2025-09-15'),
    (30, '05050920', 'VIZO LED Bulb 9W (Cool White)',               'Energy-saving LED bulb 9W',                        14,  6, 20, 100, 1200,  136.00,  145.00,  285.00, 18.00, FALSE, TRUE, NULL, '2025-09-15'),
    (31, '05050921', 'VIZO LED Bulb 12W (Cool White)',              'Energy-saving LED bulb 12W',                       14,  6, 20, 80,   960,  183.00,  195.00,  380.00, 18.00, FALSE, TRUE, NULL, '2025-09-15'),
    (32, '05050903', 'VIZO Keychain Gifting',                       'Promotional giveaway - needs stock correction',      1,  6, 50, 0,     0,    0.00,    0.00,    0.00, 18.00, FALSE, TRUE, NULL, '2025-09-15'),
    (33, '05050930', 'VIZO Titan Handfree Classic - Discontinued',  'Discontinued model',                                3,  5, 20, 0,     0,   61.00,   65.00,  140.00, 18.00, FALSE, TRUE, NULL, '2025-09-15');

INSERT INTO "ProductBarcode" (barcode_id, product_id, barcode) VALUES
    (1, 1, '600000000001'),   (2, 2, '600000000002'),   (3, 3, '600000000003'),
    (4, 4, '600000000004'),   (5, 5, '600000000005'),   (6, 6, '600000000006'),
    (7, 7, '600000000007'),   (8, 8, '600000000008'),   (9, 9, '600000000009'),
    (10, 10, '600000000010'), (11, 11, '600000000011'), (12, 12, '600000000012'),
    (13, 13, '600000000013'), (14, 14, '600000000014'), (15, 15, '600000000015'),
    (16, 16, '600000000016'), (17, 17, '600000000017'), (18, 18, '600000000018'),
    (19, 19, '600000000019'), (20, 20, '600000000020'), (21, 21, '600000000021'),
    (22, 22, '600000000022'), (23, 23, '600000000023'), (24, 24, '600000000024'),
    (25, 25, '600000000025'), (26, 26, '600000000026'), (27, 27, '600000000027'),
    (28, 28, '600000000028'), (29, 29, '600000000029'), (30, 30, '600000000030'),
    (31, 31, '600000000031'), (32, 32, '600000000032'), (33, 33, '600000000033'),
    /* Policy allows several barcodes per item - the supplier carton for the
       T9 Black carries its own. This is why barcodes are not a column. */
    (34, 1, '600000000101'),
    (35, 19, '600000000119');

/* Stock in hand. The frontend split one total across the three sellable
   shelves at 50 / 30 / 20 percent; that split is done and the result stored,
   which is what a stock table is for.
   Products 13 and 32 carry the negative balances the live catalogue has --
   known discrepancies that must stay visible until somebody counts. */
INSERT INTO "StockBalance" (product_id, location_id, quantity) VALUES
    (1,1,620),(1,2,372),(1,3,248),
    (2,1,490),(2,2,294),(2,3,196),
    (3,1,170),(3,2,102),(3,3,68),
    (4,1,270),(4,2,162),(4,3,108),
    (5,1,430),(5,2,258),(5,3,172),
    (6,1,930),(6,2,558),(6,3,372),
    (7,1,340),(7,2,204),(7,3,136),
    (8,1,170),(8,2,102),(8,3,68),
    (9,1,60),(9,2,36),(9,3,24),
    (10,1,1620),(10,2,972),(10,3,648),
    (11,1,273),(11,2,164),(11,3,109),
    (12,1,612),(12,2,367),(12,3,244),
    (13,1,-1),(13,2,0),(13,3,0),
    (14,1,184),(14,2,110),(14,3,73),
    (15,1,420),(15,2,252),(15,3,168),
    (16,1,360),(16,2,216),(16,3,144),
    (17,1,74),(17,2,44),(17,3,29),
    (18,1,12),(18,2,7),(18,3,4),
    (19,1,920),(19,2,552),(19,3,368),
    (20,1,1240),(20,2,744),(20,3,496),
    (21,1,310),(21,2,186),(21,3,124),
    (22,1,490),(22,2,294),(22,3,196),
    (23,1,0),(23,2,0),(23,3,0),
    (24,1,205),(24,2,123),(24,3,82),
    (25,1,390),(25,2,234),(25,3,156),
    (26,1,170),(26,2,102),(26,3,68),
    (27,1,789),(27,2,473),(27,3,315),
    (28,1,93),(28,2,55),(28,3,37),
    (29,1,42),(29,2,25),(29,3,17),
    (30,1,0),(30,2,0),(30,3,0),
    (31,1,6),(31,2,3),(31,3,2),
    (32,1,-975),(32,2,0),(32,3,0),
    (33,1,90),(33,2,54),(33,3,36),
    /* LOC-04 Claim Stock: the pieces sitting on the claim shelf right now.
       Never counted as sellable. */
    (11,4,12),(1,4,3),(24,4,2),
    /* LOC-05 In Transit: TRF-26-0014 left the warehouse and has not landed. */
    (19,5,120),(20,5,120);


/* ===========================================================================
   SECTION 6 -- ACCOUNTING
   =========================================================================== */

INSERT INTO "AccountGroup" (group_id, group_name, on_balance_sheet) VALUES
    (1, 'Assets',      TRUE),
    (2, 'Capital',     TRUE),
    (3, 'Expenses',    FALSE),
    (4, 'Liabilities', TRUE),
    (5, 'Revenue',     FALSE);

INSERT INTO "AccountType" (account_type_id, type_name, group_id, code_prefix, code_length, is_debit_normal, is_system) VALUES
    (1,  'Assets',               1, 'A',   7, TRUE,  TRUE),
    (2,  'Current Assets',       1, 'CA',  7, TRUE,  TRUE),
    (3,  'Cash & Bank',          1, 'CB',  7, TRUE,  TRUE),
    (4,  'Inventory',            1, 'INV', 7, TRUE,  TRUE),
    (5,  'Acc Receivables',      1, 'ACR', 5, TRUE,  TRUE),
    (6,  'Fixed Assets',         1, 'FA',  7, TRUE,  TRUE),
    (7,  'Capital',              2, 'C',   7, FALSE, TRUE),
    (8,  'Owners Profit & Loss', 2, 'OPL', 7, FALSE, TRUE),
    (9,  'Expenses',             3, 'E',   7, TRUE,  TRUE),
    (10, 'Liabilities',          4, 'L',   7, FALSE, TRUE),
    (11, 'Current Liabilities',  4, 'CL',  7, FALSE, TRUE),
    (12, 'Acc Payables',         4, 'ACP', 5, FALSE, TRUE),
    (13, 'Fixed Liabilities',    4, 'FL',  7, FALSE, TRUE),
    (14, 'Revenue',              5, 'R',   7, FALSE, TRUE);

/* The chart of accounts. opening_balance is the position the accounts were
   brought onto the system with; everything after it comes from
   "JournalEntryLine" and nowhere else. There is no stored running balance. */
INSERT INTO "Account" (account_id, account_code, account_name, parent_account_id, account_type_id, is_group, opening_balance, currency_code, is_active) VALUES
    (1,  '1000', 'Assets',                    NULL, 1,  TRUE,         0.00, 'PKR', TRUE),
    (2,  '1100', 'Current Assets',            1,    2,  TRUE,         0.00, 'PKR', TRUE),
    (3,  '1101', 'Cash on Hand',              2,    3,  FALSE,   840000.00, 'PKR', TRUE),
    (4,  '1102', 'Cash - Shop 2',             2,    3,  FALSE,   420000.00, 'PKR', TRUE),
    (5,  '1110', 'HBL Bank Account',          2,    3,  FALSE,  1840000.00, 'PKR', TRUE),
    (6,  '1111', 'Meezan Bank Account',       2,    3,  FALSE,  1240000.00, 'PKR', TRUE),
    (7,  '1112', 'UBL Bank Account',          2,    3,  FALSE,   640000.00, 'PKR', TRUE),
    (8,  '1120', 'Easypaisa Wallet',          2,    3,  FALSE,   145000.00, 'PKR', TRUE),
    (9,  '1121', 'JazzCash Wallet',           2,    3,  FALSE,    75000.00, 'PKR', TRUE),
    (10, '1130', 'Accounts Receivable',       2,    5,  FALSE, 18400000.00, 'PKR', TRUE),
    (11, '1131', 'COD with Couriers',         2,    5,  FALSE,   452900.00, 'PKR', TRUE),
    (12, '1140', 'Inventory',                 2,    4,  FALSE, 16617597.00, 'PKR', TRUE),
    (13, '1150', 'Prepaid Expenses',          2,    2,  FALSE,   124000.00, 'PKR', TRUE),
    (14, '1200', 'Fixed Assets',              1,    6,  TRUE,         0.00, 'PKR', TRUE),
    (15, '1201', 'Office Equipment',          14,   6,  FALSE,  1850000.00, 'PKR', TRUE),
    (16, '1202', 'Vehicles',                  14,   6,  FALSE,  4200000.00, 'PKR', TRUE),
    (17, '2000', 'Liabilities',               NULL, 10, TRUE,         0.00, 'PKR', TRUE),
    (18, '2100', 'Current Liabilities',       17,   11, TRUE,         0.00, 'PKR', TRUE),
    (19, '2101', 'Accounts Payable',          18,   12, FALSE,  9620000.00, 'PKR', TRUE),
    (20, '2102', 'Stock Received Not Billed', 18,   11, FALSE,   482000.00, 'PKR', TRUE),
    (21, '2110', 'Output Sales Tax Payable',  18,   11, FALSE,  1245000.00, 'PKR', TRUE),
    (22, '2111', 'Input Sales Tax',           18,   11, FALSE,   845000.00, 'PKR', TRUE),
    (23, '2120', 'WHT Payable',               18,   11, FALSE,   124000.00, 'PKR', TRUE),
    (24, '2130', 'Zakat Payable',             18,   11, FALSE,        0.00, 'PKR', TRUE),
    (25, '3000', 'Capital',                   NULL, 7,  TRUE,         0.00, 'PKR', TRUE),
    (26, '3001', 'Owner Capital',             25,   7,  FALSE, 12000000.00, 'PKR', TRUE),
    (27, '3002', 'Retained Earnings',         25,   8,  FALSE,  8240000.00, 'PKR', TRUE),
    (28, '4000', 'Revenue',                   NULL, 14, TRUE,         0.00, 'PKR', TRUE),
    (29, '4001', 'Sale',                      28,   14, FALSE, 21800000.00, 'PKR', TRUE),
    (30, '4002', 'Sales Returns',             28,   14, FALSE,  -180000.00, 'PKR', TRUE),
    (31, '4010', 'Other Income',              28,   14, FALSE,    24000.00, 'PKR', TRUE),
    (32, '5000', 'Expenses',                  NULL, 9,  TRUE,         0.00, 'PKR', TRUE),
    (33, '5001', 'Cost of Goods Sold',        32,   9,  FALSE, 14500000.00, 'PKR', TRUE),
    (34, '5101', 'Salary Expense',            32,   9,  FALSE, 10132864.00, 'PKR', TRUE),
    (35, '5102', 'Rent Expense',              32,   9,  FALSE,  3838730.00, 'PKR', TRUE),
    (36, '5103', 'Shop Expense',              32,   9,  FALSE,  7020858.00, 'PKR', TRUE),
    (37, '5104', 'Shop 2 Expense',            32,   9,  FALSE,   768806.00, 'PKR', TRUE),
    (38, '5105', 'Dealer Commission',         32,   9,  FALSE,  2988663.00, 'PKR', TRUE),
    (39, '5106', 'Warranty & Claims',         32,   9,  FALSE,  1313558.00, 'PKR', TRUE),
    (40, '5107', 'Marketing',                 32,   9,  FALSE,  1665420.00, 'PKR', TRUE),
    (41, '5108', 'Travelling',                32,   9,  FALSE,  1317727.00, 'PKR', TRUE),
    (42, '5109', 'Discount Loss',             32,   9,  FALSE,   263390.00, 'PKR', TRUE),
    (43, '5110', 'Online Shop',               32,   9,  FALSE,   188752.00, 'PKR', TRUE),
    (44, '5111', 'Food Expense',              32,   9,  FALSE,   582898.00, 'PKR', TRUE),
    (45, '5112', 'Vehicle & Fuel',            32,   9,  FALSE,   320000.00, 'PKR', TRUE),
    (46, '5113', 'Repairing Expense',         32,   9,  FALSE,   172570.00, 'PKR', TRUE),
    (47, '5114', 'Delivery & Courier',        32,   9,  FALSE,   284600.00, 'PKR', TRUE),
    (48, '5115', 'Utilities',                 32,   9,  FALSE,   145000.00, 'PKR', TRUE),
    (49, '5116', 'Bank Charges',              32,   9,  FALSE,    18000.00, 'PKR', TRUE),
    (50, '5117', 'Personal',                  32,   9,  FALSE, 13090376.00, 'PKR', TRUE),
    /* Stock that has left one shelf and not yet landed on the next. Without
       it an in-transit transfer would vanish from the balance sheet. */
    (51, '1141', 'Stock in Transit',          2,    4,  FALSE,        0.00, 'PKR', TRUE),
    (52, '5118', 'Depreciation',              32,   9,  FALSE,        0.00, 'PKR', TRUE);

/* Eighteen entries. Types SALE / PURCHASE / TRANSFER are posted by the
   service account when a document is confirmed; RECEIPT / PAYMENT / EXPENSE
   follow a voucher; JOURNAL is somebody keying it by hand on the Manual
   Entries screen. All of them land in the same two tables. */
INSERT INTO "JournalEntry" (entry_id, entry_no, entry_date, entry_type_id, period_id, location_id, reference_no, narration, status_id, created_by_user_id, posted_by_user_id, created_at) VALUES
    (1,  'JE-26-1042', '2026-04-30', 1, 4, 2, 'INV-26-0142', 'Sales invoice posting',                        2, 11, 11, '2026-04-30'),
    (2,  'JE-26-1041', '2026-04-30', 2, 4, 2, 'VCH-26-0089', 'Bank receipt - Hafeez Center Shop #28',        2, 2,  2,  '2026-04-30'),
    (3,  'JE-26-1040', '2026-04-29', 3, 4, 2, 'GRN-26-0089', 'GRN posting - Shenzhen Electronics Hub',       2, 11, 11, '2026-04-29'),
    (4,  'JE-26-1039', '2026-04-29', 4, 4, 2, 'VCH-26-0088', 'Bank payment - Pak Accessories Imports',       2, 2,  2,  '2026-04-29'),
    (5,  'JE-26-1038', '2026-04-28', 5, 4, 2, 'EXP-26-0024', 'Office rent - April 2026',                     2, 2,  2,  '2026-04-28'),
    (6,  'JE-26-1037', '2026-04-28', 6, 4, 2, NULL,          'Adjustment - bank reconciliation',             2, 2,  2,  '2026-04-28'),
    (7,  'JE-26-1036', '2026-04-27', 7, 4, 2, 'TRF-26-0012', 'Stock transfer Warehouse to Shop 2 (in transit)', 2, 11, 11, '2026-04-27'),
    (8,  'JE-26-1043', '2026-05-01', 6, 5, 2, NULL,          'Depreciation - vehicles April',                1, 2,  NULL, '2026-05-01'),
    (9,  'JE-26-1035', '2026-04-29', 2, 4, 2, 'VCH-26-0087', 'Cash receipt - Saddar Mobile Plaza',           2, 2,  2,  '2026-04-29'),
    (10, 'JE-26-1034', '2026-04-28', 2, 4, 2, 'VCH-26-0086', 'JazzCash receipt - Quetta Cellular',           2, 2,  2,  '2026-04-28'),
    (11, 'JE-26-1033', '2026-04-29', 2, 4, 3, 'VCH-26-0034', 'Easypaisa receipt - Mobile Mart Multan',       2, 9,  9,  '2026-04-29'),
    (12, 'JE-26-1045', '2026-08-12', 2, 8, 2, 'VCH-26-0091', 'Bank receipt - Mobilink Connect Lahore',       2, 2,  2,  '2026-08-12'),
    (13, 'JE-26-1046', '2026-08-11', 2, 8, 3, 'VCH-26-0092', 'Cash receipt - Faisal Mobile Mart',            2, 2,  2,  '2026-08-11'),
    (14, 'JE-26-1047', '2026-08-09', 2, 8, 2, 'VCH-26-0093', 'Bank receipt - Star Communications',           2, 2,  2,  '2026-08-09'),
    (15, 'JE-26-1048', '2026-04-27', 5, 4, 2, 'EXP-26-0023', 'Electricity - K-Electric April',               2, 2,  2,  '2026-04-27'),
    (16, 'JE-26-1049', '2026-04-26', 5, 4, 2, 'EXP-26-0022', 'Internet - Stormfiber April',                  2, 2,  2,  '2026-04-26'),
    (17, 'JE-26-1050', '2026-04-24', 5, 4, 2, 'EXP-26-0021', 'Vehicle fuel - Shell Pakistan',                2, 2,  2,  '2026-04-24'),
    (18, 'JE-26-1051', '2026-04-22', 5, 4, 3, 'EXP-26-0014', 'Marketing - Daraz sponsored ads',              2, 2,  2,  '2026-04-22'),
    (19, 'JE-26-1052', '2026-08-09', 2, 8, 1, 'VCH-26-0094', 'Bank receipt - Blue Area Distributors',        2, 2,  2,  '2026-08-09'),
    (20, 'JE-26-1053', '2026-08-05', 4, 8, 1, 'VCH-26-0095', 'Bank payment - Shenzhen Electronics Hub',      2, 2,  2,  '2026-08-05');

/* THE SINGLE SOURCE OF TRUTH.
   Ledger        = SELECT ... WHERE account_id = ? ORDER BY entry_date
   Trial balance = SUM(debit_amount), SUM(credit_amount) GROUP BY account_id
   Statement     = the same, filtered on party_user_id
   Every one of these entries balances -- see the check query at the foot of
   this file. */
INSERT INTO "JournalEntryLine" (line_id, entry_id, line_no, account_id, party_user_id, description, debit_amount, credit_amount) VALUES
    /* JE-26-1042's own five lines are emitted with the other sale postings
       further down, so they agree with the invoice to the paisa. */
    /* JE-26-1041  money in, receivable down */
    (6,  2,  1, 6,  NULL, 'Meezan Bank - TXN-7748392',           100000.00,      0.00),
    (7,  2,  2, 10, 12,   'Against INV-128',                           0.00, 100000.00),
    /* JE-26-1040  stock received, not yet billed */
    (8,  3,  1, 12, NULL, 'GRN-26-0089 - 235 pcs accepted',      482000.00,      0.00),
    (9,  3,  2, 20, 28,   'Awaiting supplier invoice',                 0.00, 482000.00),
    /* JE-26-1039  paying a supplier */
    (10, 4,  1, 19, 30,   'Pak Accessories Imports - PI-26-0040',320000.00,      0.00),
    (11, 4,  2, 5,  NULL, 'HBL Bank - CHQ-001245',                     0.00, 320000.00),
    /* JE-26-1038  April rent */
    (12, 5,  1, 35, NULL, 'Kohinoor Properties - April 2026',    120000.00,      0.00),
    (13, 5,  2, 5,  NULL, 'HBL Bank',                                  0.00, 120000.00),
    /* JE-26-1037  hand-keyed correction found while reconciling */
    (14, 6,  1, 49, NULL, 'Bank charges not on the system',        4200.00,      0.00),
    (15, 6,  2, 5,  NULL, 'HBL Bank',                                  0.00,   4200.00),
    /* JE-26-1036  stock left the warehouse and has not landed */
    (16, 7,  1, 51, NULL, 'TRF-26-0012 in transit',              245000.00,      0.00),
    (17, 7,  2, 12, NULL, 'Out of Warehouse',                          0.00, 245000.00),
    /* JE-26-1043  still a draft: nothing here has hit any report yet */
    (18, 8,  1, 52, NULL, 'Vehicles - April',                     35000.00,      0.00),
    (19, 8,  2, 16, NULL, 'Accumulated depreciation',                  0.00,  35000.00),
    /* receipts */
    (20, 9,  1, 3,  NULL, 'Cash over the counter',                32750.00,      0.00),
    (21, 9,  2, 10, 14,   'Saddar Mobile Plaza',                       0.00,  32750.00),
    (22, 10, 1, 9,  NULL, 'JazzCash - JC-998877665',              12400.00,      0.00),
    (23, 10, 2, 10, 18,   'Quetta Cellular',                           0.00,  12400.00),
    (24, 11, 1, 8,  NULL, 'Easypaisa - EP-554433221',             24600.00,      0.00),
    (25, 11, 2, 10, 20,   'Mobile Mart Multan',                        0.00,  24600.00),
    (26, 12, 1, 5,  NULL, 'HBL Bank - TXN-77483921',              40000.00,      0.00),
    (27, 12, 2, 10, 19,   'Mobilink Connect Lahore',                   0.00,  40000.00),
    (28, 13, 1, 4,  NULL, 'Cash - Shop 2',                        18400.00,      0.00),
    (29, 13, 2, 10, 17,   'Faisal Mobile Mart',                        0.00,  18400.00),
    (30, 14, 1, 6,  NULL, 'Meezan Bank - TXN-77410882',          485000.00,      0.00),
    (31, 14, 2, 10, 21,   'Star Communications',                       0.00, 485000.00),
    /* expenses */
    (32, 15, 1, 48, NULL, 'K-Electric April',                     45000.00,      0.00),
    (33, 15, 2, 5,  NULL, 'HBL Bank',                                  0.00,  45000.00),
    (34, 16, 1, 48, NULL, 'Stormfiber April',                     18000.00,      0.00),
    (35, 16, 2, 5,  NULL, 'HBL Bank',                                  0.00,  18000.00),
    (36, 17, 1, 45, NULL, 'Shell Pakistan',                       28500.00,      0.00),
    (37, 17, 2, 3,  NULL, 'Cash on Hand',                              0.00,  28500.00),
    (38, 18, 1, 40, NULL, 'Daraz sponsored ads',                  84000.00,      0.00),
    (39, 18, 2, 5,  NULL, 'HBL Bank',                                  0.00,  84000.00),
    /* part payment in against the Islamabad consignment */
    (40, 19, 1, 5,  NULL, 'HBL Bank - TXN-77410990',              87200.00,      0.00),
    (41, 19, 2, 10, 15,   'Blue Area Distributors',                    0.00,  87200.00),
    /* part payment out against PI-26-0041 */
    (42, 20, 1, 19, 28,   'Shenzhen Electronics Hub - PI-26-0041',360000.00,      0.00),
    (43, 20, 2, 5,  NULL, 'HBL Bank - TXN-88120445',                   0.00, 360000.00);

/* Money in and out. Each posted voucher owns the journal entry it produced,
   so the cash book and the ledger cannot disagree. A journal voucher touches
   no drawer, which is why cash_bank_account_id is NULL on that one row. */
INSERT INTO "Voucher" (voucher_id, voucher_no, voucher_type_id, voucher_date, location_id, party_user_id, cash_bank_account_id, amount, method_id, payment_provider, reference_no, wallet_txn_id, narration, status_id, entry_id, created_by_user_id) VALUES
    (1, 'VCH-26-0089', 3, '2026-04-30', 2, 12,   6,    100000.00, 2, 'Meezan Bank', 'TXN-7748392',  NULL,            'Payment against INV-128',          2, 2,  2),
    (2, 'VCH-26-0088', 4, '2026-04-29', 2, 30,   5,    320000.00, 2, 'HBL Bank',    'CHQ-001245',   NULL,            'Payment for PI-26-0040',           2, 4,  2),
    (3, 'VCH-26-0087', 1, '2026-04-29', 2, 14,   3,     32750.00, 1, NULL,          NULL,           NULL,            'Cash receipt over counter',        2, 9,  2),
    (4, 'VCH-26-0086', 5, '2026-04-28', 2, 18,   9,     12400.00, 3, 'JazzCash',    'JC-998877665', 'JC-998877665',  'JazzCash payment',                 2, 10, 2),
    (5, 'VCH-26-0034', 5, '2026-04-29', 3, 20,   8,     24600.00, 4, 'Easypaisa',   'EP-554433221', 'EP-554433221',  'Easypaisa payment',                2, 11, 9),
    (6, 'VCH-26-0090', 7, '2026-05-01', 2, NULL, NULL,  35000.00, 1, NULL,          NULL,           NULL,            'Depreciation entry - April vehicles', 1, 8, 2),
    (7, 'VCH-26-0091', 3, '2026-08-12', 2, 19,   5,     40000.00, 2, 'HBL Bank',    'TXN-77483921', NULL,            'Field collection COL-26-0086',     2, 12, 2),
    (8, 'VCH-26-0092', 1, '2026-08-11', 3, 17,   4,     18400.00, 1, NULL,          NULL,           NULL,            'Field collection COL-26-0085',     2, 13, 2),
    (9, 'VCH-26-0093', 3, '2026-08-09', 2, 21,   6,    485000.00, 2, 'Meezan Bank', 'TXN-77410882', NULL,            'Field collection COL-26-0083',     2, 14, 2),
    (10,'VCH-26-0094', 3, '2026-08-09', 1, 15,   5,     87200.00, 2, 'HBL Bank',    'TXN-77410990', NULL,            'Part payment against INV-26-0034', 2, 19, 2),
    (11,'VCH-26-0095', 4, '2026-08-05', 1, 28,   5,    360000.00, 2, 'HBL Bank',    'TXN-88120445', NULL,            'Part payment for PI-26-0041',      2, 20, 2);

INSERT INTO "Expense" (expense_id, expense_no, expense_date, location_id, category_name, expense_account_id, paid_from_account_id, amount, vendor_name, method_id, description, status_id, entry_id, created_by_user_id) VALUES
    (1, 'EXP-26-0024', '2026-04-28', 2, 'Office Rent',    35, 5, 120000.00, 'Kohinoor Properties', 2, 'Office rent for April 2026',        2, 5,  2),
    (2, 'EXP-26-0023', '2026-04-27', 2, 'Utilities (KE)', 48, 5,  45000.00, 'K-Electric',          2, 'Electricity bill April',            2, 15, 2),
    (3, 'EXP-26-0022', '2026-04-26', 2, 'Internet',       48, 5,  18000.00, 'Stormfiber',          2, 'Fibre internet April',              2, 16, 2),
    (4, 'EXP-26-0021', '2026-04-24', 2, 'Vehicle Fuel',   45, 3,  28500.00, 'Shell Pakistan',      1, 'Fuel for delivery vehicles',        2, 17, 2),
    (5, 'EXP-26-0014', '2026-04-22', 3, 'Marketing',      40, 5,  84000.00, 'Daraz Sponsored Ads', 2, 'Sponsored listing campaign',        2, 18, 2),
    (6, 'EXP-26-0025', '2026-04-30', 2, 'SMS Service',    43, 5,  12000.00, 'Jazz BizSMS',         2, 'Bulk SMS bundle - not posted yet',  1, NULL, 2);

INSERT INTO "BankReconciliation" (reconciliation_id, account_id, statement_date, opening_balance, closing_balance, status_id, prepared_by_user_id, finalized_on) VALUES
    (1, 5, '2026-04-30', 1700000.00, 1840000.00, 1, 2, NULL),
    (2, 6, '2026-03-31', 1100000.00, 1240000.00, 6, 2, '2026-04-05'),
    (3, 7, '2026-04-30',  600000.00,  640000.00, 6, 2, '2026-05-04'),
    (4, 5, '2026-03-31', 1580000.00, 1700000.00, 6, 2, '2026-04-05'),
    (5, 6, '2026-04-30', 1240000.00, 1425000.00, 1, 2, NULL);

/* Lines straight off the bank's statement. Only the outward transfer has been
   tied to a journal line so far; the rest are what the screen shows sitting
   unmatched. */
INSERT INTO "BankStatementLine" (statement_line_id, reconciliation_id, line_date, description, amount, matched_line_id) VALUES
    (1, 1, '2026-04-30', 'INWARD TT - HAFEEZ CTR',       100000.00, NULL),
    (2, 1, '2026-04-29', 'OUTWARD TT - PAK ACCESSORIES', -320000.00, 11),
    (3, 1, '2026-04-29', 'INWARD - STAR COMM',            240000.00, NULL),
    (4, 1, '2026-04-28', 'BANK CHARGES - APR',             -1850.00, NULL),
    (5, 1, '2026-04-27', 'INWARD TT - MOBILINK CONNECT',  180000.00, NULL),
    (6, 1, '2026-04-26', 'ATM WITHDRAWAL',                -25000.00, NULL),
    (7, 3, '2026-04-28', 'INWARD TT - MARGALLA DIST',     240000.00, NULL),
    (8, 3, '2026-04-22', 'CHEQUE CLEARING - 0044120',     -25000.00, NULL),
    (9, 4, '2026-03-30', 'INWARD TT - STAR COMM',         185000.00, NULL),
    (10,5, '2026-04-29', 'INWARD TT - CELLULAR WORLD',    185000.00, NULL);

/* ===========================================================================
   SECTION 7 -- SALES
   ---------------------------------------------------------------------------
   Seventeen customer orders, exactly the ones on the Orders screen: two
   sitting on the owner's approval queue over their credit limit, one
   cancelled, one refused at the door and sent back.

   Every header adds up: subtotal + tax - discount = total, and subtotal is
   the sum of the lines to the paisa. The discount is where the negotiated
   rate cut lands, which is how a rate is agreed in this trade.
   =========================================================================== */

INSERT INTO "SalesOrder" (order_id, order_no, customer_user_id, location_id, sales_person_user_id, order_date, delivery_date, status_id, method_id, subtotal, discount_amount, tax_amount, total_amount, credit_hold_reason, notes, created_by_user_id, created_at) VALUES
    (1, 'ORD-26-0142', 12, 2, 7, '2026-08-13', '2026-08-13', 7, 5, 123510.00, 741.80, 22231.80, 145000.00, NULL, NULL, 7, '2026-08-13'),
    (2, 'ORD-26-0141', 14, 2, 7, '2026-08-13', NULL, 4, 5, 27865.00, 130.70, 5015.70, 32750.00, NULL, NULL, 7, '2026-08-13'),
    (3, 'ORD-26-0140', 16, 2, 7, '2026-08-12', NULL, 6, 5, 48120.00, 581.60, 8661.60, 56200.00, NULL, NULL, 7, '2026-08-12'),
    (4, 'ORD-26-0088', 17, 3, 7, '2026-08-11', '2026-08-11', 9, 1, 15760.00, 196.80, 2836.80, 18400.00, NULL, NULL, 7, '2026-08-11'),
    (5, 'ORD-26-0137', 18, 2, 7, '2026-08-06', '2026-08-10', 7, 3, 10625.00, 137.50, 1912.50, 12400.00, NULL, NULL, 7, '2026-08-06'),
    (6, 'ORD-26-0139', 19, 2, 7, '2026-08-12', '2026-08-15', 7, 2, 84580.00, 1304.40, 15224.40, 98500.00, NULL, NULL, 7, '2026-08-12'),
    (7, 'ORD-26-0138', 21, 2, 7, '2026-08-10', '2026-08-12', 9, 2, 412700.00, 1986.00, 74286.00, 485000.00, NULL, NULL, 7, '2026-08-10'),
    (8, 'ORD-26-0089', 13, 3, 8, '2026-08-11', '2026-08-14', 7, 5, 71705.00, 111.90, 12906.90, 84500.00, NULL, NULL, 8, '2026-08-11'),
    (9, 'ORD-26-0087', 20, 3, 8, '2026-08-09', '2026-08-12', 9, 4, 21210.00, 427.80, 3817.80, 24600.00, NULL, NULL, 8, '2026-08-09'),
    (10, 'ORD-26-0085', 20, 3, 8, '2026-08-05', '2026-08-08', 11, 5, 32700.00, 186.00, 5886.00, 38400.00, NULL, 'Customer refused - said rate was agreed lower', 8, '2026-08-05'),
    (11, 'ORD-26-0086', 25, 3, 7, '2026-08-13', NULL, 1, 5, 38475.00, 200.50, 6925.50, 45200.00, NULL, NULL, 7, '2026-08-13'),
    (12, 'ORD-26-0143', 13, 2, 8, '2026-08-14', NULL, 3, 5, 81610.00, 299.80, 14689.80, 96000.00, 'Already PKR 84,500 out; this pushes the balance PKR 30,500 over limit', NULL, 8, '2026-08-14'),
    (13, 'ORD-26-0144', 20, 2, 7, '2026-08-14', NULL, 3, 5, 44100.00, 38.00, 7938.00, 52000.00, 'Two invoices 45+ days overdue; limit crossed by PKR 18,000', NULL, 7, '2026-08-14'),
    (14, 'ORD-26-0034', 15, 1, 10, '2026-08-09', '2026-08-14', 7, 2, 184900.00, 182.00, 33282.00, 218000.00, NULL, NULL, 10, '2026-08-09'),
    (15, 'ORD-26-0033', 23, 1, 10, '2026-08-04', '2026-08-09', 9, 2, 271440.00, 299.20, 48859.20, 320000.00, NULL, NULL, 10, '2026-08-04'),
    (16, 'ORD-26-0136', 12, 2, 7, '2026-08-07', NULL, 10, 5, 74825.00, 293.50, 13468.50, 88000.00, NULL, 'Customer cancelled before packing', 7, '2026-08-07'),
    (17, 'ORD-26-0135', 16, 2, 7, '2026-08-03', '2026-08-03', 9, 2, 120440.00, 119.20, 21679.20, 142000.00, NULL, NULL, 7, '2026-08-03');

INSERT INTO "SalesOrderItem" (order_item_id, order_id, line_no, product_id, quantity, unit_price, discount_percent, tax_percent, line_total) VALUES
    (1, 1, 1, 1, 63, 980.00, 0.00, 18.00, 61740.00),
    (2, 1, 2, 24, 15, 2480.00, 0.00, 18.00, 37200.00),
    (3, 1, 3, 19, 126, 195.00, 0.00, 18.00, 24570.00),
    (4, 2, 1, 15, 20, 680.00, 0.00, 18.00, 13600.00),
    (5, 2, 2, 19, 43, 195.00, 0.00, 18.00, 8385.00),
    (6, 2, 3, 20, 42, 140.00, 0.00, 18.00, 5880.00),
    (7, 3, 1, 7, 11, 2180.00, 0.00, 18.00, 23980.00),
    (8, 3, 2, 25, 15, 980.00, 0.00, 18.00, 14700.00),
    (9, 3, 3, 22, 32, 295.00, 0.00, 18.00, 9440.00),
    (10, 4, 1, 11, 13, 620.00, 0.00, 18.00, 8060.00),
    (11, 4, 2, 27, 11, 420.00, 0.00, 18.00, 4620.00),
    (12, 4, 3, 20, 22, 140.00, 0.00, 18.00, 3080.00),
    (13, 5, 1, 6, 18, 285.00, 0.00, 18.00, 5130.00),
    (14, 5, 2, 20, 25, 140.00, 0.00, 18.00, 3500.00),
    (15, 5, 3, 30, 7, 285.00, 0.00, 18.00, 1995.00),
    (16, 6, 1, 3, 17, 2480.00, 0.00, 18.00, 42160.00),
    (17, 6, 2, 4, 20, 1280.00, 0.00, 18.00, 25600.00),
    (18, 6, 3, 21, 29, 580.00, 0.00, 18.00, 16820.00),
    (19, 7, 1, 8, 45, 3680.00, 0.00, 18.00, 165600.00),
    (20, 7, 2, 17, 29, 4280.00, 0.00, 18.00, 124120.00),
    (21, 7, 3, 24, 33, 2480.00, 0.00, 18.00, 81840.00),
    (22, 7, 4, 10, 121, 340.00, 0.00, 18.00, 41140.00),
    (23, 8, 1, 9, 11, 3280.00, 0.00, 18.00, 36080.00),
    (24, 8, 2, 5, 20, 1050.00, 0.00, 18.00, 21000.00),
    (25, 8, 3, 19, 75, 195.00, 0.00, 18.00, 14625.00),
    (26, 9, 1, 12, 22, 480.00, 0.00, 18.00, 10560.00),
    (27, 9, 2, 14, 15, 430.00, 0.00, 18.00, 6450.00),
    (28, 9, 3, 20, 30, 140.00, 0.00, 18.00, 4200.00),
    (29, 10, 1, 26, 19, 840.00, 0.00, 18.00, 15960.00),
    (30, 10, 2, 27, 23, 420.00, 0.00, 18.00, 9660.00),
    (31, 10, 3, 22, 24, 295.00, 0.00, 18.00, 7080.00),
    (32, 11, 1, 16, 28, 680.00, 0.00, 18.00, 19040.00),
    (33, 11, 2, 18, 13, 880.00, 0.00, 18.00, 11440.00),
    (34, 11, 3, 19, 41, 195.00, 0.00, 18.00, 7995.00),
    (35, 12, 1, 28, 14, 2900.00, 0.00, 18.00, 40600.00),
    (36, 12, 2, 29, 34, 720.00, 0.00, 18.00, 24480.00),
    (37, 12, 3, 6, 58, 285.00, 0.00, 18.00, 16530.00),
    (38, 13, 1, 7, 10, 2180.00, 0.00, 18.00, 21800.00),
    (39, 13, 2, 15, 19, 680.00, 0.00, 18.00, 12920.00),
    (40, 13, 3, 20, 67, 140.00, 0.00, 18.00, 9380.00),
    (41, 14, 1, 8, 20, 3680.00, 0.00, 18.00, 73600.00),
    (42, 14, 2, 3, 22, 2480.00, 0.00, 18.00, 54560.00),
    (43, 14, 3, 25, 38, 980.00, 0.00, 18.00, 37240.00),
    (44, 14, 4, 19, 100, 195.00, 0.00, 18.00, 19500.00),
    (45, 15, 1, 17, 25, 4280.00, 0.00, 18.00, 107000.00),
    (46, 15, 2, 9, 25, 3280.00, 0.00, 18.00, 82000.00),
    (47, 15, 3, 24, 22, 2480.00, 0.00, 18.00, 54560.00),
    (48, 15, 4, 10, 82, 340.00, 0.00, 18.00, 27880.00),
    (49, 16, 1, 1, 38, 980.00, 0.00, 18.00, 37240.00),
    (50, 16, 2, 2, 23, 980.00, 0.00, 18.00, 22540.00),
    (51, 16, 3, 22, 51, 295.00, 0.00, 18.00, 15045.00),
    (52, 17, 1, 4, 38, 1280.00, 0.00, 18.00, 48640.00),
    (53, 17, 2, 5, 34, 1050.00, 0.00, 18.00, 35700.00),
    (54, 17, 3, 21, 41, 580.00, 0.00, 18.00, 23780.00),
    (55, 17, 4, 20, 88, 140.00, 0.00, 18.00, 12320.00);

/* Posting the sales. Each invoice raises the receivable, books the revenue
   and the output tax, then moves the cost out of inventory into COGS -- the
   five lines the Manual Entries screen shows for JE-26-1042. Entry 1 was
   already declared above; its lines are emitted here with the rest so they
   agree with the invoice to the paisa. */
INSERT INTO "JournalEntry" (entry_id, entry_no, entry_date, entry_type_id, period_id, location_id, reference_no, narration, status_id, created_by_user_id, posted_by_user_id, created_at) VALUES
    (21, 'JE-26-1054', '2026-08-11', 1, 8, 3, 'INV-26-0088', 'Sales invoice posting - INV-26-0088', 2, 11, 11, '2026-08-11'),
    (22, 'JE-26-1055', '2026-08-06', 1, 8, 2, 'INV-26-0137', 'Sales invoice posting - INV-26-0137', 2, 11, 11, '2026-08-06'),
    (23, 'JE-26-1056', '2026-08-12', 1, 8, 2, 'INV-26-0139', 'Sales invoice posting - INV-26-0139', 2, 11, 11, '2026-08-12'),
    (24, 'JE-26-1057', '2026-08-10', 1, 8, 2, 'INV-26-0138', 'Sales invoice posting - INV-26-0138', 2, 11, 11, '2026-08-10'),
    (25, 'JE-26-1058', '2026-08-11', 1, 8, 3, 'INV-26-0089', 'Sales invoice posting - INV-26-0089', 2, 11, 11, '2026-08-11'),
    (26, 'JE-26-1059', '2026-08-09', 1, 8, 3, 'INV-26-0087', 'Sales invoice posting - INV-26-0087', 2, 11, 11, '2026-08-09'),
    (27, 'JE-26-1060', '2026-08-09', 1, 8, 1, 'INV-26-0034', 'Sales invoice posting - INV-26-0034', 2, 11, 11, '2026-08-09'),
    (28, 'JE-26-1061', '2026-08-04', 1, 8, 1, 'INV-26-0033', 'Sales invoice posting - INV-26-0033', 2, 11, 11, '2026-08-04'),
    (29, 'JE-26-1062', '2026-08-03', 1, 8, 2, 'INV-26-0135', 'Sales invoice posting - INV-26-0135', 2, 11, 11, '2026-08-03'),
    (30, 'JE-26-1063', '2026-08-14', 1, 8, 3, 'INV-26-8866', 'Sales invoice posting - INV-26-8866', 2, 11, 11, '2026-08-14'),
    (31, 'JE-26-1064', '2026-08-15', 1, 8, 2, 'INV-26-8867', 'Sales invoice posting - INV-26-8867', 2, 11, 11, '2026-08-15'),
    (32, 'JE-26-1065', '2026-08-04', 2, 8, 1, 'VCH-26-0096', 'Settlement of INV-26-0033', 2, 2, 2, '2026-08-04'),
    (33, 'JE-26-1066', '2026-08-03', 2, 8, 2, 'VCH-26-0097', 'Settlement of INV-26-0135', 2, 2, 2, '2026-08-03'),
    (34, 'JE-26-1067', '2026-08-14', 2, 8, 3, 'VCH-26-0098', 'Settlement of INV-26-8866', 2, 2, 2, '2026-08-14'),
    (35, 'JE-26-1068', '2026-08-15', 2, 8, 2, 'VCH-26-0099', 'Settlement of INV-26-8867', 2, 2, 2, '2026-08-15');

INSERT INTO "JournalEntryLine" (line_id, entry_id, line_no, account_id, party_user_id, description, debit_amount, credit_amount) VALUES
    (1, 1, 1, 10, 12, 'INV-26-0142 receivable',      145000.00,      0.00),
    (2, 1, 2, 29, NULL, 'Sales tax exclusive',      0.00, 122768.20),
    (3, 1, 3, 21, NULL, 'GST 18%',                  0.00, 22231.80),
    (4, 1, 4, 33, NULL, 'Average cost x quantity', 70710.00,      0.00),
    (5, 1, 5, 12, NULL, 'Stock reduced',            0.00, 70710.00),
    (44, 21, 1, 10, 17, 'INV-26-0088 receivable',      18400.00,      0.00),
    (45, 21, 2, 29, NULL, 'Sales tax exclusive',      0.00, 15563.20),
    (46, 21, 3, 21, NULL, 'GST 18%',                  0.00, 2836.80),
    (47, 21, 4, 33, NULL, 'Average cost x quantity', 8510.00,      0.00),
    (48, 21, 5, 12, NULL, 'Stock reduced',            0.00, 8510.00),
    (49, 22, 1, 10, 18, 'INV-26-0137 receivable',      12400.00,      0.00),
    (50, 22, 2, 29, NULL, 'Sales tax exclusive',      0.00, 10487.50),
    (51, 22, 3, 21, NULL, 'GST 18%',                  0.00, 1912.50),
    (52, 22, 4, 33, NULL, 'Average cost x quantity', 5250.00,      0.00),
    (53, 22, 5, 12, NULL, 'Stock reduced',            0.00, 5250.00),
    (54, 23, 1, 10, 19, 'INV-26-0139 receivable',      98500.00,      0.00),
    (55, 23, 2, 29, NULL, 'Sales tax exclusive',      0.00, 83275.60),
    (56, 23, 3, 21, NULL, 'GST 18%',                  0.00, 15224.40),
    (57, 23, 4, 33, NULL, 'Average cost x quantity', 47825.00,      0.00),
    (58, 23, 5, 12, NULL, 'Stock reduced',            0.00, 47825.00),
    (59, 24, 1, 10, 21, 'INV-26-0138 receivable',      485000.00,      0.00),
    (60, 24, 2, 29, NULL, 'Sales tax exclusive',      0.00, 410714.00),
    (61, 24, 3, 21, NULL, 'GST 18%',                  0.00, 74286.00),
    (62, 24, 4, 33, NULL, 'Average cost x quantity', 245140.00,      0.00),
    (63, 24, 5, 12, NULL, 'Stock reduced',            0.00, 245140.00),
    (64, 25, 1, 10, 13, 'INV-26-0089 receivable',      84500.00,      0.00),
    (65, 25, 2, 29, NULL, 'Sales tax exclusive',      0.00, 71593.10),
    (66, 25, 3, 21, NULL, 'GST 18%',                  0.00, 12906.90),
    (67, 25, 4, 33, NULL, 'Average cost x quantity', 41305.00,      0.00),
    (68, 25, 5, 12, NULL, 'Stock reduced',            0.00, 41305.00),
    (69, 26, 1, 10, 20, 'INV-26-0087 receivable',      24600.00,      0.00),
    (70, 26, 2, 29, NULL, 'Sales tax exclusive',      0.00, 20782.20),
    (71, 26, 3, 21, NULL, 'GST 18%',                  0.00, 3817.80),
    (72, 26, 4, 33, NULL, 'Average cost x quantity', 11000.00,      0.00),
    (73, 26, 5, 12, NULL, 'Stock reduced',            0.00, 11000.00),
    (74, 27, 1, 10, 15, 'INV-26-0034 receivable',      218000.00,      0.00),
    (75, 27, 2, 29, NULL, 'Sales tax exclusive',      0.00, 184718.00),
    (76, 27, 3, 21, NULL, 'GST 18%',                  0.00, 33282.00),
    (77, 27, 4, 33, NULL, 'Average cost x quantity', 109700.00,      0.00),
    (78, 27, 5, 12, NULL, 'Stock reduced',            0.00, 109700.00),
    (79, 28, 1, 10, 23, 'INV-26-0033 receivable',      320000.00,      0.00),
    (80, 28, 2, 29, NULL, 'Sales tax exclusive',      0.00, 271140.80),
    (81, 28, 3, 21, NULL, 'GST 18%',                  0.00, 48859.20),
    (82, 28, 4, 33, NULL, 'Average cost x quantity', 158820.00,      0.00),
    (83, 28, 5, 12, NULL, 'Stock reduced',            0.00, 158820.00),
    (84, 29, 1, 10, 16, 'INV-26-0135 receivable',      142000.00,      0.00),
    (85, 29, 2, 29, NULL, 'Sales tax exclusive',      0.00, 120320.80),
    (86, 29, 3, 21, NULL, 'GST 18%',                  0.00, 21679.20),
    (87, 29, 4, 33, NULL, 'Average cost x quantity', 65845.00,      0.00),
    (88, 29, 5, 12, NULL, 'Stock reduced',            0.00, 65845.00),
    (89, 30, 1, 10, 14, 'INV-26-8866 receivable',      9800.00,      0.00),
    (90, 30, 2, 29, NULL, 'Sales tax exclusive',      0.00, 8281.70),
    (91, 30, 3, 21, NULL, 'GST 18%',                  0.00, 1518.30),
    (92, 30, 4, 33, NULL, 'Average cost x quantity', 4360.00,      0.00),
    (93, 30, 5, 12, NULL, 'Stock reduced',            0.00, 4360.00),
    (94, 31, 1, 10, 22, 'INV-26-8867 receivable',      7400.00,      0.00),
    (95, 31, 2, 29, NULL, 'Sales tax exclusive',      0.00, 6249.80),
    (96, 31, 3, 21, NULL, 'GST 18%',                  0.00, 1150.20),
    (97, 31, 4, 33, NULL, 'Average cost x quantity', 3260.00,      0.00),
    (98, 31, 5, 12, NULL, 'Stock reduced',            0.00, 3260.00),
    (99, 32, 1, 5, NULL, 'VCH-26-0096',        320000.00,      0.00),
    (100, 32, 2, 10, 23, 'Against INV-26-0033',      0.00, 320000.00),
    (101, 33, 1, 6, NULL, 'VCH-26-0097',        142000.00,      0.00),
    (102, 33, 2, 10, 16, 'Against INV-26-0135',      0.00, 142000.00),
    (103, 34, 1, 4, NULL, 'VCH-26-0098',        9800.00,      0.00),
    (104, 34, 2, 10, 14, 'Against INV-26-8866',      0.00, 9800.00),
    (105, 35, 1, 3, NULL, 'VCH-26-0099',        7400.00,      0.00),
    (106, 35, 2, 10, 22, 'Against INV-26-8867',      0.00, 7400.00);

/* Ten invoices raised off an order, plus two counter sales -- somebody walks
   in, buys and leaves, so order_id is NULL on those two rows. There is no
   stored paid or balance column: what an invoice has been paid is the sum of
   its rows in "VoucherAllocation", and nothing else. */
INSERT INTO "SalesInvoice" (invoice_id, invoice_no, order_id, customer_user_id, location_id, invoice_date, due_date, subtotal, discount_amount, tax_amount, total_amount, status_id, method_id, entry_id, created_by_user_id) VALUES
    (1, 'INV-26-0142', 1, 12, 2, '2026-08-13', '2026-09-12', 123510.00, 741.80, 22231.80, 145000.00, 4, 5, 1, 7),
    (2, 'INV-26-0088', 4, 17, 3, '2026-08-11', '2026-09-10', 15760.00, 196.80, 2836.80, 18400.00, 5, 1, 21, 7),
    (3, 'INV-26-0137', 5, 18, 2, '2026-08-06', '2026-09-05', 10625.00, 137.50, 1912.50, 12400.00, 5, 3, 22, 7),
    (4, 'INV-26-0139', 6, 19, 2, '2026-08-12', '2026-09-11', 84580.00, 1304.40, 15224.40, 98500.00, 4, 2, 23, 7),
    (5, 'INV-26-0138', 7, 21, 2, '2026-08-10', '2026-09-09', 412700.00, 1986.00, 74286.00, 485000.00, 5, 2, 24, 7),
    (6, 'INV-26-0089', 8, 13, 3, '2026-08-11', '2026-09-10', 71705.00, 111.90, 12906.90, 84500.00, 2, 5, 25, 8),
    (7, 'INV-26-0087', 9, 20, 3, '2026-08-09', '2026-09-08', 21210.00, 427.80, 3817.80, 24600.00, 5, 4, 26, 8),
    (8, 'INV-26-0034', 14, 15, 1, '2026-08-09', '2026-09-08', 184900.00, 182.00, 33282.00, 218000.00, 4, 2, 27, 10),
    (9, 'INV-26-0033', 15, 23, 1, '2026-08-04', '2026-09-03', 271440.00, 299.20, 48859.20, 320000.00, 5, 2, 28, 10),
    (10, 'INV-26-0135', 17, 16, 2, '2026-08-03', '2026-09-02', 120440.00, 119.20, 21679.20, 142000.00, 5, 2, 29, 7),
    (11, 'INV-26-8866', NULL, 14, 3, '2026-08-14', '2026-08-14', 8435.00, 153.30, 1518.30, 9800.00, 5, 1, 30, 4),
    (12, 'INV-26-8867', NULL, 22, 2, '2026-08-15', '2026-08-15', 6390.00, 140.20, 1150.20, 7400.00, 5, 1, 31, 4);

INSERT INTO "SalesInvoiceItem" (invoice_item_id, invoice_id, line_no, product_id, quantity, unit_price, discount_percent, tax_percent, unit_cost, line_total) VALUES
    (1, 1, 1, 1, 63, 980.00, 0.00, 18.00, 580.00, 61740.00),
    (2, 1, 2, 24, 15, 2480.00, 0.00, 18.00, 1480.00, 37200.00),
    (3, 1, 3, 19, 126, 195.00, 0.00, 18.00, 95.00, 24570.00),
    (4, 2, 1, 11, 13, 620.00, 0.00, 18.00, 350.00, 8060.00),
    (5, 2, 2, 27, 11, 420.00, 0.00, 18.00, 230.00, 4620.00),
    (6, 2, 3, 20, 22, 140.00, 0.00, 18.00, 65.00, 3080.00),
    (7, 3, 1, 6, 18, 285.00, 0.00, 18.00, 145.00, 5130.00),
    (8, 3, 2, 20, 25, 140.00, 0.00, 18.00, 65.00, 3500.00),
    (9, 3, 3, 30, 7, 285.00, 0.00, 18.00, 145.00, 1995.00),
    (10, 4, 1, 3, 17, 2480.00, 0.00, 18.00, 1480.00, 42160.00),
    (11, 4, 2, 4, 20, 1280.00, 0.00, 18.00, 720.00, 25600.00),
    (12, 4, 3, 21, 29, 580.00, 0.00, 18.00, 285.00, 16820.00),
    (13, 5, 1, 8, 45, 3680.00, 0.00, 18.00, 2280.00, 165600.00),
    (14, 5, 2, 17, 29, 4280.00, 0.00, 18.00, 2480.00, 124120.00),
    (15, 5, 3, 24, 33, 2480.00, 0.00, 18.00, 1480.00, 81840.00),
    (16, 5, 4, 10, 121, 340.00, 0.00, 18.00, 180.00, 41140.00),
    (17, 6, 1, 9, 11, 3280.00, 0.00, 18.00, 1980.00, 36080.00),
    (18, 6, 2, 5, 20, 1050.00, 0.00, 18.00, 620.00, 21000.00),
    (19, 6, 3, 19, 75, 195.00, 0.00, 18.00, 95.00, 14625.00),
    (20, 7, 1, 12, 22, 480.00, 0.00, 18.00, 260.00, 10560.00),
    (21, 7, 2, 14, 15, 430.00, 0.00, 18.00, 222.00, 6450.00),
    (22, 7, 3, 20, 30, 140.00, 0.00, 18.00, 65.00, 4200.00),
    (23, 8, 1, 8, 20, 3680.00, 0.00, 18.00, 2280.00, 73600.00),
    (24, 8, 2, 3, 22, 2480.00, 0.00, 18.00, 1480.00, 54560.00),
    (25, 8, 3, 25, 38, 980.00, 0.00, 18.00, 580.00, 37240.00),
    (26, 8, 4, 19, 100, 195.00, 0.00, 18.00, 95.00, 19500.00),
    (27, 9, 1, 17, 25, 4280.00, 0.00, 18.00, 2480.00, 107000.00),
    (28, 9, 2, 9, 25, 3280.00, 0.00, 18.00, 1980.00, 82000.00),
    (29, 9, 3, 24, 22, 2480.00, 0.00, 18.00, 1480.00, 54560.00),
    (30, 9, 4, 10, 82, 340.00, 0.00, 18.00, 180.00, 27880.00),
    (31, 10, 1, 4, 38, 1280.00, 0.00, 18.00, 720.00, 48640.00),
    (32, 10, 2, 5, 34, 1050.00, 0.00, 18.00, 620.00, 35700.00),
    (33, 10, 3, 21, 41, 580.00, 0.00, 18.00, 285.00, 23780.00),
    (34, 10, 4, 20, 88, 140.00, 0.00, 18.00, 65.00, 12320.00),
    (35, 11, 1, 15, 6, 680.00, 0.00, 18.00, 380.00, 4080.00),
    (36, 11, 2, 19, 13, 195.00, 0.00, 18.00, 95.00, 2535.00),
    (37, 11, 3, 20, 13, 140.00, 0.00, 18.00, 65.00, 1820.00),
    (38, 12, 1, 27, 7, 420.00, 0.00, 18.00, 230.00, 2940.00),
    (39, 12, 2, 22, 6, 295.00, 0.00, 18.00, 145.00, 1770.00),
    (40, 12, 3, 20, 12, 140.00, 0.00, 18.00, 65.00, 1680.00);

/* Returns point at the invoice the goods went out on. condition_id decides
   what happens to the piece: a resalable one goes back on a shelf, a damaged
   one has no restock location and heads for claim stock instead. */
INSERT INTO "SalesReturn" (return_id, return_no, invoice_id, customer_user_id, location_id, return_date, reason, refund_method_id, status_id, entry_id, created_by_user_id) VALUES
    (1, 'RET-KHI-26-0008', 1, 12, 2, '2026-08-14', 'Defective items', 7, 3, NULL, 4),
    (2, 'RET-LHR-26-0004', 7, 20, 3, '2026-08-12', 'Wrong item shipped', 2, 3, NULL, 8),
    (3, 'RET-KHI-26-0007', 2, 17, 3, '2026-08-13', 'Customer dissatisfaction', 1, 2, NULL, 4),
    (4, 'RET-ISB-26-0003', 9, 23, 1, '2026-08-10', 'Expired stock', 2, 3, NULL, 4),
    (5, 'RET-KHI-26-0006', 10, 16, 2, '2026-08-09', 'Over-supplied', 7, 1, NULL, 4);

INSERT INTO "SalesReturnItem" (return_item_id, return_id, line_no, product_id, quantity, unit_price, condition_id, restock_location_id) VALUES
    (1, 1, 1, 1, 4, 980.00, 2, NULL),
    (2, 2, 1, 12, 3, 480.00, 1, 3),
    (3, 3, 1, 11, 1, 620.00, 1, 3),
    (4, 4, 1, 17, 12, 4280.00, 3, NULL),
    (5, 5, 1, 4, 6, 1280.00, 1, 2);

/* ===========================================================================
   SECTION 8 -- PURCHASES
   =========================================================================== */

INSERT INTO "PurchaseOrder" (po_id, po_no, supplier_user_id, location_id, po_date, expected_date, status_id, subtotal, discount_amount, tax_amount, total_amount, notes, created_by_user_id, approved_by_user_id) VALUES
    (1, 'PO-26-0042', 27, 1, '2026-04-30', '2026-05-15', 3, 1567850.00, 63.00, 282213.00, 1850000.00, NULL, 6, 1),
    (2, 'PO-26-0041', 28, 1, '2026-04-28', '2026-05-12', 4, 1050880.00, 38.40, 189158.40, 1240000.00, NULL, 6, 1),
    (3, 'PO-26-0040', 29, 1, '2026-04-25', '2026-05-05', 5, 408545.00, 83.10, 73538.10, 482000.00, NULL, 6, 1),
    (4, 'PO-26-0018', 31, 3, '2026-04-25', '2026-05-10', 2, 805260.00, 206.80, 144946.80, 950000.00, NULL, 9, NULL),
    (5, 'PO-26-0039', 30, 1, '2026-04-22', '2026-05-02', 5, 271300.00, 134.00, 48834.00, 320000.00, NULL, 6, 1),
    (6, 'PO-26-0008', 29, 2, '2026-04-20', '2026-04-30', 7, 122905.00, 27.90, 22122.90, 145000.00, NULL, 4, 1),
    (7, 'PO-26-0038', 27, 1, '2026-04-18', '2026-04-28', 1, 1254500.00, 310.00, 225810.00, 1480000.00, NULL, 6, NULL);

INSERT INTO "PurchaseOrderItem" (po_item_id, po_id, line_no, product_id, quantity, unit_cost, tax_percent, line_total) VALUES
    (1, 1, 1, 11, 1792, 350.00, 18.00, 627200.00),
    (2, 1, 2, 12, 1809, 260.00, 18.00, 470340.00),
    (3, 1, 3, 20, 4824, 65.00, 18.00, 313560.00),
    (4, 1, 4, 19, 1650, 95.00, 18.00, 156750.00),
    (5, 2, 1, 24, 355, 1480.00, 18.00, 525400.00),
    (6, 2, 2, 3, 213, 1480.00, 18.00, 315240.00),
    (7, 2, 3, 10, 1168, 180.00, 18.00, 210240.00),
    (8, 3, 1, 27, 888, 230.00, 18.00, 204240.00),
    (9, 3, 2, 6, 846, 145.00, 18.00, 122670.00),
    (10, 3, 3, 22, 563, 145.00, 18.00, 81635.00),
    (11, 4, 1, 28, 224, 1800.00, 18.00, 403200.00),
    (12, 4, 2, 17, 97, 2480.00, 18.00, 240560.00),
    (13, 4, 3, 15, 425, 380.00, 18.00, 161500.00),
    (14, 5, 1, 21, 476, 285.00, 18.00, 135660.00),
    (15, 5, 2, 26, 169, 480.00, 18.00, 81120.00),
    (16, 5, 3, 23, 376, 145.00, 18.00, 54520.00),
    (17, 6, 1, 30, 424, 145.00, 18.00, 61480.00),
    (18, 6, 2, 31, 189, 195.00, 18.00, 36855.00),
    (19, 6, 3, 33, 378, 65.00, 18.00, 24570.00),
    (20, 7, 1, 1, 1081, 580.00, 18.00, 626980.00),
    (21, 7, 2, 2, 649, 580.00, 18.00, 376420.00),
    (22, 7, 3, 5, 405, 620.00, 18.00, 251100.00);

/* wht_amount is the withholding tax deducted at source. The supplier's own
   invoice number is unique per supplier, not globally -- two different
   suppliers both numbering an invoice "INV-001" is normal. */
INSERT INTO "PurchaseInvoice" (pi_id, invoice_no, supplier_invoice_no, supplier_user_id, po_id, invoice_date, due_date, subtotal, discount_amount, tax_amount, wht_amount, total_amount, status_id, method_id, entry_id, created_by_user_id) VALUES
    (1, 'PI-26-0042', 'CMP-INV-2026-1842', 27, NULL, '2026-07-19', '2026-08-18', 408565.00, 106.70, 73541.70, 21690.00, 482000.00, 3, 5, NULL, 2),
    (2, 'PI-26-0041', 'SEH-INV-2026-2241', 28, 2, '2026-07-21', '2026-08-20', 610440.00, 319.20, 109879.20, 32400.00, 720000.00, 4, 2, NULL, 2),
    (3, 'PI-26-0040', 'PAI-INV-2026-0421', 30, 5, '2026-07-16', '2026-08-15', 271300.00, 134.00, 48834.00, 14400.00, 320000.00, 5, 2, NULL, 2),
    (4, 'PI-26-0014', 'ATI-INV-2026-1124', 31, NULL, '2026-07-11', '2026-08-10', 241540.00, 17.20, 43477.20, 12825.00, 285000.00, 6, 5, NULL, 2),
    (5, 'PI-26-0039', 'KWC-INV-2026-0942', 29, 6, '2026-06-15', '2026-07-15', 122905.00, 27.90, 22122.90, 6525.00, 145000.00, 6, 5, NULL, 2);

INSERT INTO "PurchaseInvoiceItem" (pi_item_id, pi_id, line_no, product_id, quantity, unit_cost, tax_percent, line_total) VALUES
    (1, 1, 1, 11, 584, 350.00, 18.00, 204400.00),
    (2, 1, 2, 12, 471, 260.00, 18.00, 122460.00),
    (3, 1, 3, 20, 1257, 65.00, 18.00, 81705.00),
    (4, 2, 1, 24, 206, 1480.00, 18.00, 304880.00),
    (5, 2, 2, 3, 124, 1480.00, 18.00, 183520.00),
    (6, 2, 3, 10, 678, 180.00, 18.00, 122040.00),
    (7, 3, 1, 21, 476, 285.00, 18.00, 135660.00),
    (8, 3, 2, 26, 169, 480.00, 18.00, 81120.00),
    (9, 3, 3, 23, 376, 145.00, 18.00, 54520.00),
    (10, 4, 1, 28, 67, 1800.00, 18.00, 120600.00),
    (11, 4, 2, 17, 29, 2480.00, 18.00, 71920.00),
    (12, 4, 3, 15, 129, 380.00, 18.00, 49020.00),
    (13, 5, 1, 30, 424, 145.00, 18.00, 61480.00),
    (14, 5, 2, 31, 189, 195.00, 18.00, 36855.00),
    (15, 5, 3, 33, 378, 65.00, 18.00, 24570.00);

/* The four settlements that leave those invoices fully paid. Every PAID
   invoice in this seed has real money behind it -- there is no status set
   by hand with nothing to back it. */
INSERT INTO "Voucher" (voucher_id, voucher_no, voucher_type_id, voucher_date, location_id, party_user_id, cash_bank_account_id, amount, method_id, payment_provider, reference_no, wallet_txn_id, narration, status_id, entry_id, created_by_user_id) VALUES
    (12, 'VCH-26-0096', 3, '2026-08-04', 1, 23, 5, 320000.00, 2, 'HBL Bank', NULL, NULL, 'Settlement of INV-26-0033', 2, 32, 2),
    (13, 'VCH-26-0097', 3, '2026-08-03', 2, 16, 6, 142000.00, 2, 'Meezan Bank', NULL, NULL, 'Settlement of INV-26-0135', 2, 33, 2),
    (14, 'VCH-26-0098', 1, '2026-08-14', 3, 14, 4, 9800.00, 1, NULL, NULL, NULL, 'Settlement of INV-26-8866', 2, 34, 2),
    (15, 'VCH-26-0099', 1, '2026-08-15', 2, 22, 3, 7400.00, 1, NULL, NULL, NULL, 'Settlement of INV-26-8867', 2, 35, 2);

INSERT INTO "VoucherAllocation" (allocation_id, voucher_id, sales_invoice_id, purchase_invoice_id, amount) VALUES
    (10, 12, 9, NULL, 320000.00),
    (11, 13, 10, NULL, 142000.00),
    (12, 14, 11, NULL, 9800.00),
    (13, 15, 12, NULL, 7400.00);


/* Which invoice each voucher settled. This -- not a column on the invoice --
   is what "paid" means:
       SELECT SUM(amount) FROM "VoucherAllocation" WHERE sales_invoice_id = ?
   VCH-26-0087 is deliberately absent: that PKR 32,750 came in over the
   counter before the order was ever invoiced, so it sits on account. */
INSERT INTO "VoucherAllocation" (allocation_id, voucher_id, sales_invoice_id, purchase_invoice_id, amount) VALUES
    (1, 1,  1,    NULL, 100000.00),
    (2, 4,  3,    NULL,  12400.00),
    (3, 5,  7,    NULL,  24600.00),
    (4, 7,  4,    NULL,  40000.00),
    (5, 8,  2,    NULL,  18400.00),
    (6, 9,  5,    NULL, 485000.00),
    (7, 10, 8,    NULL,  87200.00),
    (8, 2,  NULL, 3,    320000.00),
    (9, 11, NULL, 2,    360000.00);

/* Money a rep took at the shop door. Notice what is NOT here: a link to the
   ledger, until Accounts confirms it. COL-26-0088 and COL-26-0087 are sitting
   on the Confirm Collections screen right now -- the rep has the money, the
   customer's balance has not moved, and that gap is deliberate. */
INSERT INTO "Collection" (collection_id, receipt_no, customer_user_id, collected_by_user_id, collected_on, amount, method_id, reference_no, bank_name, cheque_date, status_id, confirmed_on, confirmed_by_user_id, voucher_id, note) VALUES
    (1, 'COL-26-0088', 12, 7, '2026-08-13',  60000.00, 1, NULL,           NULL,          NULL,         1, NULL,         NULL, NULL, 'Collected on the evening round'),
    (2, 'COL-26-0087', 16, 7, '2026-08-13', 140000.00, 6, '0012457',      'Meezan Bank', '2026-08-20', 1, NULL,         NULL, NULL, 'Post-dated to the 20th'),
    (3, 'COL-26-0086', 19, 7, '2026-08-12',  40000.00, 2, 'TXN-77483921', 'HBL',         NULL,         2, '2026-08-12', 2,    7,    NULL),
    (4, 'COL-26-0085', 17, 7, '2026-08-11',  18400.00, 1, NULL,           NULL,          NULL,         2, '2026-08-11', 2,    8,    NULL),
    (5, 'COL-26-0084', 18, 7, '2026-08-10',  12400.00, 3, 'JC-998877665', NULL,          NULL,         2, '2026-08-10', 2,    4,    NULL),
    (6, 'COL-26-0083', 21, 7, '2026-08-09', 485000.00, 2, 'TXN-77410882', 'Meezan Bank', NULL,         2, '2026-08-09', 2,    9,    NULL),
    (7, 'COL-26-0082', 13, 8, '2026-08-08',  25000.00, 6, '0044120',      'UBL',         '2026-08-08', 3, NULL,         NULL, NULL, 'Cheque returned - insufficient funds'),
    (8, 'COL-26-0081', 20, 8, '2026-08-07',  24600.00, 4, 'EP-554433221', NULL,          NULL,         2, '2026-08-07', 2,    5,    NULL);

/* The frontend keeps this as against: string[] on the collection. An array is
   not first normal form, so it is a child table. COL-26-0082 has no rows at
   all -- that money was taken on account. */
INSERT INTO "CollectionAllocation" (allocation_id, collection_id, order_id, amount) VALUES
    (1, 1, 1,   60000.00),
    (2, 2, 17, 140000.00),
    (3, 3, 6,   40000.00),
    (4, 4, 4,   18400.00),
    (5, 5, 5,   12400.00),
    (6, 6, 7,  485000.00),
    (7, 8, 9,   24600.00);

/* ---- goods received ---------------------------------------------------- */

INSERT INTO "GoodsReceipt" (grn_id, grn_no, po_id, supplier_user_id, location_id, receipt_date, delivery_note_no, vehicle_no, total_value, status_id, entry_id, received_by_user_id, notes) VALUES
    (1, 'GRN-26-0089', 2,    28, 1, '2026-04-29', 'SEH-2026-0419', 'BHN-882',  482000.00, 2, 3,    4, '5 pieces broken on arrival - held back from the shelf'),
    (2, 'GRN-26-0088', 3,    29, 1, '2026-04-28', 'KWC-DN-1842',   'TLM-441',  329000.00, 2, NULL, 4, NULL),
    (3, 'GRN-26-0087', 5,    30, 1, '2026-04-26', 'PAI-2026-0421', 'JKL-2289', 316000.00, 2, NULL, 4, NULL),
    (4, 'GRN-26-0024', NULL, 31, 3, '2026-04-24', 'ATI-DN-0884',   NULL,       283200.00, 2, NULL, 9, 'Arrived against a phone call - paperwork followed'),
    (5, 'GRN-26-0090', 2,    28, 1, '2026-05-01', 'SEH-2026-0421', 'BHN-882',  121600.00, 1, NULL, 4, 'Balance of PO-26-0041, not posted yet');

INSERT INTO "GoodsReceiptItem" (grn_item_id, grn_id, line_no, product_id, qty_received, qty_damaged, unit_cost, batch_no, expiry_date) VALUES
    (1,  1, 1, 24, 155, 2, 1480.00, 'SEH-B-2601', NULL),
    (2,  1, 2, 3,  150, 3, 1480.00, 'SEH-B-2602', NULL),
    (3,  1, 3, 10, 170, 0,  180.00, 'SEH-B-2603', NULL),
    (4,  2, 1, 27, 800, 0,  230.00, 'KWC-2604',   NULL),
    (5,  2, 2, 6,  600, 0,  145.00, 'KWC-2605',   NULL),
    (6,  2, 3, 22, 400, 0,  145.00, 'KWC-2606',   NULL),
    (7,  3, 1, 21, 400, 2,  285.00, 'PAI-2607',   NULL),
    (8,  3, 2, 26, 300, 0,  480.00, 'PAI-2608',   NULL),
    (9,  3, 3, 23, 400, 2,  145.00, 'PAI-2609',   NULL),
    (10, 4, 1, 28, 60,  0, 1800.00, 'ATI-2610',   NULL),
    (11, 4, 2, 17, 40,  0, 2480.00, 'ATI-2611',   NULL),
    (12, 4, 3, 15, 200, 0,  380.00, 'ATI-2612',   NULL),
    (13, 5, 1, 24, 40,  0, 1480.00, 'SEH-B-2613', NULL),
    (14, 5, 2, 3,  30,  0, 1480.00, 'SEH-B-2614', NULL),
    (15, 5, 3, 10, 100, 0,  180.00, 'SEH-B-2615', NULL);

INSERT INTO "PurchaseReturn" (pr_id, return_no, pi_id, supplier_user_id, location_id, return_date, reason, status_id, entry_id, created_by_user_id) VALUES
    (1, 'PR-26-0008', 3, 30, 1, '2026-04-28', 'Damaged in transit',  3, NULL, 4),
    (2, 'PR-26-0007', 5, 29, 1, '2026-04-25', 'Wrong specification', 2, NULL, 4),
    (3, 'PR-26-0003', 4, 31, 3, '2026-04-22', 'Expired stock',       3, NULL, 9),
    (4, 'PR-26-0009', 1, 27, 1, '2026-04-30', 'Wrong colour',        1, NULL, 4),
    (5, 'PR-26-0010', 2, 28, 1, '2026-05-02', 'Short in packing',    2, NULL, 4);

INSERT INTO "PurchaseReturnItem" (pr_item_id, pr_id, line_no, product_id, quantity, unit_cost) VALUES
    (1,  1, 1, 21, 40,  285.00),
    (2,  1, 2, 26, 14,  480.00),
    (3,  2, 1, 30, 60,  145.00),
    (4,  2, 2, 31, 18,  195.00),
    (5,  3, 1, 28, 8,  1800.00),
    (6,  3, 2, 15, 26,  380.00),
    (7,  4, 1, 11, 60,  350.00),
    (8,  4, 2, 12, 45,  260.00),
    (9,  5, 1, 24, 12, 1480.00),
    (10, 5, 2, 10, 40,  180.00);


/* ===========================================================================
   SECTION 9 -- STOCK MOVEMENT
   =========================================================================== */

/* TRF-26-0014 is the one currently in the air: it left the Warehouse, has not
   landed at Shop 2, and its 240 pieces sit against LOC-05 In Transit. */
INSERT INTO "StockTransfer" (transfer_id, transfer_no, from_location_id, to_location_id, transfer_date, status_id, initiated_by_user_id, approved_by_user_id, received_on, notes) VALUES
    (1, 'TRF-26-0014', 1, 3, '2026-08-12', 4, 2, 1,    NULL,         'Cables for the Shop 2 counter'),
    (2, 'TRF-26-0013', 1, 2, '2026-08-11', 5, 4, 1,    '2026-08-12', NULL),
    (3, 'TRF-26-0012', 1, 3, '2026-04-27', 5, 2, 1,    '2026-04-28', NULL),
    (4, 'TRF-26-0008', 3, 2, '2026-08-08', 3, 9, 1,    NULL,         'Awaiting the van'),
    (5, 'TRF-26-0011', 1, 3, '2026-08-05', 2, 2, NULL, NULL,         NULL),
    (6, 'TRF-26-0010', 1, 2, '2026-08-04', 6, 2, NULL, NULL,         'Rejected - Shop 2 had enough on the shelf');

INSERT INTO "StockTransferItem" (transfer_item_id, transfer_id, line_no, product_id, quantity) VALUES
    (1,  1, 1, 19, 120),
    (2,  1, 2, 20, 120),
    (3,  2, 1, 27, 90),
    (4,  2, 2, 6,  60),
    (5,  3, 1, 22, 60),
    (6,  3, 2, 10, 40),
    (7,  4, 1, 15, 35),
    (8,  4, 2, 16, 25),
    (9,  5, 1, 1,  100),
    (10, 5, 2, 24, 80),
    (11, 6, 1, 11, 30),
    (12, 6, 2, 12, 20);

INSERT INTO "StockAdjustment" (adjustment_id, adjustment_no, location_id, adjustment_date, reason_id, reason_notes, status_id, entry_id, created_by_user_id) VALUES
    (1, 'ADJ-26-0034', 1, '2026-08-13', 1, 'Physical count discrepancy on the speaker rack', 2, NULL, 2),
    (2, 'ADJ-26-0012', 3, '2026-08-10', 2, 'Two cartons dropped while loading',              2, NULL, 9),
    (3, 'ADJ-26-0033', 1, '2026-08-09', 4, 'Found extra stock behind the rack',              2, NULL, 2),
    (4, 'ADJ-26-0008', 2, '2026-08-07', 3, 'Expired batteries written off',                  2, NULL, 4),
    (5, 'ADJ-26-0035', 1, '2026-08-14', 1, 'Count in progress on the keychain giveaway',     1, NULL, 2);

INSERT INTO "StockAdjustmentItem" (adjustment_item_id, adjustment_id, line_no, product_id, current_qty, new_qty) VALUES
    (1, 1, 1, 15, 423, 420),
    (2, 1, 2, 16, 363, 360),
    (3, 1, 3, 18, 14,  12),
    (4, 2, 1, 17, 31,  29),
    (5, 2, 2, 18, 7,   4),
    (6, 3, 1, 27, 785, 789),
    (7, 4, 1, 14, 122, 110),
    (8, 4, 2, 12, 371, 367),
    (9, 5, 1, 32, -975, 0);

/* HISTORY: the stock ledger. "StockBalance" says what is on the shelf;
   these rows say how it got there. */
INSERT INTO "StockMovement" (movement_id, product_id, location_id, movement_type_id, moved_at, reference_no, quantity, balance_after, user_id) VALUES
    (1,  1,  1, 2, '2026-08-13 11:42:00', 'ORD-26-0142',     -126, 620,  7),
    (2,  24, 1, 2, '2026-08-13 11:42:00', 'ORD-26-0142',      -25, 205,  7),
    (3,  19, 1, 2, '2026-08-13 11:42:00', 'ORD-26-0142',     -126, 920,  7),
    (4,  24, 1, 1, '2026-04-29 16:20:00', 'GRN-26-0089',      153, 230,  4),
    (5,  3,  1, 1, '2026-04-29 16:20:00', 'GRN-26-0089',      147, 320,  4),
    (6,  19, 1, 3, '2026-08-12 14:00:00', 'TRF-26-0014',     -120, 920,  2),
    (7,  19, 5, 4, '2026-08-12 14:00:00', 'TRF-26-0014',      120, 120,  2),
    (8,  20, 1, 3, '2026-08-12 14:00:00', 'TRF-26-0014',     -120, 1240, 2),
    (9,  20, 5, 4, '2026-08-12 14:00:00', 'TRF-26-0014',      120, 120,  2),
    (10, 15, 1, 5, '2026-08-13 17:30:00', 'ADJ-26-0034',       -3, 420,  2),
    (11, 2,  1, 6, '2026-08-14 10:15:00', 'RET-KHI-26-0008',    4, 490,  4),
    (12, 21, 1, 7, '2026-04-28 12:00:00', 'PR-26-0008',       -40, 310,  4);


/* ===========================================================================
   SECTION 10 -- DELIVERY, CLAIMS AND FIELD WORK
   =========================================================================== */

/* One row per consignment. DLV-26-0211 is the Quetta problem: three reminders
   sent, still nobody has said whether it arrived. DLV-26-0210 came straight
   back, and has no invoice because that order was never invoiced. */
INSERT INTO "Delivery" (delivery_id, delivery_no, order_id, invoice_id, channel_id, courier_id, tracking_no, booked_date, expected_date, delivered_date, status_id, parcels, weight_kg, cod_amount, is_cod_settled, booking_charge, reminders_sent, confirmed_by_user_id, notes) VALUES
    (1,  'DLV-26-0217', 1,  1,    1, 13, NULL,             '2026-08-13', '2026-08-13', NULL,         3, 3,  5.40,      0.00, FALSE,   0.00, 1, NULL, 'Handed to the rep on the evening round'),
    (2,  'DLV-26-0216', 8,  6,    3, 9,  'RC-99302218',    '2026-08-11', '2026-08-14', NULL,         3, 6,  14.20, 84500.00, FALSE, 300.00, 2, NULL, NULL),
    (3,  'DLV-26-0215', 4,  2,    1, 13, NULL,             '2026-08-11', '2026-08-11', '2026-08-11', 6, 1,  1.10,      0.00, FALSE,   0.00, 0, 7,    'Own rider - same day'),
    (4,  'DLV-26-0214', 14, 8,    4, 11, 'BL-2026-4471',   '2026-08-09', '2026-08-14', NULL,         3, 11, 28.60,     0.00, FALSE, 850.00, 1, NULL, 'Large consignment - 11 cartons'),
    (5,  'DLV-26-0213', 17, 10,   1, 13, NULL,             '2026-08-03', '2026-08-03', '2026-08-03', 6, 3,  5.80,      0.00, FALSE,   0.00, 0, 7,    NULL),
    (6,  'DLV-26-0212', 9,  7,    3, 10, 'MRC-44120901',   '2026-08-09', '2026-08-12', '2026-08-12', 6, 2,  3.30,  24600.00, TRUE,  280.00, 0, 4,    NULL),
    (7,  'DLV-26-0211', 5,  3,    3, 8,  'PIC-88213',      '2026-08-06', '2026-08-10', NULL,         4, 2,  4.20,      0.00, FALSE, 350.00, 3, NULL, 'Customer says shop was closed, cargo re-attempting'),
    (8,  'DLV-26-0210', 10, NULL, 3, 9,  'RC-99302190',    '2026-08-05', '2026-08-08', NULL,         8, 4,  9.10,      0.00, FALSE, 300.00, 0, NULL, 'Customer refused - said rate was agreed lower'),
    (9,  'DLV-26-0209', 7,  5,    2, 1,  'TCS7841203301',  '2026-08-10', '2026-08-12', '2026-08-12', 6, 2,  3.30,      0.00, FALSE, 220.00, 0, 4,    NULL),
    (10, 'DLV-26-0208', 6,  4,    2, 7,  'PX7741203355',   '2026-08-12', '2026-08-15', NULL,         3, 5,  11.80,     0.00, FALSE, 175.00, 0, NULL, NULL),
    (11, 'DLV-26-0207', 15, 9,    4, 12, 'BL-2026-4388',   '2026-08-04', '2026-08-09', '2026-08-08', 6, 8,  22.40,     0.00, FALSE, 780.00, 0, 4,    NULL);

/* A claim is per item, never per order. A shopkeeper brings back one dead
   battery months later with no idea which invoice it came on -- so
   original_order_no is free text and almost always NULL.
   The first three are still on the claim shelf (LOC-04). */
INSERT INTO "Claim" (claim_id, claim_no, customer_user_id, received_on, received_by_user_id, product_id, quantity, unit_cost, reason_id, claim_note, original_order_no, outcome_id, stage_id, supplier_user_id, sent_on, settled_on, supplier_note, reminders_sent) VALUES
    (1,  'CLM-26-0142', 12, '2026-08-14', 6, 11, 12,  350.00, 1, 'Whole packet dead, same batch',      NULL, 1, 1, NULL, NULL,         NULL,         NULL,                                     0),
    (2,  'CLM-26-0141', 16, '2026-08-13', 6, 1,  3,   580.00, 2, 'Right bud silent after a week',      NULL, 1, 1, NULL, NULL,         NULL,         NULL,                                     0),
    (3,  'CLM-26-0140', 19, '2026-08-12', 6, 24, 2,  1480.00, 5, 'Customer says voltage fluctuation',  NULL, 3, 1, NULL, NULL,         NULL,         NULL,                                     1),
    (4,  'CLM-26-0138', 13, '2026-08-04', 6, 12, 24,  260.00, 3, 'Backup under an hour',               NULL, 1, 2, 27,   '2026-08-06', NULL,         NULL,                                     2),
    (5,  'CLM-26-0137', 21, '2026-07-30', 6, 28, 4,  1800.00, 2, 'Bluetooth pairing fails',            NULL, 3, 2, 28,   '2026-08-01', NULL,         'Under test at factory',                  4),
    (6,  'CLM-26-0135', 17, '2026-07-28', 6, 19, 40,   95.00, 1, 'Whole carton not charging',          NULL, 1, 2, 27,   '2026-07-30', NULL,         NULL,                                     3),
    (7,  'CLM-26-0130', 14, '2026-07-20', 6, 20, 30,   65.00, 1, NULL,                                 NULL, 1, 3, 27,   '2026-07-22', '2026-08-02', 'Fresh carton sent',                      0),
    (8,  'CLM-26-0128', 20, '2026-07-15', 6, 11, 18,  350.00, 3, NULL,                          'ORD-26-0087', 1, 3, 27, '2026-07-17', '2026-07-29', 'Replaced in full',                       0),
    (9,  'CLM-26-0126', 25, '2026-07-10', 6, 27, 6,   230.00, 4, 'Casing cracked',                     NULL, 3, 5, 29,   '2026-07-12', '2026-07-25', 'Physical damage not covered',            0),
    (10, 'CLM-26-0124', 18, '2026-07-05', 6, 1,  2,   580.00, 5, NULL,                                 NULL, 1, 6, 28,   '2026-07-07', '2026-07-20', 'Refused - posted to Warranty & Claims',  0),
    (11, 'CLM-26-0122', 12, '2026-06-28', 6, 11, 20,  350.00, 1, NULL,                                 NULL, 1, 4, 27,   '2026-06-30', '2026-07-14', 'Credit note against next purchase',      0),
    (12, 'CLM-26-0120', 16, '2026-06-20', 6, 12, 15,  260.00, 3, NULL,                                 NULL, 1, 3, 27,   '2026-06-22', '2026-07-04', 'Replaced in full',                       0);

INSERT INTO "CustomerVisit" (visit_id, customer_user_id, sales_person_user_id, visited_at, outcome_id, notes, latitude, longitude) VALUES
    (1, 12, 9, '2026-04-30 11:30:00', 1, 'Discussed bulk discount on the PowerX line', 31.520400, 74.358700),
    (2, 13, 9, '2026-04-30 10:00:00', 2, 'Customer wants to wait for new pricing',     31.549700, 74.343600),
    (3, 16, 2, '2026-04-29 15:45:00', 3, 'Need to send updated catalogue by Monday',   24.860700, 67.001100),
    (4, 14, 2, '2026-04-29 12:15:00', 4, 'Collected PKR 32,750 against INV-26-0141',   24.856700, 67.015200),
    (5, 17, 9, '2026-04-29 10:30:00', 1, 'Reorder of Titan T9, 50 units',              31.549700, 74.343600),
    (6, 23, 4, '2026-04-28 14:00:00', 1, 'Big order for VOLT chargers and speakers',   33.729400, 73.093100),
    (7, 22, 4, '2026-04-28 11:00:00', 3, 'Will decide after Eid',                      34.015100, 71.524900);


/* ===========================================================================
   SECTION 11 -- NOTIFICATIONS AND HISTORY
   =========================================================================== */

INSERT INTO "Notification" (notification_id, user_id, severity_id, icon, title, body, created_at, is_read) VALUES
    (1, 1, 3, 'alert-triangle', '3 orders crossed their limit', 'Waiting for your approval',                 '2026-08-15 09:58:00', FALSE),
    (2, 1, 1, 'package',        'Stock received GRN-26-0089',   'From China Mobile Plaza - 240 pcs',         '2026-08-15 09:45:00', FALSE),
    (3, 1, 2, 'banknote',       'Money received',               'PKR 1,45,000 from Hafeez Center #28',       '2026-08-15 09:00:00', FALSE),
    (4, 1, 4, 'clock',          '7 invoices overdue',           'Recovery 60+ days needs attention',         '2026-08-15 07:00:00', TRUE),
    (5, 1, 1, 'send',           'Delivery booked with TCS',     'INV-26-0138 - tracking TCS7841203301',      '2026-08-15 05:00:00', TRUE),
    (6, 1, 1, 'database',       'Backup completed',             'Daily backup successful - 1.2 GB',          '2026-08-14 02:04:00', TRUE);

/* HISTORY: the audit trail behind Activity History. user_id is NULL-able
   because the service account posts entries and a failed sign-in has no
   authenticated user to blame. */
INSERT INTO "ActivityLog" (log_id, user_id, action_name, entity_type, entity_reference, detail, ip_address, location_id, severity_id, logged_at) VALUES
    (1,  7,    'DISPATCHED', 'SalesOrder',   'ORD-26-0142',            'Invoice INV-26-0142 generated automatically', '182.181.45.22', 2,    1, '2026-08-15 09:58:00'),
    (2,  11,   'AUTO-POST',  'JournalEntry', 'JE-26-1042',             'Posted by the service account',              'internal',      2,    5, '2026-08-15 09:58:00'),
    (3,  2,    'OVERRIDDEN', 'SalesOrder',   'ORD-26-0089',            'Credit hold released by hand',               '182.181.45.30', 3,    3, '2026-08-15 09:45:00'),
    (4,  4,    'POSTED',     'GoodsReceipt', 'GRN-26-0089',            '235 accepted, 5 held back damaged',          '182.181.45.45', 1,    1, '2026-08-15 09:00:00'),
    (5,  2,    'CREATED',    'Voucher',      'VCH-26-0089',            'Bank receipt PKR 100,000',                   '182.181.45.30', 2,    1, '2026-08-15 08:00:00'),
    (6,  1,    'UPDATED',    'Party',        'VZ-C-0008',              'Credit limit raised to PKR 600,000',         '182.181.45.10', 2,    3, '2026-08-15 07:00:00'),
    (7,  1,    'CREATED',    'Party',        'VZ-C-0007',              'Quetta Cellular opened',                     '182.181.45.10', 2,    1, '2026-08-14 16:00:00'),
    (8,  7,    'LOGIN',      'UserSession',  'sales@advpos.pk',        'Signed in',                                  '182.181.45.22', 3,    5, '2026-08-15 05:00:00'),
    (9,  NULL, 'LOGIN_FAIL', 'UserSession',  'asad@vizo.com.pk',       'Three failed attempts - account locked',     '39.40.123.55',  NULL, 4, '2026-08-14 21:14:00'),
    (10, 1,    'DELETED',    'Product',      '05050930',               'Discontinued line removed from the catalogue','182.181.45.10', 2,    4, '2026-08-14 18:30:00');

/* HISTORY: Backup & Restore. */
INSERT INTO "BackupHistory" (backup_id, started_at, backup_type_id, status_id, size_mb, destination, duration_seconds, checksum_hash, triggered_by_user_id) VALUES
    (1, '2026-08-15 02:00:00', 1, 1, 1269.76, 'MinIO Primary',   222, 'sha256:a8f9c41d2e7b', NULL),
    (2, '2026-08-14 02:00:00', 1, 1, 1249.28, 'MinIO Primary',   218, 'sha256:b3e7d92a1c04', NULL),
    (3, '2026-08-13 14:32:00', 3, 1, 1239.04, 'Manual download', 231, 'sha256:c1d2f80b93ae', 1),
    (4, '2026-08-13 02:00:00', 1, 1, 1228.80, 'MinIO Primary',   222, 'sha256:d4a1e63c7f28', NULL),
    (5, '2026-08-12 02:00:00', 2, 2, 1208.32, 'MinIO Primary',    41, NULL,                  NULL);


/* ===========================================================================
   CHECK QUERIES -- run these after loading. Each must return zero rows.
   ---------------------------------------------------------------------------

   -- 1. Every journal entry balances
   SELECT entry_id, SUM(debit_amount) AS dr, SUM(credit_amount) AS cr
   FROM "JournalEntryLine" GROUP BY entry_id
   HAVING SUM(debit_amount) <> SUM(credit_amount);

   -- 2. Every document header equals the sum of its lines
   SELECT o.order_no, o.subtotal, SUM(i.line_total)
   FROM "SalesOrder" o JOIN "SalesOrderItem" i USING (order_id)
   GROUP BY o.order_id, o.order_no, o.subtotal
   HAVING o.subtotal <> SUM(i.line_total);

   -- 3. subtotal + tax - discount = total
   SELECT order_no FROM "SalesOrder"
   WHERE subtotal + tax_amount - discount_amount <> total_amount;

   -- 4. Nothing allocated beyond an invoice's value
   SELECT i.invoice_no, i.total_amount, SUM(a.amount)
   FROM "SalesInvoice" i JOIN "VoucherAllocation" a ON a.sales_invoice_id = i.invoice_id
   GROUP BY i.invoice_id, i.invoice_no, i.total_amount
   HAVING SUM(a.amount) > i.total_amount;

   And the two that should return real numbers, not zero rows:

   -- Trial balance (must come out equal)
   SELECT SUM(l.debit_amount) AS total_debit, SUM(l.credit_amount) AS total_credit
   FROM "JournalEntryLine" l
   JOIN "JournalEntry" e USING (entry_id)
   WHERE e.status_id = 2;

   -- One customer's ledger
   SELECT e.entry_date, e.entry_no, l.description, l.debit_amount, l.credit_amount
   FROM "JournalEntryLine" l
   JOIN "JournalEntry" e USING (entry_id)
   WHERE l.party_user_id = 12 AND e.status_id = 2
   ORDER BY e.entry_date, l.line_id;
   =========================================================================== */
