-- ═══════════════════════════════════════════════════════════════════════════
--  09_document_series_catchup.sql
--
--  The same bug as 07_neon_sequence_reset.sql, one layer up.
--
--  That script fixed the IDENTITY sequences, which had been left parked at 1
--  while the seed inserted rows with explicit keys. "DocumentSeries" has
--  exactly the same problem and nobody caught it, because it only bites on the
--  one series you happen to use:
--
--      "DocumentSeries"."NextNumber" for ORD said 143
--      "SalesOrder" already held      ORD-26-0144
--
--  So the very first order created through the app tried to write ORD-26-0143,
--  hit the unique index on "OrderNo" and threw 23505. It looked like a bug in
--  the order screen. It was a counter that had never been wound forward past
--  the data somebody loaded underneath it.
--
--  Same story on GRN (90 vs 90 -- a straight collision), PR (9 vs 10) and
--  ADJ (27 vs 35).
--
--  This walks every series, finds the highest number ALREADY USED under that
--  prefix, and parks NextNumber one past it. Series whose documents do not use
--  their prefix at all (SR: the seeded returns are numbered RET-KHI-26-0008)
--  are left exactly as they are -- there is nothing to collide with.
--
--  Idempotent, and safe to re-run. RE-RUN IT ANY TIME DATA IS IMPORTED WITH
--  EXPLICIT DOCUMENT NUMBERS, for the same reason 07 has to be re-run.
--
--  ROLLBACK: there is nothing to roll back. Winding a counter forward can only
--  skip numbers, never reuse one. Winding it BACK is what breaks things.
-- ═══════════════════════════════════════════════════════════════════════════

BEGIN;

DO $$
DECLARE
    s          record;
    highest    int;
    /* series prefix -> the table and column its numbers live in */
    sources    text[][] := ARRAY[
        ARRAY['ORD', 'SalesOrder',      'OrderNo'],
        ARRAY['INV', 'SalesInvoice',    'InvoiceNo'],
        ARRAY['SR',  'SalesReturn',     'ReturnNo'],
        ARRAY['PO',  'PurchaseOrder',   'PoNo'],
        ARRAY['GRN', 'GoodsReceipt',    'GrnNo'],
        ARRAY['PI',  'PurchaseInvoice', 'InvoiceNo'],
        ARRAY['PR',  'PurchaseReturn',  'ReturnNo'],
        ARRAY['TRF', 'StockTransfer',   'TransferNo'],
        ARRAY['ADJ', 'StockAdjustment', 'AdjustmentNo'],
        ARRAY['DLV', 'Delivery',        'DeliveryNo']
    ];
    i int;
BEGIN
    FOR i IN 1 .. array_length(sources, 1) LOOP
        SELECT * INTO s FROM "DocumentSeries" WHERE "Prefix" = sources[i][1];
        CONTINUE WHEN NOT FOUND;

        /* Only rows whose number really starts with this prefix and ends in
           digits. Anything hand-numbered differently is not this series'
           problem. */
        EXECUTE format(
            'SELECT MAX(CAST(regexp_replace(%I, ''^.*-'', '''') AS int))
               FROM %I
              WHERE %I ~ (%L || ''-.*[0-9]$'')',
            sources[i][3], sources[i][2], sources[i][3], sources[i][1])
        INTO highest;

        IF highest IS NOT NULL AND highest >= s."NextNumber" THEN
            RAISE NOTICE 'DocumentSeries % : NextNumber % -> % (highest in use %)',
                sources[i][1], s."NextNumber", highest + 1, highest;
            UPDATE "DocumentSeries"
               SET "NextNumber" = highest + 1
             WHERE "SeriesId" = s."SeriesId";
        END IF;
    END LOOP;
END $$;

COMMIT;

-- Check afterwards:
--   SELECT "Prefix", "NextNumber" FROM "DocumentSeries" ORDER BY "Prefix";
