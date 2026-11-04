/* ============================================================================
   09_BlueTrack_PowerBiReaderRole.sql

   D-321: a database role that may read exactly what Power BI reports on
   (the objects Admin > Power BI lists, PowerBiRepository), and an account or
   group added to it. Run by Deploy/Install-BlueTrack.ps1's Db.PowerBiAccess
   step (-PowerBiReaderAccount DOMAIN\Group), which creates the SQL Server
   login first and fills in __TARGET_ACCOUNT__ in a temporary copy -- this
   tracked file is never edited. Run by hand, replace __TARGET_ACCOUNT__
   with a Windows account or group that already has a login on the instance:
       sqlcmd -S <server> -C -d <database> -i 09_BlueTrack_PowerBiReaderRole.sql

   The role and its grants are guarded (safe to re-run); ALTER ROLE ADD
   MEMBER is itself a no-op for a member already in it. Read only: no
   other table, no write, no EXECUTE. A test keeps the GRANT list equal to
   the objects Admin > Power BI lists.
   ============================================================================ */

:on error exit

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'__TARGET_ACCOUNT__')
BEGIN
    THROW 50000, 'No SQL Server login exists yet for __TARGET_ACCOUNT__. Run CREATE LOGIN [__TARGET_ACCOUNT__] FROM WINDOWS; first, then re-run this script.', 1;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'BlueTrack_PowerBI_Reader' AND type = 'R')
    CREATE ROLE BlueTrack_PowerBI_Reader;
GO

GRANT SELECT ON [dbo].[vw_powerbi_accounts] TO BlueTrack_PowerBI_Reader;
GRANT SELECT ON [dbo].[vw_powerbi_entitlements] TO BlueTrack_PowerBI_Reader;
GRANT SELECT ON [dbo].[fact_account_progress_history] TO BlueTrack_PowerBI_Reader;
GRANT SELECT ON [dbo].[dim_source_system] TO BlueTrack_PowerBI_Reader;
GRANT SELECT ON [dbo].[dim_platform] TO BlueTrack_PowerBI_Reader;
GRANT SELECT ON [dbo].[dim_safe] TO BlueTrack_PowerBI_Reader;
GRANT SELECT ON [web].[dim_application] TO BlueTrack_PowerBI_Reader;
GRANT SELECT ON [dbo].[dim_blueprint_stage] TO BlueTrack_PowerBI_Reader;
GRANT SELECT ON [dbo].[dim_progress_status] TO BlueTrack_PowerBI_Reader;
GRANT SELECT ON [dbo].[dim_risk_level] TO BlueTrack_PowerBI_Reader;
GRANT SELECT ON [dbo].[dim_account_type] TO BlueTrack_PowerBI_Reader;
GRANT SELECT ON [dbo].[dim_source_of_record] TO BlueTrack_PowerBI_Reader;
GRANT SELECT ON [dbo].[dim_user] TO BlueTrack_PowerBI_Reader;
GRANT SELECT ON [dbo].[dim_group] TO BlueTrack_PowerBI_Reader;
GRANT SELECT ON [dbo].[dim_permission] TO BlueTrack_PowerBI_Reader;
GRANT SELECT ON [dbo].[dim_date] TO BlueTrack_PowerBI_Reader;
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'__TARGET_ACCOUNT__')
    CREATE USER [__TARGET_ACCOUNT__] FOR LOGIN [__TARGET_ACCOUNT__];
GO

ALTER ROLE BlueTrack_PowerBI_Reader ADD MEMBER [__TARGET_ACCOUNT__];
GO

PRINT '09_BlueTrack_PowerBiReaderRole.sql complete.';
GO
