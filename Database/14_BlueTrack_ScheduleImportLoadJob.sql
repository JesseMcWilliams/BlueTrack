/* ============================================================================
   14_BlueTrack_ScheduleImportLoadJob.sql

   Split 2026-09-05: renumbered from 09_BlueTrack_ScheduleImportLoadJob.sql
   as part of the broader script restructure (see Database/README.md), and
   moved to run last in the sequence (after every schema/seed script) since
   it's purely operational scheduling with no schema of its own -- the same
   reasoning that already placed 07_BlueTrack_SourceImport.sql after the
   schema/ETL files. While reviewing this file against the D-89 lesson
   (never hardcode a target database name -- a prior real incident where a
   hardcoded name caused the real BlueTrack database to be dropped/recreated
   instead of the intended BlueTrackTest), found and fixed one: both
   sp_add_jobstep calls' @database_name argument was the literal 'BlueTrack'
   rather than a substitutable token, which would have pointed this job at
   the wrong database for any environment other than the real BlueTrack
   (e.g. BlueTrackTest).

   A second, related issue found and fixed the same way: the job name
   ('BlueTrack - Import and Load') and schedule name ('BlueTrack Nightly
   2AM') were also hardcoded, but msdb.dbo.sysjobs/sysschedules are
   instance-global, not scoped per target database -- running this file
   against BlueTrackTest on the same SQL Server instance as the real
   BlueTrack would have made sp_delete_job's drop-and-recreate guard
   silently delete and replace the REAL BlueTrack's job (a variant of the
   exact D-89 failure mode: same instance, different intended database).
   Both names now embed a substitutable database-name placeholder so each
   environment gets its own distinctly-named job/schedule on a shared SQL
   Server instance. (Originally written using DbUp's `$DatabaseName$`
   token like every other script, on the assumption this would run through
   App/Migrator -- changed to sqlcmd's `$(DatabaseName)` scripting-variable
   syntax once that assumption turned out to be wrong; see below.)

   RUN THIS AFTER 01-13, once Import and Load have both been run manually
   at least once and confirmed working (see Import_Load_Process_Guide.docx).

   NEVER run this through App/Migrator -- it always excludes this exact
   filename defensively, for every environment (see Program.cs's own
   comment). Confirmed 2026-09-05 against the real BlueTrack database:
   this script's own `USE msdb;` succeeds and creates the job correctly,
   but DbUp then tries to write ITS OWN journal entry for this script
   against whatever database the connection is now on -- msdb, since this
   script never switches back -- which fails with "Invalid object name
   'SchemaVersions'" (msdb has no such table) and DbUp reports the whole
   run as failed even though the job was actually created successfully.
   Run this manually instead, via sqlcmd, which natively supports scripting
   variables the same way DbUp does for every other script -- except the
   syntax is `$(DatabaseName)`, not `$DatabaseName$`, since sqlcmd doesn't
   understand DbUp's own token format:

       sqlcmd -S <server> -C -v DatabaseName="BlueTrack" -i 14_BlueTrack_ScheduleImportLoadJob.sql

   In SSMS, enable SQLCMD Mode first (Query menu), or use `:setvar DatabaseName "BlueTrack"`
   as the file's first line before running it.

   Creates a SQL Server Agent job, "BlueTrack - Import and Load", with two
   steps -- Import, then Load -- matching the guide's own "Recommended
   Operational Cadence" section. Runs nightly at 2:00 AM.

   REAL VALUES USED BELOW (confirmed 2026-09-01, updated 2026-09-05, not
   placeholders):
     - EVD database name: CyberArkSH
     - Privilege Cloud export folder: C:\Code\aPePAS\Output (updated
       2026-09-05, was C:\Code\BlueTrack\Reference\PrivilegedCloud)
       (a local path, not UNC -- confirm the SQL Server *service account*,
       not your own login, has read access to it; see Prerequisites in the guide)

   BUG FOUND AND FIXED 2026-09-05, on the first real run of this job's
   Import step: SQL Server's EXECUTE statement only accepts a constant or a
   plain variable for each parameter -- never an expression like string
   concatenation. The original Step 1 passed `@Folder + N'...'` directly as
   each usp_Import_All parameter, which fails to even PARSE ("Incorrect
   syntax near '+'"), regardless of what @Folder's value is -- reproduced
   identically with both the original path and a freshly-edited one, so
   this was never about the path. Each file path is now built into its own
   variable first, then passed as a plain variable. Also added: @Folder is
   normalized to always end in a backslash, since a path edited directly in
   SSMS's Job Step Properties dialog (as happened here) is easy to paste
   without the trailing separator.

   NAMING CORRECTION vs. the guide's own example text: the two date-stamped
   exports in this folder are actually space-separated, not underscored --
   "Export Entitlements YYYY-MM-DD.csv" and "Export Local Group Members
   YYYY-MM-DD.csv" -- not "Export_Entitlements_..."/"Export_Local_Group_
   Members_...". Step 1 below builds both dynamically from today's date to
   match what's actually there. If your real export process ever changes
   this naming, update the two string-concatenation lines in Step 1 to match.

   DOCUMENTATION GAP FOUND ALONGSIDE THIS: the guide's own Process 1 / Step 1
   text says "six current Privilege Cloud CSV exports" and doesn't mention
   the seventh -- Local Group Members, imported via usp_Import_PC_GroupMembers
   -- even though usp_Import_All has always required it. Fixed in the guide
   at the same time as this script was added.

   This script is guarded (drops and recreates the job if it already exists)
   so it can be safely re-run.
   ============================================================================ */

USE msdb;
GO

IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = 'BlueTrack ($(DatabaseName)) - Import and Load')
BEGIN
    EXEC msdb.dbo.sp_delete_job @job_name = 'BlueTrack ($(DatabaseName)) - Import and Load';
END
GO

DECLARE @JobId BINARY(16);

EXEC msdb.dbo.sp_add_job
    @job_name = 'BlueTrack ($(DatabaseName)) - Import and Load',
    @enabled = 1,
    @description = 'Nightly Import (staging refresh from Privilege Cloud + Self-Hosted EVD) followed by Load (usp_RunFullLoad) for the BlueTrack database. See Database/Import_Load_Process_Guide.docx.',
    @job_id = @JobId OUTPUT;

-- Step 1: Import. Builds the two date-stamped file paths from today's date
-- to match the real naming pattern (see header note above); the other five
-- exports use fixed filenames that get overwritten in place on each export.
EXEC msdb.dbo.sp_add_jobstep
    @job_id = @JobId,
    @step_id = 1,
    @step_name = 'Import',
    @subsystem = 'TSQL',
    @database_name = N'$(DatabaseName)',
    @on_success_action = 3,   -- go to next step
    @on_fail_action = 2,      -- quit the job, reporting failure
    @command = N'
DECLARE @Today DATE = CAST(SYSDATETIME() AS DATE);
DECLARE @TodayStr NVARCHAR(10) = CONVERT(NVARCHAR(10), @Today, 23);   -- YYYY-MM-DD
DECLARE @Folder NVARCHAR(400) = N''C:\Code\aPePAS\Output'';
IF RIGHT(@Folder, 1) <> N''\'' SET @Folder = @Folder + N''\'';

-- EXEC''s own parameter list only accepts a constant or a plain variable
-- for each argument, never an expression like concatenation (confirmed
-- 2026-09-05: "Incorrect syntax near ''+''" -- a genuine, pre-existing bug,
-- reproduced with both the original and a newly-edited folder path, so
-- unrelated to whichever path is actually in use). Each file path is
-- built into its own variable first, then passed as a plain variable below.
DECLARE @PlatformsFile     NVARCHAR(500) = @Folder + N''Export_PlatformsList.csv'';
DECLARE @UsersFile         NVARCHAR(500) = @Folder + N''Export_UsersList.csv'';
DECLARE @GroupsFile        NVARCHAR(500) = @Folder + N''Export_GroupsList.csv'';
DECLARE @GroupMembersFile  NVARCHAR(500) = @Folder + N''Export Local Group Members '' + @TodayStr + N''.csv'';
DECLARE @SafesFile         NVARCHAR(500) = @Folder + N''Export_SafesList.csv'';
DECLARE @AccountsFile      NVARCHAR(500) = @Folder + N''Export_AccountsList.csv'';
DECLARE @EntitlementsFile  NVARCHAR(500) = @Folder + N''Export Entitlements '' + @TodayStr + N''.csv'';

EXEC usp_Import_All
    @EVDDatabaseName = N''CyberArkSH'',
    @PlatformsFile = @PlatformsFile,
    @UsersFile = @UsersFile,
    @GroupsFile = @GroupsFile,
    @GroupMembersFile = @GroupMembersFile,
    @SafesFile = @SafesFile,
    @AccountsFile = @AccountsFile,
    @EntitlementsFile = @EntitlementsFile,
    @EntitlementsExportDate = @Today;
';

-- Step 2: Load.
EXEC msdb.dbo.sp_add_jobstep
    @job_id = @JobId,
    @step_id = 2,
    @step_name = 'Load',
    @subsystem = 'TSQL',
    @database_name = N'$(DatabaseName)',
    @on_success_action = 1,   -- quit the job, reporting success
    @on_fail_action = 2,      -- quit the job, reporting failure
    @command = N'EXEC usp_RunFullLoad;';

-- Schedule: nightly at 2:00 AM.
EXEC msdb.dbo.sp_add_schedule
    @schedule_name = 'BlueTrack ($(DatabaseName)) Nightly 2AM',
    @freq_type = 4,           -- daily
    @freq_interval = 1,       -- every 1 day
    @active_start_time = 020000;

EXEC msdb.dbo.sp_attach_schedule
    @job_id = @JobId,
    @schedule_name = 'BlueTrack ($(DatabaseName)) Nightly 2AM';

EXEC msdb.dbo.sp_add_jobserver
    @job_id = @JobId,
    @server_name = N'(local)';
GO

PRINT 'SQL Agent job "BlueTrack ($(DatabaseName)) - Import and Load" created: Import (Step 1) then Load (Step 2), nightly at 2:00 AM.';
