/* ═══════════════════════════════════════════════════════════════════════════
   22 — A CUSTOMER'S DOCUMENTS: SIX PHOTOGRAPHS AND ONE PDF
   ═══════════════════════════════════════════════════════════════════════════

   Opening a shop account used to be a form somebody typed from a photocopy on
   the counter. From 22 September it starts with the documents themselves:

       CNIC          front and back
       Business card front and back
       Affidavit     page 1 and page 2

   The photographs go to the IMAGES Cloudinary account as they are taken, and
   their links are kept on the party, one column each. They are kept as links
   rather than as bytes for the same reason every other document in this
   system is: Postgres is not a file store, and a 4 MB photograph in a row
   that every party query touches would be felt on every screen.

   "LegalDocsPdfUrl" is the six of them bound into one file on the DOCUMENTS
   account -- what the owner asked for by name: a legal_documents.pdf anybody
   allowed to see the customer can download. It is built when the account is
   created and rebuilt whenever a photograph is replaced.

   NOTHING HERE IS REQUIRED. A shopkeeper with no affidavit, or none of the
   three, still becomes a customer -- the screen asks, and "not available" is
   an answer. Every column is nullable and every one of them is empty for the
   26 parties that already exist.

   WHAT IS NOT STORED: the raw text the reader pulled off a CNIC. It is shown
   to the salesperson in editable boxes, they correct it, and what they save is
   what the database gets. Keeping the machine's first guess beside the
   corrected version would be keeping a second answer that is wrong more often.

   Safe to run twice.
   ═══════════════════════════════════════════════════════════════════════════ */

ALTER TABLE "Party"
    ADD COLUMN IF NOT EXISTS "CnicFrontUrl"      VARCHAR(500),
    ADD COLUMN IF NOT EXISTS "CnicBackUrl"       VARCHAR(500),
    ADD COLUMN IF NOT EXISTS "CardFrontUrl"      VARCHAR(500),
    ADD COLUMN IF NOT EXISTS "CardBackUrl"       VARCHAR(500),
    ADD COLUMN IF NOT EXISTS "AffidavitFrontUrl" VARCHAR(500),
    ADD COLUMN IF NOT EXISTS "AffidavitBackUrl"  VARCHAR(500),
    ADD COLUMN IF NOT EXISTS "LegalDocsPdfUrl"   VARCHAR(500),
    ADD COLUMN IF NOT EXISTS "LegalDocsPdfId"    VARCHAR(255);


/* ═══════════ WHAT IT SHOULD LOOK LIKE AFTERWARDS ════════════════════════ */

/*  Eight new columns, all empty to begin with:

      SELECT column_name, data_type, is_nullable
        FROM information_schema.columns
       WHERE table_name = 'Party'
         AND column_name IN ('CnicFrontUrl','CnicBackUrl','CardFrontUrl','CardBackUrl',
                             'AffidavitFrontUrl','AffidavitBackUrl','LegalDocsPdfUrl','LegalDocsPdfId')
       ORDER BY column_name;

    And who has documents on file:

      SELECT "PartyCode", "LegalName",
             ("CnicFrontUrl" IS NOT NULL) AS cnic,
             ("CardFrontUrl" IS NOT NULL) AS card,
             ("AffidavitFrontUrl" IS NOT NULL) AS affidavit,
             ("LegalDocsPdfUrl" IS NOT NULL) AS pdf
        FROM "Party" ORDER BY "PartyCode";
*/
