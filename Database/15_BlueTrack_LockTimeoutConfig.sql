/* ============================================================================
   15_BlueTrack_LockTimeoutConfig.sql

   RUN THIS AFTER 01-14.

   D-50 (Design_Data_Editing_Behavior.md): the Account Progress edit lock's
   5-minute abandoned-lock timeout is admin-configurable via the Global
   Application Configuration page, not hardcoded. No app_config column
   existed for it -- found while building the Account Progress edit form's
   locking (App/Api/Controllers/AccountProgressLockController.cs), the same
   kind of implementation-time gap as D-71/D-72.

   Default matches the design doc's own stated default (5 minutes).
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('web.app_config', 'LockTimeoutMinutes') IS NULL
BEGIN
    ALTER TABLE web.app_config ADD LockTimeoutMinutes INT NOT NULL DEFAULT 5;
END
GO

PRINT 'web.app_config: LockTimeoutMinutes added (D-50).';
