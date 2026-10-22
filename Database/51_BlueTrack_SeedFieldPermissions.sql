/* ============================================================================
   51_BlueTrack_SeedFieldPermissions.sql

   RUN THIS AFTER 01-50. Guarded -- safe to re-run.

   D-291 (the user's answer of 2026-10-21, reversing the "none seeded" part of
   D-198): Account Progress fields that need a permission beyond
   EditAccountProgress to change by hand.

     Account Type, Source of Record           CuratePlatformMapping   (Admin)
     Application                              CurateApplicationMapping (Admin)
     Blueprint Stage, Actual Completion Date  ConfirmReconciliation   (Admin, Approver)
     Risk Level                               ManageTargets           (Admin, Analyst)

   The other fields need nothing extra. A field that already has a
   RequiredPermission (an admin set it on Admin > Field Metadata) is left as
   it is. Progress Rules and the nightly load still move stages: they don't
   save through the Account Progress page.
   ============================================================================ */

USE $DatabaseName$;
GO

UPDATE fm
SET RequiredPermission = p.PermissionKey
FROM web.account_progress_field_metadata fm
JOIN (VALUES
    ('AccountTypeKey',        'CuratePlatformMapping'),
    ('SORKey',                'CuratePlatformMapping'),
    ('ApplicationKey',        'CurateApplicationMapping'),
    ('CurrentStageKey',       'ConfirmReconciliation'),
    ('ActualCompletionDate',  'ConfirmReconciliation'),
    ('RiskLevelKey',          'ManageTargets')
) AS v(FieldName, PermissionName) ON v.FieldName = fm.FieldName
JOIN web.app_permission p ON p.PermissionName = v.PermissionName
WHERE fm.RequiredPermission IS NULL;
GO
