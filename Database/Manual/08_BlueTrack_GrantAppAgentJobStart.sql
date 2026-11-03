:on error exit
/* ============================================================================
   08_BlueTrack_GrantAppAgentJobStart.sql

   D-195: this folder (Database/Manual) holds the scripts App/Migrator never
   runs. Run by Deploy/Install-BlueTrack.ps1 (step Db.AppPoolAccess, right
   after 02_BlueTrack_GrantAppServiceAccountAccess.sql), or by hand:

       sqlcmd -S <server> -C -i 08_BlueTrack_GrantAppAgentJobStart.sql

   D-302: a Pull now that succeeds starts the nightly "BlueTrack (<database>)
   - Import and Load" job (04_BlueTrack_ScheduleImportLoadJob.sql). The
   app's account can't run the Import and Load itself (it truncates staging
   and bulk-loads Privilege Cloud files: rights 02 doesn't give), so it
   starts the job, which runs as the SQL Server Agent's account.

   Starting a job someone else owns needs msdb's SQLAgentOperatorRole: its
   members can start, stop, enable and disable every local job on this
   instance and delete their history, but can't create or change jobs they
   don't own. On an instance shared with other applications, that is wider
   than BlueTrack needs; to take it back there, run
   ALTER ROLE SQLAgentOperatorRole DROP MEMBER [<account>]; in msdb. Pull
   now then says the job couldn't be started, and the load runs at the next
   nightly job.
   The audit log purge job (05) is a local job too: starting it early
   purges no more than RetentionDays allows, and the purge is sealed
   (D-295).

   __TARGET_ACCOUNT__ is a placeholder, the same account as in 02: the
   installer fills it in on a temp copy; by hand, replace it first. The
   login must already exist (02's prerequisite 1).
   ============================================================================ */

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = '__TARGET_ACCOUNT__')
BEGIN
    THROW 50000, 'No SQL Server login exists yet for __TARGET_ACCOUNT__. Run 02_BlueTrack_GrantAppServiceAccountAccess.sql''s prerequisite (CREATE LOGIN) first.', 1;
END
GO

USE msdb;
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = '__TARGET_ACCOUNT__')
BEGIN
    CREATE USER [__TARGET_ACCOUNT__] FOR LOGIN [__TARGET_ACCOUNT__];
END
GO

ALTER ROLE SQLAgentOperatorRole ADD MEMBER [__TARGET_ACCOUNT__];
GO

PRINT '08_BlueTrack_GrantAppAgentJobStart.sql complete.';
