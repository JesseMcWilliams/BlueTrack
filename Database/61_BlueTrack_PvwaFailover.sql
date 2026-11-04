/* ============================================================================
   61_BlueTrack_PvwaFailover.sql

   RUN THIS AFTER 01-60. Guarded -- safe to re-run.

   - D-320: Self-Hosted Direct can name other PVWA addresses, one per line,
     tried in order when the ones before can't be reached (a pull and Test
     sign in to the first that answers, and stay on it). Up to 5 addresses,
     each like PvwaBaseUrl (https, at most 400 characters).
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('web.cyberark_source_config', 'PvwaFallbackUrls') IS NULL
    ALTER TABLE web.cyberark_source_config ADD PvwaFallbackUrls NVARCHAR(2100) NULL;
GO

PRINT '61_BlueTrack_PvwaFailover.sql complete.';
GO
