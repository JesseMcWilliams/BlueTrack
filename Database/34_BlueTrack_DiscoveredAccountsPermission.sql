/* ============================================================================
   34_BlueTrack_DiscoveredAccountsPermission.sql

   RUN THIS AFTER 01-33. Guarded (NOT EXISTS checks) -- safe to re-run, per
   D-58's normal incremental-script convention.

   AD Account Discovery feature (2026-09-16), Phase D: ViewDiscoveredAccounts
   gates the new read-only Discovered Accounts report. Granted to Admin only
   for now, matching this same file's own precedent for ViewRiskReport
   (Database/20) -- Analyst inclusion wasn't assumed here and is a follow-up
   decision, not a default silently applied.
   ============================================================================ */

USE $DatabaseName$;
GO

IF NOT EXISTS (SELECT 1 FROM web.app_permission WHERE PermissionName = 'ViewDiscoveredAccounts')
BEGIN
    INSERT INTO web.app_permission (PermissionName, Description)
    VALUES ('ViewDiscoveredAccounts', 'View the Discovered Accounts report (AD accounts not yet onboarded into CyberArk)');
END
GO

DECLARE @AdminRoleKey INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Admin');
DECLARE @ViewDiscoveredAccountsKey INT = (SELECT PermissionKey FROM web.app_permission WHERE PermissionName = 'ViewDiscoveredAccounts');
IF @AdminRoleKey IS NOT NULL AND NOT EXISTS (SELECT 1 FROM web.role_permission WHERE RoleKey = @AdminRoleKey AND PermissionKey = @ViewDiscoveredAccountsKey)
BEGIN
    INSERT INTO web.role_permission (RoleKey, PermissionKey) VALUES (@AdminRoleKey, @ViewDiscoveredAccountsKey);
END
GO

PRINT '34_BlueTrack_DiscoveredAccountsPermission.sql complete.';
