/* ============================================================================
   10_BlueTrack_AnalystRiskReport.sql

   RUN THIS AFTER 01-09. Guarded -- safe to re-run.

   D-208: the Analyst role can see the Risk Score report (ViewRiskReport),
   decided by the user on 2026-10-13. Analysts already manage Targets and
   Access Groups, whose scores the report shows; until now only Admin held
   the permission. Applies to existing installs too. Skipped if there's no
   role named Analyst (an admin may have renamed or removed it).
   ============================================================================ */

USE $DatabaseName$;
GO

INSERT INTO web.role_permission (RoleKey, PermissionKey)
SELECT r.AppRoleKey, p.PermissionKey
FROM web.app_role r
JOIN web.app_permission p ON p.PermissionName = 'ViewRiskReport'
WHERE r.RoleName = 'Analyst'
  AND NOT EXISTS (SELECT 1 FROM web.role_permission rp WHERE rp.RoleKey = r.AppRoleKey AND rp.PermissionKey = p.PermissionKey);
GO
