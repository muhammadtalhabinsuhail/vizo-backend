/* ===========================================================================
   AdvPOS -- IDENTITY SEQUENCE RESET for the NEON database (PascalCase)
   ---------------------------------------------------------------------------
   THIS IS THE MOST IMPORTANT SCRIPT IN THIS FOLDER. Without it the application
   cannot create anything at all.

   THE PROBLEM
   -----------
   The seed data was loaded with EXPLICIT primary keys:

       INSERT INTO "Product" ("ProductId", ...) VALUES (1, ...), (2, ...) ...

   Postgres only advances an identity sequence when it actually generates a
   value. Supplying the key yourself never touches it, so after the seed every
   sequence was still parked at 1 while the tables held rows numbered up to 106.

   The result: the very first row the API tries to insert asks the sequence for
   an id, gets 1, and collides with seeded row 1:

       23505: duplicate key value violates unique constraint "Product_pkey"

   Measured on Neon 2026-08-25: 77 of 78 identity sequences were behind their
   table's max id. Creating a customer, an order, a product, a voucher, a stock
   adjustment -- every single write in the system -- failed.

   THE FIX
   -------
   Walk every identity column in the public schema and setval its sequence to
   the table's current max id, so the next generated value is max + 1.

   The third argument `true` means "this value has been used", so nextval
   returns max + 1 rather than max. GREATEST(..., 1) keeps it legal for an
   empty table.

   Idempotent and safe to re-run: on a second run every sequence is already
   correct and setval simply writes the same number back. Run it again any time
   you re-import data with explicit keys.
   =========================================================================== */

DO $$
DECLARE
    r        RECORD;
    seq_name TEXT;
    max_id   BIGINT;
    fixed    INT := 0;
    checked  INT := 0;
BEGIN
    FOR r IN
        SELECT c.relname AS table_name,
               a.attname AS id_column
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = 'public'
        JOIN pg_attribute a ON a.attrelid = c.oid
                           AND a.attnum > 0
                           AND a.attidentity <> ''
        WHERE c.relkind = 'r'
        ORDER BY c.relname
    LOOP
        seq_name := pg_get_serial_sequence(quote_ident(r.table_name), r.id_column);
        CONTINUE WHEN seq_name IS NULL;

        EXECUTE format('SELECT COALESCE(max(%I), 0) FROM %I', r.id_column, r.table_name)
            INTO max_id;

        EXECUTE format('SELECT setval(%L, GREATEST(%s, 1), true)', seq_name, max_id);

        checked := checked + 1;
        IF max_id > 0 THEN
            fixed := fixed + 1;
            RAISE NOTICE 'reset %.% -> next value %',
                r.table_name, r.id_column, max_id + 1;
        END IF;
    END LOOP;

    RAISE NOTICE '---------------------------------------------';
    RAISE NOTICE 'sequences checked: %, advanced past real data: %', checked, fixed;
END $$;


/* ---------------------------------------------------------------------------
   VERIFY -- every row should read ok. Anything reading BEHIND means the
   reset did not take and inserts into that table will still collide.
   --------------------------------------------------------------------------- */
-- SELECT c.relname AS table_name,
--        (SELECT last_value FROM pg_sequences s
--          WHERE s.schemaname = 'public'
--            AND s.sequencename = split_part(
--                  pg_get_serial_sequence(quote_ident(c.relname), a.attname), '.', 2)
--        ) AS seq_last
-- FROM pg_class c
-- JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = 'public'
-- JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND a.attidentity <> ''
-- WHERE c.relkind = 'r'
-- ORDER BY c.relname;
