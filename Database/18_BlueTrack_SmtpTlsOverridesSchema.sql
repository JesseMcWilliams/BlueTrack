/* ============================================================================
   18_BlueTrack_SmtpTlsOverridesSchema.sql

   RUN THIS AFTER 01-17. Guarded (column-exists checks) -- safe to re-run,
   per D-58's normal incremental-script convention.

   D-116's live SMTP testing against the real provisioned relay
   (192.222.222.149) found its certificate's CRL/OCSP endpoint unreachable
   from this network -- worked around at the time with a hardcoded
   SmtpClient.CheckCertificateRevocation = false in SmtpNotificationSender.
   Requested directly afterward (2026-09-08): make that an admin-editable
   per-environment checkbox rather than a permanent hardcoded behavior, and
   add a second, broader "ignore all SSL errors" checkbox alongside it (a
   full ServerCertificateValidationCallback bypass -- hostname, chain, expiry,
   everything) for environments where even that isn't enough. Both default to
   0 (off) -- secure by default, same as web.notification_config's other
   settings; an admin opts in per-environment after seeing a real failure.
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('web.notification_config', 'IgnoreCrlErrors') IS NULL
BEGIN
    ALTER TABLE web.notification_config ADD IgnoreCrlErrors BIT NOT NULL CONSTRAINT DF_notification_config_IgnoreCrlErrors DEFAULT 0;
END
GO

IF COL_LENGTH('web.notification_config', 'IgnoreSslErrors') IS NULL
BEGIN
    ALTER TABLE web.notification_config ADD IgnoreSslErrors BIT NOT NULL CONSTRAINT DF_notification_config_IgnoreSslErrors DEFAULT 0;
END
GO

PRINT '18_BlueTrack_SmtpTlsOverridesSchema.sql complete.';
