/* ============================================================================
   24_BlueTrack_DefaultRoleSeed.sql

   RUN THIS AFTER 01-23.

   Design_Authorization_Model.md's "Example Permission Bundles" (Viewer/
   Analyst/Approver) were explicitly "illustrative... confirm before
   building as literal default rows" -- 07_BlueTrack_WebInterface_Seed.sql's
   own comment on the bootstrap Admin role says the same thing. The user
   confirmed directly, 2026-09-04: seed them for real, plus a new Auditor
   role (ViewAuditLog only -- called out separately from Viewer so a
   person can be given audit-log visibility without also getting
   Viewer's dashboard/account-progress read access), so an admin can map
   a real Local/AD group to one of these immediately instead of having to
   build the role definitions themselves first via the Roles &
   Permissions admin screen.

   Deliberately does NOT include ViewAuditLog in Viewer/Analyst/Approver
   (unlike Database/Test/01_BlueTrack_Test_DevFakeAuthMatrixSeed.sql's
   own same-named test fixture roles, which do) -- that would make the
   new Auditor role redundant with Viewer. These are two different,
   independently-confirmed bundles; don't assume they match.

   Guarded -- safe to re-run. Never edits 07's existing Admin role/seed.
   ============================================================================ */

USE $DatabaseName$;
GO


/* ============================================================================
   1. The four roles themselves.
   ============================================================================ */
INSERT INTO web.app_role (RoleName, Description)
SELECT v.RoleName, v.Description
FROM (VALUES
    ('Viewer', 'Read-only access to the dashboard.'),
    ('Analyst', 'Viewer plus editing Account Progress records.'),
    ('Approver', 'Analyst plus approving Risk Exceptions and confirming reconciliation matches.'),
    ('Auditor', 'Read-only access to the Audit Log Viewer only -- no dashboard/account-progress access implied.')
) AS v(RoleName, Description)
WHERE NOT EXISTS (SELECT 1 FROM web.app_role r WHERE r.RoleName = v.RoleName);
GO


/* ============================================================================
   2. Their permission bundles (Design_Authorization_Model.md's Example
      Permission Bundles table, confirmed as literal default rows).
   ============================================================================ */
DECLARE @ViewerRoleKey INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Viewer');
DECLARE @AnalystRoleKey INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Analyst');
DECLARE @ApproverRoleKey INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Approver');
DECLARE @AuditorRoleKey INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Auditor');

INSERT INTO web.role_permission (RoleKey, PermissionKey)
SELECT v.RoleKey, p.PermissionKey
FROM (VALUES
    (@ViewerRoleKey, 'ViewDashboard'),
    (@AnalystRoleKey, 'ViewDashboard'),
    (@AnalystRoleKey, 'EditAccountProgress'),
    (@ApproverRoleKey, 'ViewDashboard'),
    (@ApproverRoleKey, 'EditAccountProgress'),
    (@ApproverRoleKey, 'ConfirmReconciliation'),
    (@ApproverRoleKey, 'ApproveExceptions'),
    (@AuditorRoleKey, 'ViewAuditLog')
) AS v(RoleKey, PermissionName)
JOIN web.app_permission p ON p.PermissionName = v.PermissionName
WHERE NOT EXISTS (
    SELECT 1 FROM web.role_permission rp
    WHERE rp.RoleKey = v.RoleKey AND rp.PermissionKey = p.PermissionKey
);
GO

PRINT 'Default roles seeded: Viewer, Analyst, Approver, Auditor.';
