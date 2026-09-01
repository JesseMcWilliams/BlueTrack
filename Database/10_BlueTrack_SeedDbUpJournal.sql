/* ============================================================================
   10_BlueTrack_SeedDbUpJournal.sql

   RUN THIS ONCE, before ever pointing BlueTrack.Migrator (App/Migrator) at
   this database for the first time -- and only if 01 through the current
   highest-numbered script have already been applied by hand, as this
   project's Dev database has been throughout this session.

   WHY THIS EXISTS: DbUp tracks which scripts it has already run in its own
   journal table, dbo.SchemaVersions, matching each script by filename.
   Without this seed, DbUp's first real run would see none of 01-09 as
   "already applied" and would try to run all of them for real -- including
   01_BlueTrack_CreateDatabase_Schema.sql, which does a destructive
   DROP DATABASE. That must never happen against a live environment (D-58).

   This script creates dbo.SchemaVersions using DbUp's own exact default
   schema (verified 2026-09-01 directly against the live source of
   DbUp/dbup-sqlserver's SqlTableJournal.cs on GitHub, not assumed from
   memory) and inserts one row per script already applied, with today's
   date as a placeholder Applied timestamp (DbUp doesn't care what the
   historical Applied value actually is, only that a row with that exact
   ScriptName exists).

   Guarded throughout -- safe to re-run, and safe to re-run after adding
   new numbered scripts to this list before they've actually been run by
   hand (it only inserts rows for filenames not already present).
   ============================================================================ */

USE BlueTrack;
GO

IF OBJECT_ID('dbo.SchemaVersions', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.SchemaVersions (
        [Id] INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SchemaVersions PRIMARY KEY,
        [ScriptName] NVARCHAR(255) NOT NULL,
        [Applied] DATETIME NOT NULL
    );
END
GO

INSERT INTO dbo.SchemaVersions (ScriptName, Applied)
SELECT v.ScriptName, GETDATE()
FROM (VALUES
    ('01_BlueTrack_CreateDatabase_Schema.sql'),
    ('02_BlueTrack_ETL_LoadProcedures.sql'),
    ('03_BlueTrack_AccountReconciliation.sql'),
    ('04_BlueTrack_PowerBI_Support.sql'),
    ('05_BlueTrack_SourceImport.sql'),
    ('06_BlueTrack_WebInterface_Schema.sql'),
    ('07_BlueTrack_WebInterface_Seed.sql'),
    ('08_BlueTrack_FixMovePermissionAlias.sql'),
    ('09_BlueTrack_ScheduleImportLoadJob.sql')
) AS v(ScriptName)
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.SchemaVersions sv WHERE sv.ScriptName = v.ScriptName
);
GO

PRINT 'DbUp journal (dbo.SchemaVersions) seeded. BlueTrack.Migrator will now only run scripts numbered 11 and above.';
