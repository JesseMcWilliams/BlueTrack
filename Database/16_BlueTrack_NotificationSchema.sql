/* ============================================================================
   16_BlueTrack_NotificationSchema.sql

   RUN THIS AFTER 01-15. Guarded (`IF OBJECT_ID(...) IS NULL` per table) --
   safe to re-run, per D-58's normal incremental-script convention, now
   resumed since the 2026-09-05 restructure (D-106/D-107).

   Design_Notifications.md, resolved 2026-09-06: a general, reusable
   "send this alert to the admins" capability -- requested twice, once
   broadly ("build out the notification framework and consolidate it to
   one source") and once for a specific trigger (D-114: warn when
   DevFakeAuth has been enabled for more than a week). D-19 already
   established the principle for Risk Exceptions ("active notification...
   as an optional/configurable capability") -- this is the first real
   build of that capability, general enough for a second trigger later
   without another schema change.

   Four decisions resolved directly with the user, all in
   Design_Notifications.md:
     - Recipients: a dedicated admin-managed list (`notification_recipient`),
       not derived from app_user.Email -- confirmed unusable today, since
       WindowsIntegrated (the only real auth method wired) never supplies
       an email claim, and this app deliberately avoids AD/LDAP lookups
       elsewhere (AdminUsersController.cs's own comment).
     - Delivery: a .NET background service + MailKit, not SQL Server
       Database Mail -- keeps SMTP settings admin-UI-editable
       (`notification_config`), consistent with Identity Providers/
       Secrets Store/App Config, rather than buried in msdb's own
       system tables.
     - "Modern SMTP" scoped to STARTTLS + username/password, not OAuth2 --
       covers on-prem Exchange and the internal relay/smart-host pattern,
       not a direct M365/Google Workspace mailbox with Basic Auth disabled.
     - Framework scope: the general, extensible schema below, not a
       narrow DevFakeAuth-only build -- D-19's overdue Risk Exceptions is
       already a known second trigger waiting on this exact capability.
   ============================================================================ */

USE $DatabaseName$;
GO

-- notification_config: singleton settings row, kept separate from
-- web.app_config the same way web.audit_config already is ("kept
-- separate... so audit-specific and general settings don't mix").
-- PasswordSecretReference is protected via ILocalSecretProtector (the
-- same app-config-secret mechanism OIDC's ClientSecret already uses --
-- not the pluggable Secrets Store, which is for privileged-account
-- retrieval, a different concern). Seeded with reasonable STARTTLS
-- defaults and no server/credentials -- an admin fills in real values
-- via the Notifications admin page, same pattern as OIDC/SAML's
-- disabled-placeholder rows (D-84).
IF OBJECT_ID('web.notification_config', 'U') IS NULL
BEGIN
    CREATE TABLE web.notification_config (
        NotificationConfigKey     INT IDENTITY(1,1) PRIMARY KEY,
        SmtpHost                    NVARCHAR(255)    NULL,
        SmtpPort                      INT              NOT NULL DEFAULT 587,
        EnableStartTls                  BIT              NOT NULL DEFAULT 1,
        AuthMethod                        NVARCHAR(20)     NOT NULL DEFAULT 'Basic',   -- 'None' or 'Basic' -- see Design_Notifications.md's OAuth2 open question (resolved: out of scope for now)
        Username                            NVARCHAR(255)    NULL,
        PasswordSecretReference                NVARCHAR(500)    NULL,
        FromAddress                              NVARCHAR(320)    NULL,
        FromDisplayName                            NVARCHAR(255)    NULL,
        ModifiedBy                                    INT              NULL REFERENCES web.app_user(UserKey),
        ModifiedDate                                    DATETIME2        NULL
    );

    INSERT INTO web.notification_config (SmtpPort, EnableStartTls, AuthMethod) VALUES (587, 1, 'Basic');
END
GO

-- notification_recipient: independent of role membership, the same way
-- web.identity_group_role_map is independent of dbo.dim_user. Created
-- empty -- an admin adds real recipients via the Notifications page.
IF OBJECT_ID('web.notification_recipient', 'U') IS NULL
BEGIN
    CREATE TABLE web.notification_recipient (
        RecipientKey     INT IDENTITY(1,1) PRIMARY KEY,
        Email              NVARCHAR(320)    NOT NULL UNIQUE,
        DisplayName          NVARCHAR(255)    NULL,
        IsActive                BIT              NOT NULL DEFAULT 1
    );
END
GO

-- dim_notification_type: extensible catalog, same pattern as
-- web.dim_audit_event_type -- a new alert kind is a new row, not a
-- schema change. Seeded with the one trigger wired today (D-114); D-19's
-- overdue Risk Exceptions is the known next candidate.
IF OBJECT_ID('web.dim_notification_type', 'U') IS NULL
BEGIN
    CREATE TABLE web.dim_notification_type (
        NotificationTypeKey     INT IDENTITY(1,1) PRIMARY KEY,
        NotificationTypeName      NVARCHAR(100)    NOT NULL UNIQUE,
        Description                  NVARCHAR(500)    NULL
    );

    INSERT INTO web.dim_notification_type (NotificationTypeName, Description) VALUES
        ('DevFakeAuthEnabledTooLong', 'DevFakeAuth has been enabled for more than 7 days (D-114) -- it bypasses real authentication and should only be left on briefly during local development.');
END
GO

-- notification_log: one row per actual send -- both an audit trail and
-- the de-duplication mechanism (a scheduled check that finds a recent
-- row for the same type within its cooldown window doesn't re-send).
IF OBJECT_ID('web.notification_log', 'U') IS NULL
BEGIN
    CREATE TABLE web.notification_log (
        NotificationLogKey     BIGINT IDENTITY(1,1) PRIMARY KEY,
        NotificationTypeKey      INT              NOT NULL REFERENCES web.dim_notification_type(NotificationTypeKey),
        SentDate                    DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        Detail                        NVARCHAR(2000)   NULL
    );
END
GO

-- ManageNotifications permission (found missing the same way ViewDeploymentInfo
-- was, D-98): added to the confirmed catalog and granted to the bootstrap
-- Admin role explicitly, since 09_BlueTrack_WebSeed.sql's own blanket
-- "every permission" grant to Admin only ran once, at that script's own
-- execution -- a permission added afterward, by a later incremental
-- script, needs its own explicit grant to reach an already-seeded Admin role.
IF NOT EXISTS (SELECT 1 FROM web.app_permission WHERE PermissionName = 'ManageNotifications')
BEGIN
    INSERT INTO web.app_permission (PermissionName, Description)
    VALUES ('ManageNotifications', 'Configure SMTP settings and notification recipients');
END
GO

DECLARE @ManageNotificationsPermissionKey INT = (SELECT PermissionKey FROM web.app_permission WHERE PermissionName = 'ManageNotifications');
DECLARE @AdminRoleKey INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Admin');

IF @AdminRoleKey IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM web.role_permission WHERE RoleKey = @AdminRoleKey AND PermissionKey = @ManageNotificationsPermissionKey
)
BEGIN
    INSERT INTO web.role_permission (RoleKey, PermissionKey) VALUES (@AdminRoleKey, @ManageNotificationsPermissionKey);
END
GO

PRINT '16_BlueTrack_NotificationSchema.sql complete.';
