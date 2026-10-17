/* ============================================================================
   35_BlueTrack_ApiKeyExpiredRetention.sql

   RUN THIS AFTER 01-34. Guarded -- safe to re-run.

   D-243 (phase 5), the user's answer of 2026-10-17: an API key that expired
   long enough ago is deleted automatically.

   - web.app_config.ApiKeyExpiredRetentionDays: how many days an expired key
     is kept before it is deleted (default 365, 1 to 3650; 0 keeps them for
     ever).
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('web.app_config', 'ApiKeyExpiredRetentionDays') IS NULL
    ALTER TABLE web.app_config ADD ApiKeyExpiredRetentionDays INT NOT NULL
        CONSTRAINT DF_app_config_ApiKeyExpiredRetentionDays DEFAULT (365)
        CONSTRAINT CK_app_config_ApiKeyExpiredRetentionDays CHECK (ApiKeyExpiredRetentionDays BETWEEN 0 AND 3650);
GO
