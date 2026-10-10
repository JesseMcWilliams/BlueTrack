/* ============================================================================
   03_BlueTrack_GrantBackupStatusReaderRole.sql

   D-195: this folder (Database/Manual) holds the scripts App/Migrator never
   runs -- it reads only the top level of Database/. Each is run by hand
   (sqlcmd or SSMS) or by Deploy/Install-BlueTrack.ps1.

   Targets msdb, not the BlueTrack database (see
   04_BlueTrack_ScheduleImportLoadJob.sql for why that can't run through
   DbUp), and is a DBA-owned permission grant anyway. Run manually via
   sqlcmd, or generate a filled-in copy from the Group / Role Mapping admin
   page's "Generate db_backupstatus_reader Script" button (targets a
   resolved AD group's account name automatically):

       sqlcmd -S <server> -C -i 03_BlueTrack_GrantBackupStatusReaderRole.sql

   D-107 (Design_Admin-Deployment-Management.md, Part 3.3) designed this
   role/grant set but never turned it into a runnable file -- this is that
   file. Confirmed real, flagged gap: this app's own least-privileged SQL
   service account has no read access to msdb.dbo.backupset by default, so
   the Deployment page's backup-status query fails until a DBA runs
   something like this once per environment. A role (not a one-off grant)
   so the permission is assignable to more than just the app's own service
   account -- e.g. a separate human admin login checking status via SSMS.

   The role/grants are guarded (safe to re-run). The final ALTER ROLE ADD
   MEMBER line is NOT guarded -- SQL Server's own ALTER ROLE ADD MEMBER is
   itself idempotent (adding an existing member is a no-op, not an error).

   __TARGET_ACCOUNT__ below is a placeholder, substituted by
   GroupRoleMappingsController's script-generator endpoint before download
   -- never edited by hand in this tracked file. If running this file
   directly (not via the generated download), replace __TARGET_ACCOUNT__
   yourself with the login/account that should be able to read backup
   status (e.g. 'DOMAIN\BlueTrackAppPoolAccount' or a resolved AD group).
   That account/group must already exist as a SQL Server login on this
   instance -- granting msdb role membership doesn't create the login
   itself; if it doesn't exist yet, ALTER ROLE below fails with "principal
   ... does not exist", and a login needs to be created first.
   ============================================================================ */

USE msdb;
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'db_backupstatus_reader' AND type = 'R')
BEGIN
    CREATE ROLE db_backupstatus_reader;
END
GO

GRANT SELECT ON msdb.dbo.backupset TO db_backupstatus_reader;
GRANT SELECT ON msdb.dbo.backupmediafamily TO db_backupstatus_reader;
GRANT SELECT ON msdb.dbo.backupfile TO db_backupstatus_reader;
GO

ALTER ROLE db_backupstatus_reader ADD MEMBER [__TARGET_ACCOUNT__];
GO

PRINT '03_BlueTrack_GrantBackupStatusReaderRole.sql complete.';
