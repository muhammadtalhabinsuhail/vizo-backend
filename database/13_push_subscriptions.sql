-- 13_push_subscriptions.sql
--
-- Web Push needs somewhere to keep the endpoint each browser hands back when a
-- person allows notifications. One row per BROWSER, not per user: the same
-- person on a laptop, a phone and the shop terminal is three rows, and a push
-- has to reach all three or they will swear it never arrived.
--
-- "Endpoint" is UNIQUE on purpose. A browser re-subscribes on its own -- after
-- a service-worker update, or when the push service rotates the URL -- and
-- without the constraint every one of those would add another row and the
-- person would get the same notification three or four times.
--
-- Nothing here is a secret of ours: P256dh and Auth are the BROWSER's keys,
-- used to encrypt a payload so only that browser can read it. The VAPID
-- private key, which is ours, is never stored in the database.
--
-- Safe to run twice.

CREATE TABLE IF NOT EXISTS "PushSubscription" (
    "PushSubscriptionId" SERIAL PRIMARY KEY,
    "UserId"             INT          NOT NULL,
    "Endpoint"           VARCHAR(500) NOT NULL,
    "P256dh"             VARCHAR(255) NOT NULL,
    "Auth"               VARCHAR(255) NOT NULL,
    "UserAgent"          VARCHAR(300) NULL,
    "CreatedAt"          TIMESTAMP    NOT NULL DEFAULT NOW(),
    "LastUsedAt"         TIMESTAMP    NULL,

    CONSTRAINT "PushSubscription_Endpoint_key" UNIQUE ("Endpoint"),
    CONSTRAINT "PushSubscription_UserId_fkey"
        FOREIGN KEY ("UserId") REFERENCES "User" ("UserId") ON DELETE CASCADE
);

-- Every push starts "give me this user's browsers", so this is the index that
-- matters.
CREATE INDEX IF NOT EXISTS "IX_PushSubscription_UserId"
    ON "PushSubscription" ("UserId");

-- ─────────────────────────────────────────────────────────────────────────────
-- Which notifications each person wants.
--
-- Ship this WITH the first notification, not after it. Without a way to turn
-- individual kinds off, people mute the whole thing inside a fortnight -- and
-- then the credit-limit alert, the one that actually matters, stops arriving
-- too.
--
-- A MISSING ROW MEANS ON. Storing only the exceptions means a new notification
-- type starts switched on for everybody without a backfill, and the table stays
-- small: it only ever holds the things people have deliberately turned off.
-- ─────────────────────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS "NotificationPreference" (
    "PreferenceId" SERIAL PRIMARY KEY,
    "UserId"       INT         NOT NULL,
    -- Matches PushNotificationService.NotificationKind, e.g. 'ORDER_CREATED'.
    "Kind"         VARCHAR(60) NOT NULL,
    "PushEnabled"  BOOLEAN     NOT NULL DEFAULT TRUE,
    "BellEnabled"  BOOLEAN     NOT NULL DEFAULT TRUE,

    CONSTRAINT "NotificationPreference_User_Kind_key" UNIQUE ("UserId", "Kind"),
    CONSTRAINT "NotificationPreference_UserId_fkey"
        FOREIGN KEY ("UserId") REFERENCES "User" ("UserId") ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS "IX_NotificationPreference_UserId"
    ON "NotificationPreference" ("UserId");

-- Verify:
--   SELECT COUNT(*) FROM "PushSubscription";
--   SELECT COUNT(*) FROM "NotificationPreference";
