/* ============================================================================
   34_BlueTrack_ApiKeyOwnerAndEdit.sql

   RUN THIS AFTER 01-33. Guarded -- safe to re-run.

   D-243 (phase 4), the user's answers of 2026-10-17:

   - web.api_key.OwnerEmail: who is told about the key's expiry. Optional.
   - web.api_key.RateLimitPerMinute: this key's own request limit; NULL uses
     the global one (app_config.ApiRateLimitPerMinute).
   - web.app_config.ApiKeyReminderLeadDays: how many days before a key expires
     its owner is reminded (default 7, 1 to 90).
   - Expiry emails: the owner gets one email the lead time before the expiry
     and one on the day (ApiKeyExpiring); the admins get one on the day
     (ApiKeyExpired, a new notification type, off until enabled). This replaces
     the 30-day and 7-day emails of script 32.
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('web.api_key', 'OwnerEmail') IS NULL
    ALTER TABLE web.api_key ADD OwnerEmail NVARCHAR(320) NULL;
GO

IF COL_LENGTH('web.api_key', 'RateLimitPerMinute') IS NULL
    ALTER TABLE web.api_key ADD RateLimitPerMinute INT NULL
        CONSTRAINT CK_api_key_RateLimitPerMinute CHECK (RateLimitPerMinute IS NULL OR RateLimitPerMinute BETWEEN 1 AND 100000);
GO

IF COL_LENGTH('web.app_config', 'ApiKeyReminderLeadDays') IS NULL
    ALTER TABLE web.app_config ADD ApiKeyReminderLeadDays INT NOT NULL
        CONSTRAINT DF_app_config_ApiKeyReminderLeadDays DEFAULT (7)
        CONSTRAINT CK_app_config_ApiKeyReminderLeadDays CHECK (ApiKeyReminderLeadDays BETWEEN 1 AND 90);
GO

UPDATE web.dim_notification_type
SET Description = 'Once a day, from the daily digest time: an email to each API key''s owner (the owner email on the key) the lead time before it expires and on the day it expires, so the script that uses it can be given a new key in time (D-243). Keys with no owner email are skipped.'
WHERE NotificationTypeName = 'ApiKeyExpiring';
GO

INSERT INTO web.dim_notification_type (NotificationTypeName, Description, IsEnabled)
SELECT 'ApiKeyExpired', 'Once a day, from the daily digest time: an email to the administrators for each API key that expired that day, so they can see which script is about to fail (D-243).', 0
WHERE NOT EXISTS (SELECT 1 FROM web.dim_notification_type WHERE NotificationTypeName = 'ApiKeyExpired');
GO
