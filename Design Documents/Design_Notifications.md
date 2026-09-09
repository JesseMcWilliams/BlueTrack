# Web Interface Design Document — Notifications

**Blueprint Progress Tracking Web Interface**

## Purpose & Scope

A general, reusable "send this alert to the admins" capability, requested twice now: once broadly ("build out the notification framework and consolidate it to one source... support modern SMTP features"), and once for a specific trigger (D-114: warn when DevFakeAuth has been left enabled for more than a week). D-19 already established the principle for Risk Exceptions -- "active notification (email/Teams/etc.) will be offered, but as an optional/configurable capability" -- this document is the first real design pass at building that capability rather than just the in-app banner half of it.

**This is the document that matches what's actually built.** `Design_Notification_Framework.md` (2026-09-05) proposed an earlier architecture that was never implemented as written — see that document's own superseded notice.

## Current State (confirmed by reading the code, not assumed)

- **No notification/email infrastructure exists anywhere in this codebase.** No SMTP client library, no `IEmailSender`-style abstraction, no notification-related table.
- **`web.app_user.Email` is populated from `ClaimsPrincipal.FindFirst(ClaimTypes.Email)`** (`CurrentUserResolver.cs`), and the only authentication method actually wired today (WindowsIntegrated, per D-84's own tracked status) never supplies an email claim -- a Windows Negotiate token carries no email address without a separate directory lookup. `AdminUsersController.cs`'s own comment confirms this app has *deliberately* avoided adding AD/LDAP querying (`System.DirectoryServices`) anywhere. **In practice, `app_user.Email` is NULL for every real user in this environment today.** Any notification recipient list therefore cannot be derived from "users holding the Admin role" without first solving that gap. **(D-116, 2026-09-08: the AD/LDAP avoidance above was deliberately reversed, requested directly this time -- see that section below. `app_user.Email` itself is still unpopulated; D-116 resolves recipients through `identity_group_role_map`'s own group identifiers instead, not through `app_user`.)**
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

## D-116 (2026-09-08): role-targeted email, LDAP, and the shared Credential store

Requested directly: (1) an admin-settable notification email per role, where "any group assigned that role" should also get notified; (2) LDAP lookup, a deliberate reversal of this document's own "this app deliberately avoids AD/LDAP lookups" note above -- requested explicitly this time, not assumed; (3) a generic Credentials admin page (secrets backend selection per named credential, DPAPI machine/user scope with a described upgrade behavior); (4) a real local SMTP server (Windows-authenticated, on `192.222.222.149:25`) to actually configure and test against; (5) a Backup App button on the Deployment page (see `Design_Admin_Deployment_Management.md` §3.4).

Four questions resolved directly (`Design_Decision_Register.md`'s D-116 entry has the full detail):
- LDAP resolves a role's mapped AD groups by **expanding membership recursively to individual users** and reading each member's own `mail` attribute -- not the group's own mail alias -- so it reaches every real person holding the role even if the group itself isn't mail-enabled.
- Role/LDAP-resolved recipients are **additive** to the flat `web.notification_recipient` list from D-115, never a replacement -- a notification type with no assigned role behaves exactly as it did before this change.
- The new generic `web.credential` store (`Design_Credentials_Management.md`) replaced `notification_config`'s own inline `Username`/`PasswordSecretReference` columns with a single `SmtpCredentialKey` -- the user's explicit choice to migrate SMTP onto the shared store rather than leave it on separate storage.
- The DPAPI Machine-then-User scope upgrade mechanism (see `Design_Credentials_Management.md`) applies to any credential, SMTP's included.

**Schema** (`17_BlueTrack_CredentialsLdapBackupSchema.sql`): `web.app_role.NotificationEmail` (nullable); `web.dim_notification_type.TargetRoleKey` (nullable FK to `app_role` -- NULL is the default and preserves D-115's exact behavior); `web.notification_config.SmtpCredentialKey` replacing the old `Username`/`PasswordSecretReference` columns (which held only test values already reverted to NULL, so nothing real was dropped).

**Code**: `NotificationRepository.GetTargetRoleInfoAsync(notificationTypeName)` returns the target role's own `NotificationEmail` plus the raw `IdentityGroupName` values from every `identity_group_role_map` row mapped to that role (real AD SIDs and, harmlessly, any non-AD pseudo-group like a DevFakeAuth test mapping, which `LdapGroupMemberResolver` simply fails to resolve and skips). A new `App/Api/Ldap/LdapGroupMemberResolver.cs` -- this app's **first use of `System.DirectoryServices.AccountManagement`** -- binds via `PrincipalContext` using the LDAP bind account (a `web.credential` row, resolved the same way as any other credential) and expands each group recursively (`GroupPrincipal.GetMembers(recursive: true)`), reading `UserPrincipal.EmailAddress`. Entirely opt-in: returns an empty list with no attempt at all unless `web.ldap_config.IsEnabled` and a bind `CredentialKey` are both set. `NotificationCheckBackgroundService` assembles the final send list as a `HashSet<string>` union of the flat active-recipient list, the target role's own email, and the LDAP-resolved addresses -- wrapped in its own `try/catch` so an LDAP failure (bind account misconfigured, domain unreachable) degrades the recipient list rather than blocking the whole send. `SmtpNotificationSender` now resolves its credential via `CredentialRepository.ResolveForUseAsync` instead of decrypting an inline field.

**One real MailKit finding from live testing**: MailKit's `AuthenticateAsync(username, password)` auto-negotiates whichever SASL mechanism the server advertises (PLAIN/LOGIN/NTLM/...) -- the real Windows-authenticated relay needed no special-casing beyond a plain username/password credential to authenticate via NTLM. Separately, that relay's real TLS certificate passed hostname/chain validation but had no reachable CRL/OCSP endpoint from this network; skipping just that check was initially hardcoded (`SmtpClient.CheckCertificateRevocation = false`), then immediately made a real per-environment setting instead -- see the follow-up directly below.

### D-116 follow-up (same day): TLS override checkboxes, not a hardcoded default

The initial `CheckCertificateRevocation = false` fix above was a permanent hardcoded behavior for every environment. Asked to make it configurable instead, plus add a second, broader option: `web.notification_config` gained two admin-editable checkboxes on the Notifications page (`18_BlueTrack_SmtpTlsOverridesSchema.sql`), both **off by default** (secure by default, same convention as every other setting in this table):

- **Ignore CRL issues** (`IgnoreCrlErrors`) -- maps to `SmtpClient.CheckCertificateRevocation = false`. Skips only the CRL/OCSP lookup; hostname and chain-of-trust validation stay fully enforced.
- **Ignore all SSL errors** (`IgnoreSslErrors`) -- maps to `SmtpClient.ServerCertificateValidationCallback = (_, _, _, _) => true`. A full bypass (hostname, chain, expiry -- everything), broader and more dangerous than the CRL-only option; the UI labels it "use with caution."

**Verified live, both directions, for both checkboxes**: with both off, a real test send against the real relay failed with the exact same CRL error as before (confirming the default is genuinely secure, not silently bypassed). Turning on `IgnoreCrlErrors` alone made the same send succeed. Separately, pointing the config at the relay's raw IP address (which fails hostname validation, a *different* error than the CRL one) failed with both off, then succeeded with only `IgnoreSslErrors` on -- confirming the two checkboxes are independent, not aliases of each other. `dotnet test` 253/253, Vitest 18/18 (unaffected).

**Verified end to end against real infrastructure**: created a real `WindowsDpapi` credential for `BlueTrack_SMTP` with `ScopePreference = User`, confirmed it started at `CurrentScope = Machine` and flipped to `User` (ciphertext genuinely changed, ~216-char Base64 both times) on the very first decrypt, then continued to decrypt correctly afterward. Sent a real test email through the real relay (`192.222.222.149`, resolved via its actual hostname to satisfy certificate validation) using that credential -- delivered successfully. Set the Admin role's `NotificationEmail`, targeted `DevFakeAuthEnabledTooLong` at the Admin role, enabled LDAP using the same SMTP credential as the bind account (a real domain account, so this exercised the actual production code path, not just a capability probe), backdated `DevFakeAuth`'s `ModifiedDate` past the 7-day threshold, and restarted the API to trigger the background service's immediate first run: a real notification fired and was logged, with the recipient union correctly including a real AD group's real members (confirmed via a separate ad hoc probe against `CyberArk Vault Admins`, resolving two real `mail`-attributed accounts) alongside the role email and flat list. All test data (credential, LDAP config, role email, recipient, notification log, DevFakeAuth state) reverted afterward. `dotnet test` 253/253, Vitest 18/18 (unaffected -- no test coverage exists yet for this feature, matching D-115's own note).

## Implementation Status

**Implemented, D-115 (2026-09-06).** All four open questions resolved (dedicated admin-managed recipient list; .NET background service + MailKit, not Database Mail; STARTTLS + username/password only, not OAuth2; the general extensible framework, not a DevFakeAuth-only build) -- see the Decision Register entry for the full resolution and verification detail.

Built as proposed above, with no changes to the data model: `16_BlueTrack_NotificationSchema.sql` (`web.notification_config`, `web.notification_recipient`, `web.dim_notification_type` seeded with `DevFakeAuthEnabledTooLong`, `web.notification_log`, plus a new `ManageNotifications` permission explicitly granted to Admin). `NotificationRepository` (redacted vs. unredacted config reads, matching `IdentityProviderRepository`'s own pattern), `SmtpNotificationSender` (MailKit, decrypts the password via `ILocalSecretProtector`), `INotificationCheck`/`DevFakeAuthEnabledCheck` (the one wired trigger today), `NotificationCheckBackgroundService` (daily cycle, its own DI scope per run, cooldown-gated via `notification_log`), `NotificationsController` (`/api/admin/notifications`, gated by `ManageNotifications`), and a new `Notifications.vue` admin page (SMTP config form, Send Test Email, recipients CRUD).

Verified against the real `BlueTrack` database: config/recipient CRUD, the password column holding genuine DPAPI ciphertext (not plaintext), and a real MailKit connection attempt against an unreachable host failing gracefully (`200 OK`, `{success:false, error:...}`) rather than a 500. No real SMTP server was available in this environment to confirm an actual delivered email end to end. `dotnet test` 253/253, Vitest 18/18 (unaffected -- purely additive, no existing code path changed).

Not built, deliberately out of scope per the resolved questions above: OAuth2/XOAUTH2 (a direct M365/Google Workspace mailbox with Basic Auth disabled would need it); a second `INotificationCheck` (D-19's overdue Risk Exceptions is the next likely candidate, but wiring it is its own future task, not assumed here).
