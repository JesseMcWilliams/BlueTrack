/* ============================================================================
   04_BlueTrack_ScheduleImportLoadJob.sql

   D-195: this folder (Database/Manual) holds the scripts App/Migrator never
   runs -- it reads only the top level of Database/. Each is run by hand
   (sqlcmd or SSMS) or by Deploy/Install-BlueTrack.ps1.

   Creates the SQL Server Agent job "BlueTrack (<database>) - Import and
   Load": one step, EXEC usp_RunNightlyImportLoad; (D-224, script 21): the
   Import (usp_Import_Configured), then the Load (usp_RunFullLoad) if the
   Import succeeded, then the daily snapshot for Reports > Trends
   (usp_RunDailySnapshot) whether or not they succeeded. A failure is still
   re-raised, so the job reports it. Each part has its own dbo.etl_run_log
   row. Nightly at 2:00 AM. Drops and recreates the job if it exists, so it
   can be re-run: re-running it (the installer's -Step Db.NightlyJob) is how
   an existing install gets the D-224 job. Before D-224 the job had two
   steps, Import and Load; that job keeps working after script 21 (the Load
   still takes the snapshot), but takes no snapshot when the Import fails.

   D-211 (Data Sources phase 3): the job no longer holds any source setting.
   usp_Import_Configured (Database/12_BlueTrack_CyberArkSourceConfig.sql)
   reads them from web.cyberark_source_config, which Admin > Data Sources
   edits. Before creating the job, this script writes that row from the
   values below **only when no row exists**; when one does, the page's
   settings are kept and a WARNING is printed if they differ from these.

   Values: Deploy/Install-BlueTrack.ps1 (Db.NightlyJob step) runs a temp
   copy with the four __TOKENS__ below replaced from its answers. To run it
   by hand, replace them yourself in a copy:
     __IMPORT_PC__ / __IMPORT_SH__   1 or 0 (at least one must be 1)
     __EXPORT_FOLDER__               the Privilege Cloud export folder, read by
                                     the SQL Server *service account*
     __EVD_DB__                      the Self-Hosted EVD database name
   then: sqlcmd -S <server> -C -v DatabaseName="BlueTrack" -i <copy>.sql
   (In SSMS, enable SQLCMD Mode, or start with :setvar DatabaseName "BlueTrack".)

   Lessons kept from earlier versions:
   - D-89: the job and schedule names embed $(DatabaseName), because msdb's
     job list is instance-wide; a fixed name would let a BlueTrackTest run
     replace the real BlueTrack's job on a shared instance.
   - Not a Migrator script: after USE msdb, DbUp tries to write its journal
     into msdb and reports failure even though the job was created.
   ============================================================================ */

USE [$(DatabaseName)];
GO

-- D-211: the settings row, written only when none exists.
DECLARE @ImportPrivilegeCloud BIT = __IMPORT_PC__;
DECLARE @ImportSelfHosted BIT = __IMPORT_SH__;
DECLARE @ExportFolderPath NVARCHAR(400) = NULLIF(N'__EXPORT_FOLDER__', N'');
DECLARE @EvdDatabaseName NVARCHAR(128) = NULLIF(N'__EVD_DB__', N'');

IF NOT EXISTS (SELECT 1 FROM web.cyberark_source_config WHERE CyberArkSourceConfigKey = 1)
BEGIN
    INSERT INTO web.cyberark_source_config (CyberArkSourceConfigKey, ImportPrivilegeCloud, ExportFolderPath, ImportSelfHosted, EvdDatabaseName)
    VALUES (1, @ImportPrivilegeCloud, @ExportFolderPath, @ImportSelfHosted, @EvdDatabaseName);
    PRINT 'CyberArk source settings saved (web.cyberark_source_config). From now on, change them on Admin > Data Sources.';
END
ELSE IF EXISTS (SELECT 1 FROM web.cyberark_source_config
                WHERE CyberArkSourceConfigKey = 1
                  AND (ImportPrivilegeCloud <> @ImportPrivilegeCloud OR ImportSelfHosted <> @ImportSelfHosted
                       OR (@ImportPrivilegeCloud = 1 AND ISNULL(ExportFolderPath, N'') <> ISNULL(@ExportFolderPath, N''))
                       OR (@ImportSelfHosted = 1 AND ISNULL(EvdDatabaseName, N'') <> ISNULL(@EvdDatabaseName, N''))))
BEGIN
    PRINT 'WARNING: the CyberArk source settings saved on Admin > Data Sources differ from the installer''s answers. The saved settings are kept; change them on the page if they''re wrong.';
END
GO

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
    @description = 'Nightly usp_RunNightlyImportLoad: Import (usp_Import_Configured: the CyberArk sources set on Admin > Data Sources), then Load (usp_RunFullLoad), then the daily snapshot (usp_RunDailySnapshot) even when the Import or Load failed (D-224).',
    @job_id = @JobId OUTPUT;

-- The one step (D-224). D-211: every source setting is read from web.cyberark_source_config.
EXEC msdb.dbo.sp_add_jobstep
    @job_id = @JobId,
    @step_id = 1,
    @step_name = 'Import, Load and Snapshot',
    @subsystem = 'TSQL',
    @database_name = N'$(DatabaseName)',
    @on_success_action = 1,   -- quit the job, reporting success
    @on_fail_action = 2,      -- quit the job, reporting failure
    @command = N'EXEC usp_RunNightlyImportLoad;';

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

PRINT 'SQL Agent job "BlueTrack ($(DatabaseName)) - Import and Load" created: usp_RunNightlyImportLoad (Import, Load, then the daily snapshot), nightly at 2:00 AM.';
