/* ============================================================================
   55_BlueTrack_AnalystFieldAccess.sql

   RUN THIS AFTER 01-54. Guarded -- safe to re-run.

   D-296 (the user's correction of 2026-10-21 to D-291): Analysts change an
   account's Blueprint Stage, Account Type and Application by hand, so those
   three fields need no permission beyond EditAccountProgress again. Each is
   cleared only while it still has the permission script 51 gave it; one an
   admin set differently on Admin > Field Metadata is left as it is.

   Unchanged: Source of Record (CuratePlatformMapping), Actual Completion
   Date (ConfirmReconciliation) and Risk Level (ManageTargets).
   ============================================================================ */

USE $DatabaseName$;
GO

UPDATE fm
SET RequiredPermission = NULL
FROM web.account_progress_field_metadata fm
JOIN web.app_permission p ON p.PermissionKey = fm.RequiredPermission
JOIN (VALUES
    ('CurrentStageKey', 'ConfirmReconciliation'),
    ('AccountTypeKey',  'CuratePlatformMapping'),
    ('ApplicationKey',  'CurateApplicationMapping')
) AS v(FieldName, PermissionName) ON v.FieldName = fm.FieldName AND v.PermissionName = p.PermissionName;
GO
