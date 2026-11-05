/* ============================================================================
   71_BlueTrack_RunStallAlerts.sql

   RUN THIS AFTER 01-70. Guarded -- safe to re-run.

   D-348 (the user, 2026-11-05: "Do all 3. Allow setting this in the Admin
   page" -- the rest of D-337's proposals):
   - web.app_config.RunStallMinutes (5 to 1440, default 30): a pull with no
     progress, or a load procedure (or an Import or Snapshot) still running,
     for this long is stalled. Set on Admin > Data Sources, Import and Load.
   - dbo.cyberark_direct_pull.LastProgressAt: when the pull last read
     something (saved with its steps, D-337), so "no progress" can be told
     from "slow".
   - Notification type RunStalled (off until turned on, like the others),
     one email per stalled pull or load step, with the load session's wait.
   - Audit type ImportLoadSettingsChanged: the stall time was changed.
   - Audit type ImportLoadCancelled: Cancel load on Data Sources stopped
     the Import and Load SQL Agent job (the app's login is in msdb's
     SQLAgentOperatorRole, Manual/08, which sp_stop_job allows).
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('web.app_config', 'RunStallMinutes') IS NULL
    ALTER TABLE web.app_config ADD RunStallMinutes INT NOT NULL
        CONSTRAINT DF_app_config_RunStallMinutes DEFAULT 30
        CONSTRAINT CK_app_config_RunStallMinutes CHECK (RunStallMinutes BETWEEN 5 AND 1440);
GO

IF COL_LENGTH('dbo.cyberark_direct_pull', 'LastProgressAt') IS NULL
    ALTER TABLE dbo.cyberark_direct_pull ADD LastProgressAt DATETIME2 NULL;
GO

INSERT INTO web.dim_notification_type (NotificationTypeName, Description, IsEnabled)
SELECT 'RunStalled', 'A Self-Hosted Direct pull has made no progress, or a step of the nightly Import and Load (a Load procedure, the Import or the Snapshot) has been running, for longer than the time set on Admin > Data Sources, Import and Load (D-348). One email per stalled pull or step, with what the load''s SQL Server session is waiting on.', 0
WHERE NOT EXISTS (SELECT 1 FROM web.dim_notification_type WHERE NotificationTypeName = 'RunStalled');
GO

INSERT INTO web.dim_audit_event_type (EventTypeName, Description)
SELECT v.EventTypeName, v.Description
FROM (VALUES
    ('ImportLoadCancelled', 'Cancel load on Admin > Data Sources stopped the Import and Load SQL Agent job (D-348)'),
    ('ImportLoadSettingsChanged', 'The stall alert time on Admin > Data Sources, Import and Load, was changed (D-348)')
) v(EventTypeName, Description)
WHERE NOT EXISTS (SELECT 1 FROM web.dim_audit_event_type t WHERE t.EventTypeName = v.EventTypeName);
GO

PRINT '71_BlueTrack_RunStallAlerts.sql complete.';
GO
