/* ============================================================================
   39_BlueTrack_DirectPullNotification.sql

   RUN THIS AFTER 01-38. Guarded -- safe to re-run.

   D-250: a Self-Hosted Direct pull that fails or is rejected is mailed when it
   happens (SelfHostedPullFailedCheck), instead of the admin first hearing of it
   from the nightly import failing half an hour later. Like the other
   notification types it starts disabled: turn it on, and add recipients, on
   Admin > Notifications.
   ============================================================================ */

USE $DatabaseName$;
GO

INSERT INTO web.dim_notification_type (NotificationTypeName, Description, IsEnabled)
SELECT 'SelfHostedPullFailed', 'A Self-Hosted Direct pull (PVWA REST API, D-248) failed or was rejected because the Safe or account count changed more than allowed, or has not finished 2 hours after it started (D-250). One email per pull, within about 5 minutes.', 0
WHERE NOT EXISTS (SELECT 1 FROM web.dim_notification_type WHERE NotificationTypeName = 'SelfHostedPullFailed');
GO

PRINT '39_BlueTrack_DirectPullNotification.sql complete.';
GO
