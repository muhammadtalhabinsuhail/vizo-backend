-- ============================================================================
--  36  A numbering series for collections (COL-26-0089, COL-26-0090 ...)
-- ============================================================================
--
--  Applied to the local test copy only -- NOT yet run on live.
--
--  Until 27 Sep nothing in the API created a collection: the eight on live
--  (COL-26-0081 .. 0088) were seeded, and the "record collection" dialog only
--  showed a toast. Confirm Collections can now collect against any invoiced
--  order, so collections need their own series. Without one, NextNumber()
--  silently falls back to COL-yyyyMMddHHmmss (HANDOFF trap 9).
--
--  The counter starts after the highest number already used, so the first new
--  one follows the seeds. Safe to run twice.
-- ============================================================================

INSERT INTO "DocumentSeries" ("SeriesKey", "Label", "Prefix", "IncludeYear", "Padding", "NextNumber")
SELECT 'money.collection', 'Collection', 'COL', TRUE, 4,
       COALESCE((SELECT MAX(CAST(SUBSTRING("ReceiptNo" FROM '(\d+)$') AS integer))
                 FROM "Collection" WHERE "ReceiptNo" ~ '^COL-\d{2}-\d+$'), 0) + 1
WHERE NOT EXISTS (SELECT 1 FROM "DocumentSeries" WHERE "Prefix" = 'COL');
