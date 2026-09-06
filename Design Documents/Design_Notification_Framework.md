# Web Interface Design Document — Notification Framework

**Blueprint Progress Tracking Web Interface**

## Purpose & Scope

Three existing decisions each independently assumed some outbound-notification capability exists or would exist, without any of them actually designing it — found during the 2026-09-05 documentation audit:

- **D-19** (Risk Exceptions): "Active notification (email/Teams/etc.) will be offered, but as an optional/configurable capability" for exceptions approaching/past their review date.
- **D-24** (Authentication): break-glass credential use "triggers an active alert, in addition to passive capture in the general audit log."
- **D-26** (Authentication): a rejected SAML assertion is "logged and alerted on."

Confirmed directly with the user, 2026-09-05: **build one consolidated notification mechanism that all three (and anything later) call into, not three separate implementations** — with real flexibility for multiple delivery channels, and specifically **modern SMTP support** (not the legacy, Microsoft-recommended-against `System.Net.Mail.SmtpClient`).

## Architecture

One shared dispatcher, mirroring this app's existing `IVaultSecretProvider`/`VaultSecretProviderResolver` pattern (many pluggable implementations behind one resolver, D-16):

```
INotificationChannel (interface)
├── SmtpNotificationChannel   -- first implementation, built now
└── (future: WebhookNotificationChannel, TeamsNotificationChannel, ...)

NotificationDispatcher          -- the ONE consolidated entry point
    Task NotifyAsync(string eventTypeName, NotificationContext context)
```

Every caller (the Risk Exception review-date check, break-glass logon detection, SAML assertion rejection) calls `NotificationDispatcher.NotifyAsync(...)` — none of them implement their own sending logic. The dispatcher looks up which channels are enabled, which recipients are configured for that event type, renders a subject/body, dispatches through every enabled channel, and writes an audit row — this is genuinely the "one source" requested, not three copies of similar-but-not-identical email code.

## Proposed Schema

```sql
-- web.dim_notification_event_type: the controlled list of "triggers" that
-- can fire a notification. Consolidates D-19/D-24/D-26's three
-- independent assumptions into one shared, extensible list.
CREATE TABLE web.dim_notification_event_type (
    NotificationEventTypeKey INT IDENTITY(1,1) PRIMARY KEY,
    EventTypeName            NVARCHAR(100) NOT NULL UNIQUE,
    Description              NVARCHAR(500) NULL
);
-- Seeded: RiskExceptionReviewDue (D-19), BreakGlassLogon (D-24), SamlAssertionRejected (D-26).
-- A future event type is a new seeded row, not a schema change.

-- web.notification_channel: a configured delivery channel. SMTP is the
-- first/default; the shape is extensible (ChannelType) so a webhook or
-- Teams channel later doesn't need a redesign.
CREATE TABLE web.notification_channel (
    NotificationChannelKey INT              IDENTITY(1,1) PRIMARY KEY,
    ChannelType            NVARCHAR(50)     NOT NULL,   -- 'Smtp' (only one built for now)
    DisplayName            NVARCHAR(200)    NOT NULL,
    IsEnabled              BIT              NOT NULL DEFAULT 0,
    ConfigurationValues    NVARCHAR(MAX)    NULL,        -- non-secret settings as JSON, structured per ChannelType -- same pattern as Identity Providers/Secrets Store (D-95)
    SecretReference        NVARCHAR(500)    NULL,        -- credential/token, protected via this app's EXISTING Secrets Storage abstraction (ILocalSecretProtector/IVaultSecretProvider) -- no new credential-storage mechanism invented for this
    CreatedBy              INT              NULL REFERENCES web.app_user(UserKey),
    ModifiedBy             INT              NULL REFERENCES web.app_user(UserKey),
    ModifiedDate           DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);

-- web.notification_recipient: who gets notified for a given event type --
-- either everyone currently holding a named permission (self-healing as
-- role membership changes, no list to maintain) or an explicit address.
CREATE TABLE web.notification_recipient (
    NotificationRecipientKey INT              IDENTITY(1,1) PRIMARY KEY,
    NotificationEventTypeKey INT              NOT NULL REFERENCES web.dim_notification_event_type(NotificationEventTypeKey),
    RecipientType            NVARCHAR(20)     NOT NULL,   -- 'Permission' / 'ExplicitEmail'
    PermissionName           NVARCHAR(100)    NULL,       -- when RecipientType = 'Permission': notify everyone currently holding it
    ExplicitEmail            NVARCHAR(320)    NULL,       -- when RecipientType = 'ExplicitEmail'
    IsEnabled                BIT              NOT NULL DEFAULT 1
);

-- web.notification_log: outbox/audit trail -- what was actually sent, to
-- whom, and whether it succeeded. Distinct from web.audit_event (which
-- already, separately, passively captures break-glass use and rejected
-- SAML assertions per D-10/D-11) -- this table is specifically about the
-- notification attempt itself, not the underlying security event.
CREATE TABLE web.notification_log (
    NotificationLogKey       BIGINT           IDENTITY(1,1) PRIMARY KEY,
    NotificationEventTypeKey INT              NOT NULL REFERENCES web.dim_notification_event_type(NotificationEventTypeKey),
    Subject                  NVARCHAR(500)    NULL,
    RecipientCount           INT              NOT NULL,
    Success                  BIT              NOT NULL,
    ErrorMessage             NVARCHAR(2000)   NULL,
    SentDate                 DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);
```

## Modern SMTP Support

Confirmed directly: plan for flexibility and support modern SMTP features. Concretely:

- **MailKit**, not `System.Net.Mail.SmtpClient` — Microsoft's own documentation recommends against `System.Net.Mail` for new development; it has no practical path to OAuth2 and limited modern-TLS support. MailKit is the standard, actively-maintained replacement.
- **STARTTLS and implicit TLS**, both supported via MailKit's `SecureSocketOptions` — configurable per channel, not hardcoded to one.
- **OAuth2/XOAUTH2 authentication** — increasingly *required*, not optional: Microsoft 365/Exchange Online has been deprecating basic SMTP AUTH, and Google Workspace pushes the same direction. MailKit supports this via `SaslMechanismOAuth2`.
- **`SmtpNotificationChannel`'s `ConfigurationValues` gets an `AuthMethod`-conditional field set**, reusing the exact pattern already established for Azure Key Vault/AWS Secrets Manager (D-95): `Host`, `Port`, `SecurityMode` (`StartTls`/`ImplicitTls`/`None`), `AuthMethod` (`Basic` | `OAuth2`), and only when `Basic`: `Username` + a credential via `SecretReference`; only when `OAuth2`: `TenantId`/`ClientId` + a credential (client secret or certificate thumbprint) via `SecretReference`.

## Time-Based vs. Event-Driven Triggers — a real architectural fork

Confirmed by checking this app's own existing scheduled-job mechanism (`Database/09_BlueTrack_ScheduleImportLoadJob.sql`, a SQL Agent job for the nightly Import/Load): it's SQL Agent-driven, and SQL Server's own native mail mechanism (`msdb.dbo.sp_send_dbmail`, "Database Mail") is a **different, older SMTP implementation with no OAuth2 path** — using it here would split notification sending across two different mechanisms (Database Mail for time-based triggers, `SmtpNotificationChannel`/MailKit for event-driven ones), directly working against "consolidate to one source."

- **Event-driven triggers** (`BreakGlassLogon`, `SamlAssertionRejected`) fire synchronously, inline, from the request/code path that caused them — straightforward, just an `await NotificationDispatcher.NotifyAsync(...)` call at the right point.
- **Time-based triggers** (`RiskExceptionReviewDue` — nothing "happens," a date is simply crossed) need something to periodically check. **Proposed: an ASP.NET Core hosted `BackgroundService`** running inside the API process (checking daily, e.g. once per idle period) that queries for exceptions crossing their review date and calls the *same* `NotificationDispatcher` — keeping every notification, time-based or event-driven, going through the one consolidated C# path with full modern-SMTP support, rather than adding a second, weaker sending mechanism via SQL Agent + Database Mail.

## Admin Pages

- **Notification Channels** admin page (`admin/NotificationChannels.vue`) — configure SMTP (or a future channel), single add/edit, matching the existing Identity Providers/Secrets Store page shape. New permission: `ManageNotifications`.
- **Notification Recipients** — likely a section of the same page rather than a separate one, given the small scope (three event types today): pick which permission(s) or explicit addresses get notified per event type.
- A "send a test notification" action (mirroring Secrets Store Configuration's existing "Test Connection" pattern) is worth including so an admin can confirm SMTP settings work without waiting for a real break-glass logon or exception deadline.

## Open Questions

- **Message templating** — subject/body content per event type isn't designed yet (plain text? a token-substitution template stored per `NotificationEventTypeKey`, editable by an admin? hardcoded per-trigger text in code?).
- **Retry behavior** — a transient SMTP failure isn't addressed yet (retry once? queue and retry later? just log the failure in `notification_log` and move on?). Given this app's small scale, a simple "log and move on, surfaced via the log for an admin to notice" is the likely starting point, but not yet confirmed.
- **`BackgroundService` check frequency and exact "review date crossed" query** for `RiskExceptionReviewDue` — not yet designed; needs to reuse `RiskExceptionRepository.GetOverdueReviewAsync()` or similar rather than a new query, and needs a "don't re-notify for the same exception every single day" de-duplication rule.
- **Whether `notification_log` should link back to the specific entity** (which exception, which logon event) rather than just recording the event type name — would make the log more useful for tracing "did the exception review reminder for EXC-2026-0042 actually go out," not yet decided.

---
*New document added 2026-09-05, following a documentation audit that found three separate decisions (D-19, D-24, D-26) assuming a notification mechanism existed when none had ever been designed or built. Design only — no schema applied, no code written yet.*
