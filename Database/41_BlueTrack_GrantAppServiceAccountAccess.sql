/* ============================================================================
   41_BlueTrack_GrantAppServiceAccountAccess.sql

   NEVER run this through App/Migrator, for any environment -- always
   excluded (see App/Migrator/Program.cs). Unlike 00/14/38/40, this isn't a
   structural DbUp limitation (this script never leaves the target
   database, so DbUp's own post-script journal write would succeed fine).
   It's excluded for the same reason 38 (D-107) is treated as a manual,
   DBA-run action despite having no technical need to be: granting a real
   service account real database permissions is a deliberate action a DBA
   should consciously run and review, not something that silently happens
   as one step of an unattended `dotnet run App/Migrator` sequence -- and
   the __TARGET_ACCOUNT__ placeholder below would hard-fail CREATE USER
   outright (no login literally named "__TARGET_ACCOUNT__" exists) if this
   ever did run unedited through an automated sequence, unlike
   11_BlueTrack_DevFakeAuthSeed.sql's own placeholder, which is a harmless
   no-op string value, not an object name a statement tries to resolve.

   Confirmed a real, previously-undocumented gap (2026-09-24, D-30's own
   design comment -- "Windows Integrated Authentication to SQL Server, no
   SQL login, no standing secret" -- names the PRINCIPLE but this repo had
   no script or documented permission set turning it into an actual grant):
   on a real IIS deployment, the App Pool's `ApplicationPoolIdentity`
   virtual account authenticates to SQL Server over the network as the
   *computer account* (`DOMAIN\HOSTNAME$`), not as the named App Pool
   identity itself -- confirmed directly via a live SqlException
   ("Login failed for user 'DOMAIN\HOSTNAME$'") on a real host with no
   login for that account yet. A domain service account (or gMSA) used as
   the App Pool identity instead would authenticate as itself, the same
   way; either way, something needs this exact grant.

   Prerequisites, same two steps D-107/Design_Operations_Guide.md's
   `db_backupstatus_reader` section already documents (a SQL Server login
   is not automatically a database user, and neither is automatically
   created for you):
   1. A SQL Server login on this instance, if one doesn't already exist:
        CREATE LOGIN [DOMAIN\AccountName] FROM WINDOWS;
      (needs `DOMAIN\AccountName` form, not a UPN -- resolve first if
      that's all you have.)
   2. Run this script against the target database itself (not msdb):
        sqlcmd -S <server> -C -d BlueTrack -i 41_BlueTrack_GrantAppServiceAccountAccess.sql

   Least-privilege, not db_owner (D-30's own stated principle, finally
   turned into an actual grant list): `db_datareader` + `db_datawriter`
   cover the ad hoc SELECT/INSERT/UPDATE/DELETE this app's Dapper-based
   repositories do directly against `dbo`/`web` tables; EXECUTE on both
   schemas covers the stored procedures the nightly Import+Load job and
   AD Account Discovery/notification background services call
   (`usp_Import_*`, `usp_RunFullLoad`, `usp_PurgeAuditLog`, etc.) that
   plain db_datareader/db_datawriter membership does not itself grant.
   No DDL, no permission-granting, no cross-database, no server-level
   rights beyond the login itself.

   __TARGET_ACCOUNT__ below is a placeholder -- replace it by hand with
   the real login/account (e.g. `DOMAIN\BlueTrackAppPoolAccount`, or, for
   the default `ApplicationPoolIdentity` case, the deployment server's own
   computer account, `DOMAIN\HOSTNAME$`) before running this file.
   ============================================================================ */

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = '__TARGET_ACCOUNT__')
BEGIN
    CREATE USER [__TARGET_ACCOUNT__] FOR LOGIN [__TARGET_ACCOUNT__];
END
GO

ALTER ROLE db_datareader ADD MEMBER [__TARGET_ACCOUNT__];
ALTER ROLE db_datawriter ADD MEMBER [__TARGET_ACCOUNT__];
GO

GRANT EXECUTE ON SCHEMA::dbo TO [__TARGET_ACCOUNT__];
GRANT EXECUTE ON SCHEMA::web TO [__TARGET_ACCOUNT__];
GO

PRINT '41_BlueTrack_GrantAppServiceAccountAccess.sql complete.';
