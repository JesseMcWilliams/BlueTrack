# Web Interface Design Document — Notifications

**Blueprint Progress Tracking Web Interface**

## Purpose & Scope

A general, reusable "send this alert to the admins" capability, requested twice now: once broadly ("build out the notification framework and consolidate it to one source... support modern SMTP features"), and once for a specific trigger (D-114: warn when DevFakeAuth has been left enabled for more than a week). D-19 already established the principle for Risk Exceptions -- "active notification (email/Teams/etc.) will be offered, but as an optional/configurable capability" -- this document is the first real design pass at building that capability rather than just the in-app banner half of it.

## Current State (confirmed by reading the code, not assumed)

- **No notification/email infrastructure exists anywhere in this codebase.** No SMTP client library, no `IEmailSender`-style abstraction, no notification-related table.
- **`web.app_user.Email` is populated from `ClaimsPrincipal.FindFirst(ClaimTypes.Email)`** (`CurrentUserResolver.cs`), and the only authentication method actually wired today (WindowsIntegrated, per D-84's own tracked status) never supplies an email claim -- a Windows Negotiate token carries no email address without a separate directory lookup. `AdminUsersController.cs`'s own comment confirms this app has *deliberately* avoided adding AD/LDAP querying (`System.DirectoryServices`) anywhere. **In practice, `app_user.Email` is NULL for every real user in this environment today.** Any notification recipient list therefore cannot be derived from "users holding the Admin role" without first solving that gap.
- **D-108's SQL Agent job** (`14_BlueTrack_ScheduleImportLoadJob.sql`) is this project's only precedent for scheduled/recurring work, and SQL Server itself has a built-in mail feature (Database Mail, `msdb.dbo.sp_send_dbmail`) that could send email with zero new application code or library -- but its configuration (a Database Mail profile/account) lives in `msdb`'s own system tables, edited via SSMS/T-SQL, not through any BlueTrack admin page -- a real departure from this app's established convention of admin-UI-editable settings (Identity Providers, Secrets Store, App Config are all editable through a BlueTrack admin page, not buried in SQL Server's own configuration).
- **"Modern SMTP"**: Microsoft 365 and Google Workspace have both deprecated SMTP AUTH with a plain username/password for most tenants, requiring OAuth2 (XOAUTH2) instead -- a real complication if this needs to send through a real M365/Google mailbox directly, as opposed to an internal SMTP relay/smart-host (many orgs run one specifically to let internal systems send mail without each one implementing OAuth2).

## Proposed Data Model

Everything below is a *proposal* pending the open questions -- no code written yet.

### web.notification_config

Singleton settings row, kept separate from `web.app_config` the same way `web.audit_config` already is ("kept separate... so audit-specific and general settings don't mix").

| Field | Type | Purpose |
|---|---|---|
| NotificationConfigKey | int, PK | Surrogate key |
| SmtpHost | nvarchar(255) | |
| SmtpPort | int | |
| EnableStartTls | bit | |
| AuthMethod | nvarchar(20) | `None` / `Basic` / `OAuth2` -- see Open Question below |
| Username | nvarchar(255), nullable | |
| PasswordSecretReference | nvarchar(500), nullable | Protected via whichever Secrets Store backend is active, same pattern as every other credential in this app -- never a plaintext column |
| FromAddress | nvarchar(320) | |
| FromDisplayName | nvarchar(255), nullable | |

### web.notification_recipient

Since `app_user.Email` can't be relied on (see above), a small admin-managed list -- independent of role membership, the same way `web.identity_group_role_map` is independent of `dbo.dim_user`.

| Field | Type | Purpose |
|---|---|---|
| RecipientKey | int, PK | |
| Email | nvarchar(320) | |
| DisplayName | nvarchar(255), nullable | |
| IsActive | bit | Deactivate without deleting (keeps history if this recipient is later reactivated) |

### web.dim_notification_type

Extensible catalog, same pattern as `web.dim_audit_event_type` -- new alert kinds get a new row, not a schema change.

| Field | Type | Purpose |
|---|---|---|
| NotificationTypeKey | int, PK | |
| NotificationTypeName | nvarchar(100), unique | e.g. `DevFakeAuthEnabledTooLong` |
| Description | nvarchar(500), nullable | |

### web.notification_log

One row per actual send -- both an audit trail of what was emailed and the de-duplication mechanism (a scheduled check that finds a recent log row for the same type doesn't re-send).

| Field | Type | Purpose |
|---|---|---|
| NotificationLogKey | bigint, PK | |
| NotificationTypeKey | FK | |
| SentDate | datetime2 | |
| Detail | nvarchar(2000), nullable | e.g. which recipients, what triggered it |

## Proposed Workflow

1. A scheduled check (frequency/mechanism: see Open Questions) evaluates each wired condition. Initially just one: DevFakeAuth enabled `IsEnabled = 1` with `ModifiedDate` older than 7 days (the same signal D-114's in-app banner already uses).
2. If triggered, and `web.notification_log` has no row for that `NotificationTypeKey` within the current cooldown window, send one email to every `IsActive` row in `web.notification_recipient`, then log the send.
3. The SMTP send itself reuses whichever Secrets Store backend is active for the SMTP password, consistent with every other credential in this app (never a plaintext config value).

## Open Questions

Four decisions materially change the shape of this build -- asked directly rather than assumed, per this project's own convention.

1. **Recipient list**: given `app_user.Email` is unusable today, is a dedicated admin-managed recipient list (independent of role membership) the right approach, or is there a different source of truth for "who are the admins" this document is missing?
2. **Delivery mechanism**: SQL Server Database Mail (`sp_send_dbmail` from a SQL Agent job -- zero new app code, but config lives outside BlueTrack's own admin UI, in `msdb`) vs. a .NET background service with its own SMTP client (consistent with this app's admin-UI-editable-settings convention everywhere else, needs a new library since `System.Net.Mail.SmtpClient` is obsolete -- MailKit is the standard modern replacement).
3. **OAuth2 / "modern SMTP"**: does this need to authenticate directly against a mailbox that requires OAuth2 (M365/Google Workspace with Basic Auth disabled), or is STARTTLS + username/password sufficient (covers on-prem Exchange and the internal relay/smart-host pattern many orgs use for exactly this reason)? OAuth2 client-credentials support is a materially bigger build than basic SMTP AUTH.
4. **Framework scope now**: build the general, extensible schema above (recipient list, notification-type catalog, log) even though only one trigger (DevFakeAuth) is wired today -- matching the original "build out the notification framework" ask -- or a narrower, single-purpose build just for this one alert, generalized later if/when a second trigger (e.g. D-19's overdue Risk Exceptions) actually gets built?

## Implementation Status

**Implemented, D-115 (2026-09-06).** All four open questions resolved (dedicated admin-managed recipient list; .NET background service + MailKit, not Database Mail; STARTTLS + username/password only, not OAuth2; the general extensible framework, not a DevFakeAuth-only build) -- see the Decision Register entry for the full resolution and verification detail.

Built as proposed above, with no changes to the data model: `16_BlueTrack_NotificationSchema.sql` (`web.notification_config`, `web.notification_recipient`, `web.dim_notification_type` seeded with `DevFakeAuthEnabledTooLong`, `web.notification_log`, plus a new `ManageNotifications` permission explicitly granted to Admin). `NotificationRepository` (redacted vs. unredacted config reads, matching `IdentityProviderRepository`'s own pattern), `SmtpNotificationSender` (MailKit, decrypts the password via `ILocalSecretProtector`), `INotificationCheck`/`DevFakeAuthEnabledCheck` (the one wired trigger today), `NotificationCheckBackgroundService` (daily cycle, its own DI scope per run, cooldown-gated via `notification_log`), `NotificationsController` (`/api/admin/notifications`, gated by `ManageNotifications`), and a new `Notifications.vue` admin page (SMTP config form, Send Test Email, recipients CRUD).

Verified against the real `BlueTrack` database: config/recipient CRUD, the password column holding genuine DPAPI ciphertext (not plaintext), and a real MailKit connection attempt against an unreachable host failing gracefully (`200 OK`, `{success:false, error:...}`) rather than a 500. No real SMTP server was available in this environment to confirm an actual delivered email end to end. `dotnet test` 253/253, Vitest 18/18 (unaffected -- purely additive, no existing code path changed).

Not built, deliberately out of scope per the resolved questions above: OAuth2/XOAUTH2 (a direct M365/Google Workspace mailbox with Basic Auth disabled would need it); a second `INotificationCheck` (D-19's overdue Risk Exceptions is the next likely candidate, but wiring it is its own future task, not assumed here).
