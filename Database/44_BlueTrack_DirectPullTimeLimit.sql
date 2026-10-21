/* ============================================================================
   44_BlueTrack_DirectPullTimeLimit.sql

   RUN THIS AFTER 01-43. Guarded -- safe to re-run.

   D-273 (Claude_Docs/Design_CyberArk-Direct-Integration.md, "Settings"): a
   Self-Hosted Direct pull, real or shadow, is stopped once it has run for
   DirectPullTimeLimitMinutes (default 45), so a PVWA that hangs can't hold
   the pull, and the next one, for ever. Set on Edit CyberArk sources.
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('web.cyberark_source_config', 'DirectPullTimeLimitMinutes') IS NULL
    ALTER TABLE web.cyberark_source_config ADD DirectPullTimeLimitMinutes INT NOT NULL
        CONSTRAINT DF_cyberark_source_config_DirectPullTimeLimitMinutes DEFAULT 45;
GO

PRINT '44_BlueTrack_DirectPullTimeLimit.sql complete.';
GO
