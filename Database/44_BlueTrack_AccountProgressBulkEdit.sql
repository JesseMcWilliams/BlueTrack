/* ============================================================================
   44_BlueTrack_AccountProgressBulkEdit.sql

   RUN THIS AFTER 01-43. Guarded (COL_LENGTH / NOT EXISTS checks) -- safe
   to re-run, per D-58's incremental-script convention.

   D-182: bulk edit on Account Progress.
     web.app_config.BulkEditMaxAccounts   the most accounts one bulk edit may
                                          change (default 500, agreed
                                          2026-10-09); set on Global
                                          Application Configuration.
     'BulkEdit' audit event type          one summary event per bulk edit,
                                          alongside the usual per-account
                                          FieldEdit events.
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('web.app_config', 'BulkEditMaxAccounts') IS NULL
    ALTER TABLE web.app_config ADD BulkEditMaxAccounts INT NOT NULL CONSTRAINT DF_app_config_BulkEditMaxAccounts DEFAULT 500;
GO

IF NOT EXISTS (SELECT 1 FROM web.dim_audit_event_type WHERE EventTypeName = 'BulkEdit')
    INSERT INTO web.dim_audit_event_type (EventTypeName, Description)
    VALUES ('BulkEdit', 'A bulk edit changed several records at once (each change is also logged as its own FieldEdit)');
GO

PRINT '44_BlueTrack_AccountProgressBulkEdit.sql complete.';
