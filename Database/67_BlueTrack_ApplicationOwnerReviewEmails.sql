/* ============================================================================
   67_BlueTrack_ApplicationOwnerReviewEmails.sql

   RUN THIS AFTER 01-66. Guarded -- safe to re-run.

   - D-334: the daily OverdueRiskExceptions digest also emails each
     application's owner (dim_application.OwnerEmail, D-46) the exceptions
     of their applications that are overdue for review. Two options, both
     off by default:
       OwnerIncludeAccountExceptions  also exceptions on single accounts that
                                      belong to the owner's application (the
                                      account's own Application or its Safe's)
       OwnerReminderDays              also a reminder this many days before
                                      the review date (0 = none, up to 90)
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('web.notification_config', 'OwnerIncludeAccountExceptions') IS NULL
    ALTER TABLE web.notification_config ADD
        OwnerIncludeAccountExceptions BIT NOT NULL CONSTRAINT DF_notification_config_OwnerIncludeAccountExceptions DEFAULT 0;
GO

IF COL_LENGTH('web.notification_config', 'OwnerReminderDays') IS NULL
    ALTER TABLE web.notification_config ADD
        OwnerReminderDays INT NOT NULL CONSTRAINT DF_notification_config_OwnerReminderDays DEFAULT 0
            CONSTRAINT CK_notification_config_OwnerReminderDays CHECK (OwnerReminderDays BETWEEN 0 AND 90);
GO

PRINT '67_BlueTrack_ApplicationOwnerReviewEmails.sql complete.';
GO
