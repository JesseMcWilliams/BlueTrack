/* ============================================================================
   25_BlueTrack_DeploymentInfoPermissionSeed.sql

   RUN THIS AFTER 01-23 (note: 24_BlueTrack_DefaultRoleSeed.sql is a separate,
   still-unmerged branch's script number -- if both land, whichever merges
   second needs renumbering so the sequence stays unique).

   Adds the ViewDeploymentInfo permission (Design_Admin_Deployment_Management.md,
   D-95/D-96/D-97, Part 3) for already-deployed environments -- 06's own
   INSERT INTO web.app_permission already covers a fresh install, but that
   script drops and recreates the table (its own header says so), so it's
   not safe to re-run against a real environment with live data. Also grants
   it to the bootstrap Admin role, matching 07_BlueTrack_WebInterface_Seed.sql's
   own "every confirmed permission" bootstrap intent -- new permissions added
   after that one-time bootstrap don't automatically reach Admin otherwise.

   Guarded -- safe to re-run.
   ============================================================================ */

USE $DatabaseName$;
GO

IF NOT EXISTS (SELECT 1 FROM web.app_permission WHERE PermissionName = 'ViewDeploymentInfo')
BEGIN
    INSERT INTO web.app_permission (PermissionName, Description)
    VALUES ('ViewDeploymentInfo', 'View deployment/environment info, health checks, and backup status');
END
GO

DECLARE @ViewDeploymentInfoPermissionKey INT = (SELECT PermissionKey FROM web.app_permission WHERE PermissionName = 'ViewDeploymentInfo');
DECLARE @AdminRoleKey INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Admin');

IF @AdminRoleKey IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM web.role_permission
    WHERE RoleKey = @AdminRoleKey AND PermissionKey = @ViewDeploymentInfoPermissionKey
)
BEGIN
    INSERT INTO web.role_permission (RoleKey, PermissionKey)
    VALUES (@AdminRoleKey, @ViewDeploymentInfoPermissionKey);
END
GO

PRINT 'ViewDeploymentInfo permission seeded and granted to Admin.';
