/* ============================================================================
   16_BlueTrack_BreakGlassAccounts.sql

   RUN THIS AFTER 01-15. Guarded -- safe to re-run.

   D-216: break-glass sign-in alerts (the break-glass decision: the
   credential lives in CyberArk, and using it raises an alert). BlueTrack
   adds no separate sign-in path -- an emergency account signs in like any
   other (for example the server's local Administrator, which the bootstrap
   Admin role maps through BUILTIN\Administrators). Instead:
   - web.break_glass_account: the accounts an admin has designated as
     break-glass, by the identifier BlueTrack sees at sign-in (a Windows SID,
     or an OIDC/SAML user identifier).
   - A sign-in by one writes a BreakGlassLogon audit event and emails the
     BreakGlassLogon notification type's recipients.
   ============================================================================ */

USE $DatabaseName$;
GO

IF OBJECT_ID('web.break_glass_account') IS NULL
BEGIN
    CREATE TABLE web.break_glass_account (
        BreakGlassAccountKey   INT IDENTITY(1,1) CONSTRAINT PK_break_glass_account PRIMARY KEY,
        ExternalIdentifier     NVARCHAR(300)  NOT NULL CONSTRAINT UQ_break_glass_account_ExternalIdentifier UNIQUE,
        DisplayName            NVARCHAR(300)  NOT NULL,
        Notes                  NVARCHAR(1000) NULL,
        CreatedBy              INT            NULL CONSTRAINT FK_break_glass_account_CreatedBy REFERENCES web.app_user(UserKey),
        CreatedDate            DATETIME2      NOT NULL CONSTRAINT DF_break_glass_account_CreatedDate DEFAULT SYSUTCDATETIME()
    );
END
GO

INSERT INTO web.dim_audit_event_type (EventTypeName, Description)
SELECT 'BreakGlassLogon', 'A designated break-glass account signed in'
WHERE NOT EXISTS (SELECT 1 FROM web.dim_audit_event_type WHERE EventTypeName = 'BreakGlassLogon');
GO

INSERT INTO web.dim_notification_type (NotificationTypeName, Description)
SELECT 'BreakGlassLogon', 'A designated break-glass account signed in to BlueTrack (D-216). Sent at every sign-in, with no cooldown.'
WHERE NOT EXISTS (SELECT 1 FROM web.dim_notification_type WHERE NotificationTypeName = 'BreakGlassLogon');
GO
