/* ============================================================================
   37_BlueTrack_RiskExceptionSegregationOfDuties.sql

   RUN THIS AFTER 01-36. Guarded (column-exists check) -- safe to re-run,
   per D-58's normal incremental-script convention. Numbered 37, not 31 --
   originally written as 31/32 on a branch that diverged before AD Account
   Discovery (D-137/D-138) claimed 31-36 on main; renumbered to the next
   free slot when merging, per this project's own coordinated-numbering
   convention (see Database/README.md's note on 27/28).

   Segregation of duties on Risk Exception approval, requested directly:
   a role-based control so the same person who approved an exception
   (web.risk_exception.ApprovedBy) cannot also be the one who links it to
   an account's progress record (AccountProgressController.Update). Off by
   default -- some organizations don't have enough staff to separate the
   two roles, so this is an admin-configurable global toggle on the
   Global Application Configuration page, not a hard-coded requirement.
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('web.app_config', 'EnforceRiskExceptionSegregationOfDuties') IS NULL
BEGIN
    ALTER TABLE web.app_config
        ADD EnforceRiskExceptionSegregationOfDuties BIT NOT NULL CONSTRAINT DF_app_config_EnforceRiskExceptionSoD DEFAULT (0);
END
GO

-- New permission, added after 09_BlueTrack_WebSeed.sql's own one-time
-- blanket grant already ran -- explicit catalog insert + explicit grant to
-- the bootstrap Admin role, same as every permission added since
-- (D-98/D-115/D-118/D-119/D-120). Gates the new detective/audit report
-- (Option C) that lists every same-person approve+link case, independent
-- of whether the enforcement toggle above is or ever was on.
IF NOT EXISTS (SELECT 1 FROM web.app_permission WHERE PermissionName = 'ViewRiskExceptionSodReport')
BEGIN
    INSERT INTO web.app_permission (PermissionName, Description)
    VALUES ('ViewRiskExceptionSodReport', 'View the Risk Exception segregation-of-duties report (same person approved and linked an exception)');
END
GO

DECLARE @AdminRoleKey INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Admin');
DECLARE @ViewRiskExceptionSodReportKey INT = (SELECT PermissionKey FROM web.app_permission WHERE PermissionName = 'ViewRiskExceptionSodReport');
IF @AdminRoleKey IS NOT NULL AND NOT EXISTS (SELECT 1 FROM web.role_permission WHERE RoleKey = @AdminRoleKey AND PermissionKey = @ViewRiskExceptionSodReportKey)
BEGIN
    INSERT INTO web.role_permission (RoleKey, PermissionKey) VALUES (@AdminRoleKey, @ViewRiskExceptionSodReportKey);
END
GO

PRINT '37_BlueTrack_RiskExceptionSegregationOfDuties.sql complete.';
