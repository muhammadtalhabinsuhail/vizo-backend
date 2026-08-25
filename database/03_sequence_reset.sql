/* ===========================================================================
   ADVANCE POS SYSTEM (AdvPOS) -- identity sequence reset
   ---------------------------------------------------------------------------
   02_seed.sql inserts explicit primary keys so the data reads the same way it
   reads in the frontend. That leaves every identity sequence sitting at 1, so
   the first row the backend inserts would collide with seeded row 1.

   Run this once, straight after the seed. Each line moves a sequence past the
   highest id already in its table. Plain SQL -- no procedure, no trigger.
   =========================================================================== */

SELECT setval(pg_get_serial_sequence('"Account"', 'account_id'), COALESCE((SELECT MAX(account_id) FROM "Account"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"AccountGroup"', 'group_id'), COALESCE((SELECT MAX(group_id) FROM "AccountGroup"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"AccountType"', 'account_type_id'), COALESCE((SELECT MAX(account_type_id) FROM "AccountType"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"ActivityLog"', 'log_id'), COALESCE((SELECT MAX(log_id) FROM "ActivityLog"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"AdjustmentReason"', 'reason_id'), COALESCE((SELECT MAX(reason_id) FROM "AdjustmentReason"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"AppSetting"', 'setting_id'), COALESCE((SELECT MAX(setting_id) FROM "AppSetting"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"BackupHistory"', 'backup_id'), COALESCE((SELECT MAX(backup_id) FROM "BackupHistory"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"BackupStatus"', 'status_id'), COALESCE((SELECT MAX(status_id) FROM "BackupStatus"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"BackupType"', 'backup_type_id'), COALESCE((SELECT MAX(backup_type_id) FROM "BackupType"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"BankReconciliation"', 'reconciliation_id'), COALESCE((SELECT MAX(reconciliation_id) FROM "BankReconciliation"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"BankStatementLine"', 'statement_line_id'), COALESCE((SELECT MAX(statement_line_id) FROM "BankStatementLine"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"Brand"', 'brand_id'), COALESCE((SELECT MAX(brand_id) FROM "Brand"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"Category"', 'category_id'), COALESCE((SELECT MAX(category_id) FROM "Category"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"City"', 'city_id'), COALESCE((SELECT MAX(city_id) FROM "City"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"Claim"', 'claim_id'), COALESCE((SELECT MAX(claim_id) FROM "Claim"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"ClaimOutcome"', 'outcome_id'), COALESCE((SELECT MAX(outcome_id) FROM "ClaimOutcome"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"ClaimReason"', 'reason_id'), COALESCE((SELECT MAX(reason_id) FROM "ClaimReason"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"ClaimStage"', 'stage_id'), COALESCE((SELECT MAX(stage_id) FROM "ClaimStage"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"Collection"', 'collection_id'), COALESCE((SELECT MAX(collection_id) FROM "Collection"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"CollectionAllocation"', 'allocation_id'), COALESCE((SELECT MAX(allocation_id) FROM "CollectionAllocation"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"CollectionStatus"', 'status_id'), COALESCE((SELECT MAX(status_id) FROM "CollectionStatus"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"Company"', 'company_id'), COALESCE((SELECT MAX(company_id) FROM "Company"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"Courier"', 'courier_id'), COALESCE((SELECT MAX(courier_id) FROM "Courier"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"CreditHoldPolicy"', 'policy_id'), COALESCE((SELECT MAX(policy_id) FROM "CreditHoldPolicy"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"CustomerVisit"', 'visit_id'), COALESCE((SELECT MAX(visit_id) FROM "CustomerVisit"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"Delivery"', 'delivery_id'), COALESCE((SELECT MAX(delivery_id) FROM "Delivery"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"DeliveryChannel"', 'channel_id'), COALESCE((SELECT MAX(channel_id) FROM "DeliveryChannel"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"DeliveryStatus"', 'status_id'), COALESCE((SELECT MAX(status_id) FROM "DeliveryStatus"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"DocumentSeries"', 'series_id'), COALESCE((SELECT MAX(series_id) FROM "DocumentSeries"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"Expense"', 'expense_id'), COALESCE((SELECT MAX(expense_id) FROM "Expense"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"FiscalPeriod"', 'period_id'), COALESCE((SELECT MAX(period_id) FROM "FiscalPeriod"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"GoodsReceipt"', 'grn_id'), COALESCE((SELECT MAX(grn_id) FROM "GoodsReceipt"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"GoodsReceiptItem"', 'grn_item_id'), COALESCE((SELECT MAX(grn_item_id) FROM "GoodsReceiptItem"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"InvoiceStatus"', 'status_id'), COALESCE((SELECT MAX(status_id) FROM "InvoiceStatus"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"JournalEntry"', 'entry_id'), COALESCE((SELECT MAX(entry_id) FROM "JournalEntry"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"JournalEntryLine"', 'line_id'), COALESCE((SELECT MAX(line_id) FROM "JournalEntryLine"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"JournalEntryType"', 'entry_type_id'), COALESCE((SELECT MAX(entry_type_id) FROM "JournalEntryType"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"Location"', 'location_id'), COALESCE((SELECT MAX(location_id) FROM "Location"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"LocationKind"', 'kind_id'), COALESCE((SELECT MAX(kind_id) FROM "LocationKind"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"MovementType"', 'movement_type_id'), COALESCE((SELECT MAX(movement_type_id) FROM "MovementType"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"Notification"', 'notification_id'), COALESCE((SELECT MAX(notification_id) FROM "Notification"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"OrderStatus"', 'status_id'), COALESCE((SELECT MAX(status_id) FROM "OrderStatus"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"PartyCategory"', 'category_id'), COALESCE((SELECT MAX(category_id) FROM "PartyCategory"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"PaymentMethod"', 'method_id'), COALESCE((SELECT MAX(method_id) FROM "PaymentMethod"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"Permission"', 'permission_id'), COALESCE((SELECT MAX(permission_id) FROM "Permission"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"PostingStatus"', 'status_id'), COALESCE((SELECT MAX(status_id) FROM "PostingStatus"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"Product"', 'product_id'), COALESCE((SELECT MAX(product_id) FROM "Product"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"ProductBarcode"', 'barcode_id'), COALESCE((SELECT MAX(barcode_id) FROM "ProductBarcode"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"Province"', 'province_id'), COALESCE((SELECT MAX(province_id) FROM "Province"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"PurchaseInvoice"', 'pi_id'), COALESCE((SELECT MAX(pi_id) FROM "PurchaseInvoice"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"PurchaseInvoiceItem"', 'pi_item_id'), COALESCE((SELECT MAX(pi_item_id) FROM "PurchaseInvoiceItem"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"PurchaseOrder"', 'po_id'), COALESCE((SELECT MAX(po_id) FROM "PurchaseOrder"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"PurchaseOrderItem"', 'po_item_id'), COALESCE((SELECT MAX(po_item_id) FROM "PurchaseOrderItem"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"PurchaseOrderStatus"', 'status_id'), COALESCE((SELECT MAX(status_id) FROM "PurchaseOrderStatus"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"PurchaseReturn"', 'pr_id'), COALESCE((SELECT MAX(pr_id) FROM "PurchaseReturn"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"PurchaseReturnItem"', 'pr_item_id'), COALESCE((SELECT MAX(pr_item_id) FROM "PurchaseReturnItem"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"ReturnCondition"', 'condition_id'), COALESCE((SELECT MAX(condition_id) FROM "ReturnCondition"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"ReturnStatus"', 'status_id'), COALESCE((SELECT MAX(status_id) FROM "ReturnStatus"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"Role"', 'role_id'), COALESCE((SELECT MAX(role_id) FROM "Role"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"SalesInvoice"', 'invoice_id'), COALESCE((SELECT MAX(invoice_id) FROM "SalesInvoice"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"SalesInvoiceItem"', 'invoice_item_id'), COALESCE((SELECT MAX(invoice_item_id) FROM "SalesInvoiceItem"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"SalesOrder"', 'order_id'), COALESCE((SELECT MAX(order_id) FROM "SalesOrder"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"SalesOrderItem"', 'order_item_id'), COALESCE((SELECT MAX(order_item_id) FROM "SalesOrderItem"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"SalesReturn"', 'return_id'), COALESCE((SELECT MAX(return_id) FROM "SalesReturn"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"SalesReturnItem"', 'return_item_id'), COALESCE((SELECT MAX(return_item_id) FROM "SalesReturnItem"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"SeverityLevel"', 'severity_id'), COALESCE((SELECT MAX(severity_id) FROM "SeverityLevel"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"StockAdjustment"', 'adjustment_id'), COALESCE((SELECT MAX(adjustment_id) FROM "StockAdjustment"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"StockAdjustmentItem"', 'adjustment_item_id'), COALESCE((SELECT MAX(adjustment_item_id) FROM "StockAdjustmentItem"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"StockMovement"', 'movement_id'), COALESCE((SELECT MAX(movement_id) FROM "StockMovement"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"StockTransfer"', 'transfer_id'), COALESCE((SELECT MAX(transfer_id) FROM "StockTransfer"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"StockTransferItem"', 'transfer_item_id'), COALESCE((SELECT MAX(transfer_item_id) FROM "StockTransferItem"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"TransferStatus"', 'status_id'), COALESCE((SELECT MAX(status_id) FROM "TransferStatus"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"User"', 'user_id'), COALESCE((SELECT MAX(user_id) FROM "User"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"VisitOutcome"', 'outcome_id'), COALESCE((SELECT MAX(outcome_id) FROM "VisitOutcome"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"Voucher"', 'voucher_id'), COALESCE((SELECT MAX(voucher_id) FROM "Voucher"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"VoucherAllocation"', 'allocation_id'), COALESCE((SELECT MAX(allocation_id) FROM "VoucherAllocation"), 0) + 1, FALSE);
SELECT setval(pg_get_serial_sequence('"VoucherType"', 'voucher_type_id'), COALESCE((SELECT MAX(voucher_type_id) FROM "VoucherType"), 0) + 1, FALSE);

/* =========================================================================== */
