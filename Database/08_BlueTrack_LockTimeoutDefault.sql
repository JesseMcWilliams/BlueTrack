/* ============================================================================
   08_BlueTrack_LockTimeoutDefault.sql

   RUN THIS AFTER 01-07. Guarded -- safe to re-run.

   D-201: the Account Progress edit-lock timeout (web.app_config.LockTimeoutMinutes)
   defaults to 30 minutes instead of 5, for new installs only (chosen directly
   on 2026-10-12). The API accepts 1 to 720 minutes.

     - The column's default becomes 30 (a named constraint, replacing the
       unnamed DEFAULT 5 from 04).
     - The single app_config row is set to 30 only on a new install, meaning
       nobody has signed in yet (web.app_user holds only
       05's data-feed system account, S-1-0-0). An existing install
       keeps its value, 5 or whatever an admin set.
   ============================================================================ */

USE $DatabaseName$;
GO

IF NOT EXISTS (SELECT 1 FROM sys.default_constraints
               WHERE parent_object_id = OBJECT_ID('web.app_config') AND name = 'DF_app_config_LockTimeoutMinutes')
BEGIN
    DECLARE @oldDefault SYSNAME = (
        SELECT dc.name
        FROM sys.default_constraints dc
        JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
        WHERE dc.parent_object_id = OBJECT_ID('web.app_config') AND c.name = 'LockTimeoutMinutes');
    IF @oldDefault IS NOT NULL
        EXEC (N'ALTER TABLE web.app_config DROP CONSTRAINT ' + @oldDefault);

    ALTER TABLE web.app_config ADD CONSTRAINT DF_app_config_LockTimeoutMinutes DEFAULT 30 FOR LockTimeoutMinutes;
END
GO

IF NOT EXISTS (SELECT 1 FROM web.app_user WHERE ExternalIdentifier <> 'S-1-0-0')
    UPDATE web.app_config SET LockTimeoutMinutes = 30 WHERE LockTimeoutMinutes = 5;
GO
