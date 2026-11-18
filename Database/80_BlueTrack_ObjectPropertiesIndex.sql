/* ============================================================================
   80_BlueTrack_ObjectPropertiesIndex.sql

   RUN THIS AFTER 01-79. Guarded -- safe to re-run.

   D-369 (the user, 2026-11-18, from a customer's resource report): an index
   on dbo.stg_sh_objectproperties (CAOPSafeId, CAOPFileId), including the
   property name and value. usp_Load_FactAccount pivots each staged account's
   properties (UserName, Address, PolicyID, LogonDomain, CPMDisabled) with a
   join on those two columns. At the customer it took 31 seconds of CPU and
   8.26 million reads for 16,524 accounts: about 500 pages, the whole table,
   read once per account, a nested-loop plan (likely compiled while staging
   was nearly empty). Locally, forcing that plan on 16,600 accounts took
   16 million reads and 85 s of CPU without the index, 54,000 reads and
   0.2 s with it. The index holds the table's rows again (a few MB).
   ============================================================================ */
USE $DatabaseName$;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_stg_sh_objectproperties_SafeFile' AND object_id = OBJECT_ID('dbo.stg_sh_objectproperties'))
    CREATE INDEX IX_stg_sh_objectproperties_SafeFile ON dbo.stg_sh_objectproperties (CAOPSafeId, CAOPFileId)
        INCLUDE (CAOPObjectPropertyName, CAOPObjectPropertyValue);
GO

PRINT '80_BlueTrack_ObjectPropertiesIndex.sql complete.';
GO
