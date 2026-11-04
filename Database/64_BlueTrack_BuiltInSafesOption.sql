/* ============================================================================
   64_BlueTrack_BuiltInSafesOption.sql

   RUN THIS AFTER 01-63. Guarded -- safe to re-run.

   - D-327: Self-Hosted Direct can also read CyberArk's built-in (internal)
     Safes and their members, which PVWA's REST Safe list leaves out. Off by
     default: the data is to be reviewed for whether it is useful. The Safes
     it adds are marked internal (dbo.vw_selfhosted_internal_safe, script 60).
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('web.cyberark_source_config', 'IncludeBuiltInSafes') IS NULL
    ALTER TABLE web.cyberark_source_config ADD
        IncludeBuiltInSafes BIT NOT NULL CONSTRAINT DF_cyberark_source_config_IncludeBuiltInSafes DEFAULT 0;
GO

PRINT '64_BlueTrack_BuiltInSafesOption.sql complete.';
GO
