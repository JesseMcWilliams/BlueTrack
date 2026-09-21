/* ============================================================================
   40_BlueTrack_ScheduleAuditLogPurgeJob.sql

   NEVER run this through App/Migrator -- like 14/38 before it, it targets
   msdb, not the target database, and DbUp's own post-script journal write
   would fail against msdb the same way documented in
   14_BlueTrack_ScheduleImportLoadJob.sql's own header. Run manually via
   sqlcmd:

       sqlcmd -S <server> -C -v DatabaseName="BlueTrack" -i 40_BlueTrack_ScheduleAuditLogPurgeJob.sql

   Creates a SQL Server Agent job, "BlueTrack ($(DatabaseName)) - Audit Log
   Purge", one step calling dbo.usp_PurgeAuditLog (39_BlueTrack_AuditLogPurgeProcedure.sql,
   which must already exist -- run through App/Migrator normally, it's a
   plain stored procedure in the target database, not msdb). Runs nightly
   at 3:00 AM -- one hour after the existing Import+Load job (2:00 AM,
   script 14), so a long Import+Load run never overlaps the purge.

   The job/schedule names embed the substituted database name, same
   reasoning as script 14's own header: msdb.dbo.sysjobs/sysschedules are
   instance-global, not scoped per target database, so BlueTrack and
   BlueTrackTest (or any other environment sharing this SQL Server
   instance) get distinctly-named jobs rather than colliding.

   This script is guarded (drops and recreates the job if it already
   exists) so it can be safely re-run. Does NOT enable audit log purging
   on its own -- usp_PurgeAuditLog itself no-ops (logging a 'Skipped' row)
   until an admin sets a real value for RetentionDays on the Global
   Application Configuration page. Installing this job is what makes that
   setting actually do something, going forward.
   ============================================================================ */

USE msdb;
GO

IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = 'BlueTrack ($(DatabaseName)) - Audit Log Purge')
BEGIN
    EXEC msdb.dbo.sp_delete_job @job_name = 'BlueTrack ($(DatabaseName)) - Audit Log Purge';
END
GO

DECLARE @JobId BINARY(16);

EXEC msdb.dbo.sp_add_job
    @job_name = 'BlueTrack ($(DatabaseName)) - Audit Log Purge',
    @enabled = 1,
    @description = 'Nightly audit_event/audit_field_change purge (usp_PurgeAuditLog) for the BlueTrack database, per web.audit_config.RetentionDays. See Design Documents/Design_Audit_Logging.md and Design_Operations_Guide.md.',
    @job_id = @JobId OUTPUT;

EXEC msdb.dbo.sp_add_jobstep
    @job_id = @JobId,
    @step_id = 1,
    @step_name = 'Purge',
    @subsystem = 'TSQL',
    @database_name = N'$(DatabaseName)',
    @on_success_action = 1,   -- quit the job, reporting success
    @on_fail_action = 2,      -- quit the job, reporting failure
    @command = N'EXEC dbo.usp_PurgeAuditLog;';

EXEC msdb.dbo.sp_add_schedule
    @schedule_name = 'BlueTrack ($(DatabaseName)) Nightly 3AM',
    @freq_type = 4,           -- daily
    @freq_interval = 1,       -- every 1 day
    @active_start_time = 030000;

EXEC msdb.dbo.sp_attach_schedule
    @job_id = @JobId,
    @schedule_name = 'BlueTrack ($(DatabaseName)) Nightly 3AM';

EXEC msdb.dbo.sp_add_jobserver
    @job_id = @JobId,
    @server_name = N'(local)';
GO

PRINT 'SQL Agent job "BlueTrack ($(DatabaseName)) - Audit Log Purge" created: nightly at 3:00 AM.';
