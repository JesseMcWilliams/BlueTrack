/* ============================================================================
   09_BlueTrack_ScheduleImportLoadJob.sql

   RUN THIS AFTER 01-08, once Import and Load have both been run manually
   at least once and confirmed working (see Import_Load_Process_Guide.docx).

   Creates a SQL Server Agent job, "BlueTrack - Import and Load", with two
   steps -- Import, then Load -- matching the guide's own "Recommended
   Operational Cadence" section. Runs nightly at 2:00 AM.

   REAL VALUES USED BELOW (confirmed 2026-09-01, not placeholders):
     - EVD database name: CyberArkSH
     - Privilege Cloud export folder: C:\Code\BlueTrack\Reference\PrivilegedCloud
       (a local path, not UNC -- confirm the SQL Server *service account*,
       not your own login, has read access to it; see Prerequisites in the guide)

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

IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = 'BlueTrack - Import and Load')
BEGIN
    EXEC msdb.dbo.sp_delete_job @job_name = 'BlueTrack - Import and Load';
END
GO

DECLARE @JobId BINARY(16);

EXEC msdb.dbo.sp_add_job
    @job_name = 'BlueTrack - Import and Load',
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
    @database_name = 'BlueTrack',
    @on_success_action = 3,   -- go to next step
    @on_fail_action = 2,      -- quit the job, reporting failure
    @command = N'
DECLARE @Today DATE = CAST(SYSDATETIME() AS DATE);
DECLARE @TodayStr NVARCHAR(10) = CONVERT(NVARCHAR(10), @Today, 23);   -- YYYY-MM-DD
DECLARE @Folder NVARCHAR(400) = N''C:\Code\BlueTrack\Reference\PrivilegedCloud\'';

EXEC usp_Import_All
    @EVDDatabaseName = N''CyberArkSH'',
    @PlatformsFile = @Folder + N''Export_PlatformsList.csv'',
    @UsersFile = @Folder + N''Export_UsersList.csv'',
    @GroupsFile = @Folder + N''Export_GroupsList.csv'',
    @GroupMembersFile = @Folder + N''Export Local Group Members '' + @TodayStr + N''.csv'',
    @SafesFile = @Folder + N''Export_SafesList.csv'',
    @AccountsFile = @Folder + N''Export_AccountsList.csv'',
    @EntitlementsFile = @Folder + N''Export Entitlements '' + @TodayStr + N''.csv'',
    @EntitlementsExportDate = @Today;
';

-- Step 2: Load.
EXEC msdb.dbo.sp_add_jobstep
    @job_id = @JobId,
    @step_id = 2,
    @step_name = 'Load',
    @subsystem = 'TSQL',
    @database_name = 'BlueTrack',
    @on_success_action = 1,   -- quit the job, reporting success
    @on_fail_action = 2,      -- quit the job, reporting failure
    @command = N'EXEC usp_RunFullLoad;';

-- Schedule: nightly at 2:00 AM.
EXEC msdb.dbo.sp_add_schedule
    @schedule_name = 'BlueTrack Nightly 2AM',
    @freq_type = 4,           -- daily
    @freq_interval = 1,       -- every 1 day
    @active_start_time = 020000;

EXEC msdb.dbo.sp_attach_schedule
    @job_id = @JobId,
    @schedule_name = 'BlueTrack Nightly 2AM';

EXEC msdb.dbo.sp_add_jobserver
    @job_id = @JobId,
    @server_name = N'(local)';
GO

PRINT 'SQL Agent job "BlueTrack - Import and Load" created: Import (Step 1) then Load (Step 2), nightly at 2:00 AM.';
