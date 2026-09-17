/* ============================================================================
   35_BlueTrack_DiscoveredAccountWorkflow.sql

   RUN THIS AFTER 01-34. Guarded (column-exists checks, NOT EXISTS checks) --
   safe to re-run, per D-58's normal incremental-script convention.

   AD Account Discovery feature (2026-09-16), follow-up requested directly:
   a workflow for moving a discovered account into CyberArk onboarding.
   web.discovered_account gains Status ('New'/'Accepted'/'Dismissed',
   default 'New' so every existing row keeps its current meaning) and
   ResolvedAccountKey (set only on Accept, pointing at the real
   dbo.fact_account row this candidate became) plus ReviewedBy/ReviewedDate.

   New permission ManageDiscoveredAccounts, separate from the existing
   read-only ViewDiscoveredAccounts (mirrors the ViewDeploymentInfo/
   TriggerBackup split) -- Accept writes a real dbo.fact_account row, a
   bigger deal than viewing the report. Granted to Admin only for now,
   same as every permission this feature has added so far.

   See 36_BlueTrack_FixAutoAdvanceForDiscoveredAccounts.sql for the separate,
   more consequential fix this workflow depends on being safe.
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('web.discovered_account', 'Status') IS NULL
BEGIN
    ALTER TABLE web.discovered_account ADD Status NVARCHAR(20) NOT NULL CONSTRAINT DF_discovered_account_Status DEFAULT 'New';
END
GO

IF COL_LENGTH('web.discovered_account', 'ResolvedAccountKey') IS NULL
BEGIN
    ALTER TABLE web.discovered_account ADD ResolvedAccountKey BIGINT NULL REFERENCES dbo.fact_account(AccountKey);
END
GO

IF COL_LENGTH('web.discovered_account', 'ReviewedBy') IS NULL
BEGIN
    ALTER TABLE web.discovered_account ADD ReviewedBy INT NULL REFERENCES web.app_user(UserKey);
END
GO

IF COL_LENGTH('web.discovered_account', 'ReviewedDate') IS NULL
BEGIN
    ALTER TABLE web.discovered_account ADD ReviewedDate DATETIME2 NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM web.app_permission WHERE PermissionName = 'ManageDiscoveredAccounts')
BEGIN
    INSERT INTO web.app_permission (PermissionName, Description)
    VALUES ('ManageDiscoveredAccounts', 'Accept a Discovered Account into onboarding tracking, or dismiss it');
END
GO

DECLARE @AdminRoleKey INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Admin');
DECLARE @ManageDiscoveredAccountsKey INT = (SELECT PermissionKey FROM web.app_permission WHERE PermissionName = 'ManageDiscoveredAccounts');
IF @AdminRoleKey IS NOT NULL AND NOT EXISTS (SELECT 1 FROM web.role_permission WHERE RoleKey = @AdminRoleKey AND PermissionKey = @ManageDiscoveredAccountsKey)
BEGIN
    INSERT INTO web.role_permission (RoleKey, PermissionKey) VALUES (@AdminRoleKey, @ManageDiscoveredAccountsKey);
END
GO

PRINT '35_BlueTrack_DiscoveredAccountWorkflow.sql complete.';
