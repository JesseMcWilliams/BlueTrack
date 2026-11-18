/* ============================================================================
   77_BlueTrack_PullMissedNotification.sql

   RUN THIS AFTER 01-76. Guarded -- safe to re-run.

   D-363 (follows D-361, D-362): notification type SelfHostedPullMissed, off
   until turned on like the others: the nightly Self-Hosted Direct pull time
   passed (by 10 minutes) and no pull started, one email per pull time,
   saying whether the API was running then. SelfHostedPullFailed (D-250)
   covers a pull that ran and failed; this covers the night none ran.
   ============================================================================ */
USE $DatabaseName$;
GO

INSERT INTO web.dim_notification_type (NotificationTypeName, Description, IsEnabled)
SELECT 'SelfHostedPullMissed', 'The nightly Self-Hosted Direct pull time passed and no pull started, usually because the BlueTrack API wasn''t running then, so the next Import will fail (D-363). One email per missed pull time, saying whether the API was running and when it started again.', 0
WHERE NOT EXISTS (SELECT 1 FROM web.dim_notification_type WHERE NotificationTypeName = 'SelfHostedPullMissed');
GO

PRINT '77_BlueTrack_PullMissedNotification.sql complete.';
GO
