/* ============================================================================
   32_BlueTrack_ApiKeyExpiryAndRateLimit.sql

   RUN THIS AFTER 01-31. Guarded -- safe to re-run.

   D-243 (phase 3b), after 31_BlueTrack_ApiAccess.sql:

   - Notification type ApiKeyExpiring: an email for each API key 30 days and
     7 days before it expires (a key made for 30 days or fewer gets only the
     7-day one; for 7 days or fewer, none). Off until enabled and given
     recipients on Admin > Notifications, like the other digests.
   - web.app_config.ApiRateLimitPerMinute: the most requests one API key may
     make in a minute (default 600, 1 to 100000). Over it, the API answers 429.
   ============================================================================ */

USE $DatabaseName$;
GO

INSERT INTO web.dim_notification_type (NotificationTypeName, Description, IsEnabled)
SELECT 'ApiKeyExpiring', 'Once a day, from the daily digest time: an email for each API key 30 days and 7 days before it expires, so the script that uses it can be given a new key in time (D-243).', 0
WHERE NOT EXISTS (SELECT 1 FROM web.dim_notification_type WHERE NotificationTypeName = 'ApiKeyExpiring');
GO

IF COL_LENGTH('web.app_config', 'ApiRateLimitPerMinute') IS NULL
    ALTER TABLE web.app_config ADD ApiRateLimitPerMinute INT NOT NULL
        CONSTRAINT DF_app_config_ApiRateLimitPerMinute DEFAULT (600)
        CONSTRAINT CK_app_config_ApiRateLimitPerMinute CHECK (ApiRateLimitPerMinute BETWEEN 1 AND 100000);
GO
