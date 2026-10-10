:on error exit
/* ============================================================================
   02_BlueTrack_GrantAppServiceAccountAccess.sql

   D-195: this folder (Database/Manual) holds the scripts App/Migrator never
   runs -- it reads only the top level of Database/. Each is run by hand
   (sqlcmd or SSMS) or by Deploy/Install-BlueTrack.ps1.

   D-167: the `:on error exit` line above is load-bearing, not decoration --
   sqlcmd's own default behavior is to print an error and keep running the
   REMAINING GO-separated batches anyway, meaning a missing-login failure
   (see below) would print a clear error, then keep going, hit several more
   confusing native SQL errors, and still print "...complete." at the very
   end -- exactly what happened live the first time this script hit that
   exact failure, making it easy to miss the real (first, actionable) error
   under the noise and wrongly assume the run succeeded.

   Not a Migrator script, for any environment. Unlike the msdb scripts
   beside it, this isn't a structural DbUp limitation (this script never leaves the target
   database, so DbUp's own post-script journal write would succeed fine).
   It's manual for the same reason the backup-status grant (D-107) is treated as a manual,
   DBA-run action despite having no technical need to be: granting a real
   service account real database permissions is a deliberate action a DBA
   should consciously run and review, not something that silently happens
   as one step of an unattended `dotnet run App/Migrator` sequence -- and
   the __TARGET_ACCOUNT__ placeholder below would hard-fail CREATE USER
   outright (no login literally named "__TARGET_ACCOUNT__" exists) if this
   ever did run unedited through an automated sequence, unlike
   06_BlueTrack_DevFakeAuthUserMapping.sql's own placeholder, which is a harmless
   no-op string value, not an object name a statement tries to resolve.

   Confirmed a real, previously-undocumented gap (2026-09-24, D-30's own
   design comment -- "Windows Integrated Authentication to SQL Server, no
   SQL login, no standing secret" -- names the PRINCIPLE but this repo had
   no script or documented permission set turning it into an actual grant):
   which account the App Pool's `ApplicationPoolIdentity` presents depends
   on where SQL Server runs (D-171):
     - Same machine as IIS (the normal single-server layout): its own
       virtual account, `IIS APPPOOL\<pool>`. Confirmed live on two hosts
       from the SID on SQL Server's 18456 events. TRAP: SQL Server's
       "Login failed for user '...'" message names the computer account
       (`DOMAIN\HOSTNAME$`) here, but a login for that is never matched --
       D-164 and D-170 both granted it and the error continued.
     - Another machine: the *computer account* (`DOMAIN\HOSTNAME$`) --
       standard Windows behavior, not yet tested on a BlueTrack host.
   Deploy/Install-BlueTrack.ps1's Db.AppPoolAccess step works out the
   account and runs a filled-in temp copy of this file for you. A domain
   service account (or gMSA) used as
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
        sqlcmd -S <server> -C -d BlueTrack -i 02_BlueTrack_GrantAppServiceAccountAccess.sql

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
   the default `ApplicationPoolIdentity` case, `IIS APPPOOL\<pool>` with
   SQL Server on the same machine, or the computer account
   `DOMAIN\HOSTNAME$` with SQL Server elsewhere) before running this file.
   ============================================================================ */

-- D-167: confirmed live that skipping the CREATE LOGIN prerequisite above
-- doesn't fail loudly and obviously here -- re-running this script against
-- an account with a database user but no server login can silently leave
-- things in a broken, hard-to-diagnose state, still failing later with
-- SQL Server's own generic "Login failed" / "Could not find a login
-- matching the name provided" once the app actually tries to connect.
-- Check for the prerequisite explicitly and fail loudly, here, instead.
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = '__TARGET_ACCOUNT__')
BEGIN
    THROW 50000, 'No SQL Server login exists yet for __TARGET_ACCOUNT__. Run CREATE LOGIN [__TARGET_ACCOUNT__] FROM WINDOWS; first (see this script''s own header, prerequisite 1), then re-run this script.', 1;
END
GO

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

PRINT '02_BlueTrack_GrantAppServiceAccountAccess.sql complete.';
