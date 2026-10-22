# Web Interface Design Document — Notifications

**Blueprint Progress Tracking Web Interface**

## Purpose & Scope

A general, reusable "send this alert to the admins" capability, requested twice now: once broadly ("build out the notification framework and consolidate it to one source... support modern SMTP features"), and once for a specific trigger (D-114: warn when DevFakeAuth has been left enabled for more than a week). D-19 already established the principle for Risk Exceptions -- "active notification (email/Teams/etc.) will be offered, but as an optional/configurable capability" -- this document is the first real design pass at building that capability rather than just the in-app banner half of it.

**This is the document that matches what's actually built.** `Archive_Design_Notification-Framework.md` (2026-09-05) proposed an earlier architecture that was never implemented as written — see that document's own superseded notice.

## Current State (confirmed by reading the code, not assumed)

- **No notification/email infrastructure exists anywhere in this codebase.** No SMTP client library, no `IEmailSender`-style abstraction, no notification-related table.
- **`web.app_user.Email` is populated from `ClaimsPrincipal.FindFirst(ClaimTypes.Email)`** (`CurrentUserResolver.cs`), and the only authentication method actually wired today (WindowsIntegrated, per D-84's own tracked status) never supplies an email claim -- a Windows Negotiate token carries no email address without a separate directory lookup. `AdminUsersController.cs`'s own comment confirms this app has *deliberately* avoided adding AD/LDAP querying (`System.DirectoryServices`) anywhere. **In practice, `app_user.Email` is NULL for every real user in this environment today.** Any notification recipient list therefore cannot be derived from "users holding the Admin role" without first solving that gap. **(D-116, 2026-09-08: the AD/LDAP avoidance above was deliberately reversed, requested directly this time -- see that section below. `app_user.Email` itself is still unpopulated; D-116 resolves recipients through `identity_group_role_map`'s own group identifiers instead, not through `app_user`.)**
- **D-146's SQL Agent job** (`04_BlueTrack_ScheduleImportLoadJob.sql`) is this project's only precedent for scheduled/recurring work, and SQL Server itself has a built-in mail feature (Database Mail, `msdb.dbo.sp_send_dbmail`) that could send email with zero new application code or library -- but its configuration (a Database Mail profile/account) lives in `msdb`'s own system tables, edited via SSMS/T-SQL, not through any BlueTrack admin page -- a real departure from this app's established convention of admin-UI-editable settings (Identity Providers, Secrets Store, App Config are all editable through a BlueTrack admin page, not buried in SQL Server's own configuration).
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
| AuthMethod | nvarchar(20) | `None` / `Basic`; D-223 adds `Microsoft365` / `GoogleWorkspace` / `GenericOAuth2` (see that section) |
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
| NotificationTypeName | nvarchar(100), unique | e.g. `DevFakeAuthEnabledTooLong`; `BreakGlassLogon` (D-216, sent at sign-in by `BreakGlassAlerter`, not by a periodic check; recipients from the shared `NotificationRecipientResolver`) |
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

Requested directly: (1) an admin-settable notification email per role, where "any group assigned that role" should also get notified; (2) LDAP lookup, a deliberate reversal of this document's own "this app deliberately avoids AD/LDAP lookups" note above -- requested explicitly this time, not assumed; (3) a generic Credentials admin page (secrets backend selection per named credential, DPAPI machine/user scope with a described upgrade behavior); (4) a real local SMTP server (Windows-authenticated, on `192.222.222.149:25`) to actually configure and test against; (5) a Backup App button on the Deployment page (see `Design_Admin-Deployment-Management.md` §3.4).

Four questions resolved directly (`Design_Decision-Register.md`'s D-116 entry has the full detail):
- LDAP resolves a role's mapped AD groups by **expanding membership recursively to individual users** and reading each member's own `mail` attribute -- not the group's own mail alias -- so it reaches every real person holding the role even if the group itself isn't mail-enabled.
- Role/LDAP-resolved recipients are **additive** to the flat `web.notification_recipient` list from D-115, never a replacement -- a notification type with no assigned role behaves exactly as it did before this change.
- The new generic `web.credential` store (`Design_Credentials-Management.md`) replaced `notification_config`'s own inline `Username`/`PasswordSecretReference` columns with a single `SmtpCredentialKey` -- the user's explicit choice to migrate SMTP onto the shared store rather than leave it on separate storage.
- The DPAPI Machine-then-User scope upgrade mechanism (see `Design_Credentials-Management.md`) applies to any credential, SMTP's included.

**Schema** (`04_BlueTrack_Baseline_WebSchema.sql`): `web.app_role.NotificationEmail` (nullable); `web.dim_notification_type.TargetRoleKey` (nullable FK to `app_role` -- NULL is the default and preserves D-115's exact behavior); `web.notification_config.SmtpCredentialKey` replacing the old `Username`/`PasswordSecretReference` columns (which held only test values already reverted to NULL, so nothing real was dropped).

**Code**: `NotificationRepository.GetTargetRoleInfoAsync(notificationTypeName)` returns the target role's own `NotificationEmail` plus the raw `IdentityGroupName` values from every `identity_group_role_map` row mapped to that role (real AD SIDs and, harmlessly, any non-AD pseudo-group like a DevFakeAuth test mapping, which `LdapGroupMemberResolver` simply fails to resolve and skips). A new `App/Api/Ldap/LdapGroupMemberResolver.cs` -- this app's **first use of `System.DirectoryServices.AccountManagement`** -- binds via `PrincipalContext` using the LDAP bind account (a `web.credential` row, resolved the same way as any other credential) and expands each group recursively (`GroupPrincipal.GetMembers(recursive: true)`), reading `UserPrincipal.EmailAddress`. Entirely opt-in: returns an empty list with no attempt at all unless `web.ldap_config.IsEnabled` and a bind `CredentialKey` are both set. `NotificationCheckBackgroundService` assembles the final send list as a `HashSet<string>` union of the flat active-recipient list, the target role's own email, and the LDAP-resolved addresses -- wrapped in its own `try/catch` so an LDAP failure (bind account misconfigured, domain unreachable) degrades the recipient list rather than blocking the whole send. `SmtpNotificationSender` now resolves its credential via `CredentialRepository.ResolveForUseAsync` instead of decrypting an inline field.

**One real MailKit finding from live testing**: MailKit's `AuthenticateAsync(username, password)` auto-negotiates whichever SASL mechanism the server advertises (PLAIN/LOGIN/NTLM/...) -- the real Windows-authenticated relay needed no special-casing beyond a plain username/password credential to authenticate via NTLM. Separately, that relay's real TLS certificate passed hostname/chain validation but had no reachable CRL/OCSP endpoint from this network; skipping just that check was initially hardcoded (`SmtpClient.CheckCertificateRevocation = false`), then immediately made a real per-environment setting instead -- see the follow-up directly below.

### D-116 follow-up (same day): TLS override checkboxes, not a hardcoded default

The initial `CheckCertificateRevocation = false` fix above was a permanent hardcoded behavior for every environment. Asked to make it configurable instead, plus add a second, broader option: `web.notification_config` gained two admin-editable checkboxes on the Notifications page (`04_BlueTrack_Baseline_WebSchema.sql`), both **off by default** (secure by default, same convention as every other setting in this table):

- **Ignore CRL issues** (`IgnoreCrlErrors`) -- maps to `SmtpClient.CheckCertificateRevocation = false`. Skips only the CRL/OCSP lookup; hostname and chain-of-trust validation stay fully enforced.
- **Ignore all SSL errors** (`IgnoreSslErrors`) -- maps to `SmtpClient.ServerCertificateValidationCallback = (_, _, _, _) => true`. A full bypass (hostname, chain, expiry -- everything), broader and more dangerous than the CRL-only option; the UI labels it "use with caution."

**Verified live, both directions, for both checkboxes**: with both off, a real test send against the real relay failed with the exact same CRL error as before (confirming the default is genuinely secure, not silently bypassed). Turning on `IgnoreCrlErrors` alone made the same send succeed. Separately, pointing the config at the relay's raw IP address (which fails hostname validation, a *different* error than the CRL one) failed with both off, then succeeded with only `IgnoreSslErrors` on -- confirming the two checkboxes are independent, not aliases of each other. `dotnet test` 253/253, Vitest 18/18 (unaffected).

**Verified end to end against real infrastructure**: created a real `WindowsDpapi` credential for `BlueTrack_SMTP` with `ScopePreference = User`, confirmed it started at `CurrentScope = Machine` and flipped to `User` (ciphertext genuinely changed, ~216-char Base64 both times) on the very first decrypt, then continued to decrypt correctly afterward. Sent a real test email through the real relay (`192.222.222.149`, resolved via its actual hostname to satisfy certificate validation) using that credential -- delivered successfully. Set the Admin role's `NotificationEmail`, targeted `DevFakeAuthEnabledTooLong` at the Admin role, enabled LDAP using the same SMTP credential as the bind account (a real domain account, so this exercised the actual production code path, not just a capability probe), backdated `DevFakeAuth`'s `ModifiedDate` past the 7-day threshold, and restarted the API to trigger the background service's immediate first run: a real notification fired and was logged, with the recipient union correctly including a real AD group's real members (confirmed via a separate ad hoc probe against `CyberArk Vault Admins`, resolving two real `mail`-attributed accounts) alongside the role email and flat list. All test data (credential, LDAP config, role email, recipient, notification log, DevFakeAuth state) reverted afterward. `dotnet test` 253/253, Vitest 18/18 (unaffected -- no test coverage exists yet for this feature, matching D-115's own note).

## D-222 (2026-10-13): Import and Load failures, overdue exceptions, weekly summary

Three more types, seeded by `19_BlueTrack_MoreNotifications.sql`, each an `INotificationCheck` in `App/Api/Notifications/`. All three start **disabled**; an admin turns them on in the Notification Types table (new **Enabled** column, `dim_notification_type.IsEnabled`; the two older types stay on, and a disabled type is never evaluated or sent, `BreakGlassLogon` included). Recipients are the same as every type: the flat list plus the optional target role (D-116).

| Type | When | Content |
|---|---|---|
| `NightlyImportFailed` | Within one check interval (5 minutes) of an Import, Load or (D-224) daily Snapshot run failing, or of a run still `Started` 4 hours after it began (a stopped job or killed session). Once per run. | Step, start/failure time (server time), error, procedure and line, the `import_log` files that failed in that run, who started it. |
| `SelfHostedPullFailed` | Within one check interval (5 minutes) of a Self-Hosted Direct pull (`dbo.cyberark_direct_pull`, D-248) ending Failed or Rejected, or still `Started` 2 hours after it began; pulls older than 28 hours are ignored. One email per pull (DedupKey `pull:<key>`): the outcome, who started it, every message (a rejected pull names the Safes it no longer sees), the Safe and account counts before and now, what to do, and a link to Admin > Data Sources. Added disabled, like the others (D-250, script 39). |
| `OverdueRiskExceptions` | Daily, from the **daily digest time**. Not sent when none are overdue. | The Overdue Reviews page's list (`GetOverdueReviewAsync`: Active, `ReviewDate` before today, D-19), first 100. |
| `WeeklySummary` | On the **weekly summary day**, from its **time**. Once per ISO week. | KPI Summary ratios (D-143); % complete and accounts per stage now against the newest snapshot at least 7 days older (`fact_account_progress_history`, D-221; "No history yet" when there's none); Overdue / At-Risk accounts; exceptions overdue for review; the last Import and Load runs. |
| `SignInRejected` | D-285: at once, from `SignInRejectedAlerter` (like `BreakGlassAlerter`, not a periodic check), when a SAML response is rejected or an OIDC sign-in fails; at most one email per 15 minutes per app instance. | When, the reason, the source IP, how many more since the last email; the LogonFailed audit event is written either way. |
| `SamlCertificateExpiring` | D-287: daily, from the **daily digest time** (`SamlCertificateExpiringCheck`), for the enabled SAML provider's IdP and SP certificates; DedupKey per thumbprint and step (30 days, 7 days, expired, missing). | Which certificate, thumbprint, subject, expiry, and what to do. |
| `CpmHealth` | D-288: daily, from the **daily digest time** (`CpmHealthCheck`); DedupKey per day; not sent when none. | The accounts whose CPM status became failure since the last CpmHealth email (`fact_account.CpmFailureSince` after the log's latest `SentDate`; the first: the last day), up to 100, with Safe, platform and last CPM action. |

**How a failed Import and Load is seen.** The SQL Agent job (`Database/Manual/04`) can't call the API, and `usp_RunFullLoad` recorded nothing. Script 19 adds `dbo.etl_run_log` and redefines `usp_Import_Configured` and `usp_RunFullLoad` (otherwise unchanged) to write a row per run: `Started`, then `Succeeded` or `Failed` with `ERROR_MESSAGE()`, `ERROR_PROCEDURE()` and `ERROR_LINE()`; the Load's row is written outside its transaction so the rollback keeps it. The API reads it with its existing `db_datareader`, so no `msdb` grant is needed. Runs older than 28 hours are ignored. Not covered: a job that never starts (SQL Server Agent stopped, job disabled).

**Schedule** (`notification_config.DailyDigestTime`, `WeeklySummaryDay`, `WeeklySummaryTime`; defaults 07:00, Mon, 07:00; server local time) is set in the Notifications page's **Schedule** section (`GET/PUT /api/admin/notifications/schedule`, audited as a `FieldEdit`). The one daily time serves every daily digest. A digest is due at the first check on or after its time: the same day for the daily one (a server down at 07:00 sends when it's back that day), only on the set day for the weekly one.

**Once only, across restarts.** `NotificationCheckResult.DedupKey` (`run:<EtlRunKey>`, `day:<yyyy-MM-dd>`, `week:<yyyy>-W<ww>`) replaces the cooldown: the background service inserts the `notification_log` row first (`notification_log.DedupKey`, unique per type), sends, and deletes the row if there are no recipients or the send fails, so the next cycle retries. Two cycles or two API processes can't both send. The check interval went from daily to 5 minutes; `DevFakeAuthEnabledTooLong` is still held to its 7-day cooldown.

**Email format.** `NotificationEmail` builds HTML (inline styles, tables) with the same content as the plain-text alternative (`INotificationSender.SendAsync`'s optional `htmlBody`; MimeKit `BodyBuilder`). Every value is HTML-encoded there; counts, percentages, dates and server times use the installation's format culture (`web.app_config.FormatCulture`, D-294; en-US: "1,234", "61.7%", "10/21/2026"). Dedup keys stay invariant. Subjects start `BlueTrack:`.

**Links to pages (D-224).** With `app_config.SiteUrl` set (Global Application Configuration, or the installer's `Db.SiteUrl` step), each "In BlueTrack, open ..." page is a link (`NotificationEmail.SeePage`/`SeePages`, built by `SiteLinks.Link` as the Site URL plus the SPA route: the site root serves the SPA, the API is under `/BlueTrack`), and so is the footer's Admin > Notifications; the plain text gives the address in brackets. Without it, the page name only, as before. The `href` is the `Uri.AbsoluteUri` (percent-encoded) and then HTML-encoded. Pages: NightlyImportFailed links Admin > Data Sources > CyberArk Sources (`/admin/data-sources/cyberark`), or Reports > Trends for a failed Snapshot; OverdueRiskExceptions links Risk Exceptions > Overdue Reviews (`/exceptions/overdue`); WeeklySummary links Reports > KPI Summary, Trends and Overdue / At-Risk (`/reports/kpi-summary`, `/reports/trends`, `/reports/overdue`) and also lists the last Snapshot run; the BreakGlassLogon alert (plain text) names Admin > Audit Log with its address (`/admin/audit-log`). A stored value that isn't a usable URL (typed straight into the table) is treated as not set.

**Not built: `ApprovalsWaiting`** (a per-approver daily digest of exceptions waiting for approval). No exception ever waits: one is approved as it's created (D-70; statuses are Active, Expired, Revoked), and D-140's segregation of duties is about who links an approved exception, not who approves. And nothing stored says who holds `ApproveExceptions` now: rights are resolved at sign-in and cached for at most 15 minutes (D-197), and group memberships aren't stored. The weekly summary leaves out "exceptions waiting approval" for the same reason.

## D-223 (2026-10-13): OAuth2 (XOAUTH2) sign-in for SMTP

Chosen by the user: three more **Auth Method** values beside `None` and `Basic`, for mailboxes where username/password SMTP AUTH is off. Each gets an access token, then signs in with SASL XOAUTH2 (`base64("user=" + mailbox + "^Aauth=Bearer " + token + "^A^A")`), which MailKit 4.17.0 sends as `new SaslMechanismOAuth2(userName, auth_token)` passed to `SmtpClient.AuthenticateAsync(SaslMechanism, CancellationToken)` (both confirmed in the package's own `MailKit.xml`).

| Mode | Settings (script 20, none secret) | The credential's password holds | Token request |
|---|---|---|---|
| `Microsoft365` | Tenant ID (GUID or domain), Client ID, `Secret` or `Certificate`, sign-in mailbox | The client secret, or a PEM certificate and its unencrypted RSA private key | Client credentials to `https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token`, scope `https://outlook.office365.com/.default`; a certificate signs a `client_assertion` JWT (PS256, `x5t#S256` header; `aud` = token endpoint, `iss` = `sub` = client ID, `jti`, `nbf`, `iat`, `exp` 10 minutes on) |
| `GoogleWorkspace` | Sign-in mailbox (impersonated) | The service account's whole JSON key file | An RS256 JWT (`kid` = `private_key_id`; `iss` = `client_email`, `scope` = `https://mail.google.com/`, `aud` = `https://oauth2.googleapis.com/token`, `sub` = mailbox, `exp` = `iat` + 1 hour) posted as `grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer` |
| `GenericOAuth2` | Token URL (https only), Client ID, client authentication `Basic` (RFC 6749 2.3.1, the method every server must support) or `Body`, optional scope and `audience`, XOAUTH2 username | The client secret | RFC 6749 4.4 client credentials |

The sign-in mailbox defaults to the From address. The Notifications page shows only the selected mode's fields and fills the documented host (`smtp.office365.com` / `smtp.gmail.com`), port 587 and STARTTLS when a mode is first picked; the save clears other modes' fields.

**Prerequisites, per the sources below.** Microsoft 365: an Entra app with the **Office 365 Exchange Online > SMTP.SendAsApp** application permission and admin consent; its service principal registered in Exchange Online (`New-ServicePrincipal -AppId <APPLICATION_ID> -ObjectId <OBJECT_ID>`, the Enterprise Application's Object ID, not the App Registration's); `Add-MailboxPermission -Identity <mailbox> -User <SERVICE_PRINCIPAL_ID> -AccessRights FullAccess`; SMTP AUTH on for the mailbox (`Set-CASMailbox -Identity <mailbox> -SmtpClientAuthenticationDisabled $false`; it is off for tenants created after January 2020, and unavailable while Entra security defaults are on); `Add-RecipientPermission` (Send As) if the From address differs from the mailbox. Google Workspace: a service account with a JSON key; a super administrator adds its numeric client ID under **Security > Access and data control > API Controls > Domain wide delegation** with scope `https://mail.google.com/` (Google says propagation can take up to 24 hours); the mailbox is a user in the domain. Google documents the service-account JWT flow and the SMTP XOAUTH2 scope separately, not as one worked SMTP example, so this combination is the least certain of the three.

**Microsoft's deprecation status.** Microsoft is retiring *Basic* authentication for SMTP AUTH client submission and recommends OAuth; app-only (client credentials) OAuth for SMTP is documented as supported on the OAuth page below (updated 2025-10-17). Nothing found marks it deprecated, so it was built; Graph `sendMail` was not needed.

**Secrets.** Kept only in the `web.credential` row (`SmtpCredentialKey`), through its existing backends (DPAPI or a vault); `credential.ProtectedPassword` became `NVARCHAR(MAX)` so a key file fits. The Credentials page's password box is one line, so line breaks may be lost: JSON doesn't need them, and the PEM reader (`OAuthKeyMaterial`) ignores whitespace, or its absence, inside a block. No API response, log or audit row holds a secret, assertion or token: token errors read "Token endpoint returned invalid_client: <first line of error_description>", with the secret masked should a provider echo it.

**Tokens** (`SmtpOAuthTokenService`, Singleton): cached in memory, keyed by a SHA-256 of the settings and secret (a new secret or setting gets a new token), until 5 minutes before `expires_in` (half its life if shorter; not cached without one). 10 seconds a try, 2 retries on a network failure, timeout, 429 or 5xx, none on a 4xx (D-215); no redirects (D-202). The token is got before connecting to SMTP, so a token problem is reported on its own. If the server refuses the token, a new one is fetched and the sign-in retried once, with a provider-specific hint if that fails too. An OAuth2 mode needs STARTTLS (save-time 400, and the sender refuses to send a token over an unencrypted connection, as D-198 does for Basic).

**Saving** (`SmtpAuthMethods.Validate`): 400 with the reasons for an unknown mode or a missing or bad field. Changing the tenant, client ID, token URL, scope, audience, sign-in mailbox (or From, in an OAuth2 mode) counts as a new destination for D-198, so it also needs `ManageCredentials`. Every new field is in the `FieldEdit` audit. **Send Test Email** is now audit-logged as a `ConnectionTest` (D-210's rule for actions that use a secret for real), with the mode and the outcome.

**Status: built, unproven live** for all three providers (no tenant, Workspace domain or OAuth2 provider here). Tests cover the token requests (form fields, scopes, JWT claims and signatures verified with keys made in the test), caching, refresh-once, retries, the sign-in each mode uses (`ISmtpTransport` fake), validation, the script-20 round trip and secrets kept out of responses and the audit log, all against fakes. Sovereign clouds (other Entra hosts) aren't supported.

**D-223 sources** (read 2026-10-13):
- Microsoft, OAuth for IMAP/POP/SMTP (XOAUTH2 format, client credentials, `SMTP.SendAsApp`, `New-ServicePrincipal`, `Add-MailboxPermission`, scope): https://learn.microsoft.com/en-us/exchange/client-developer/legacy-protocols/how-to-authenticate-an-imap-pop-smtp-application-by-using-oauth
- Microsoft, client credentials flow (token endpoint, form fields, error body): https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-client-creds-grant-flow
- Microsoft, certificate credentials (assertion header and claims, PS256): https://learn.microsoft.com/en-us/entra/identity-platform/certificate-credentials
- Microsoft, SMTP AUTH per mailbox (`Set-CASMailbox`, security defaults): https://learn.microsoft.com/en-us/exchange/clients-and-mobile-in-exchange-online/authenticated-client-smtp-submission
- Microsoft, client SMTP submission settings (`smtp.office365.com`, 587, TLS 1.2+, Basic auth deprecation note): https://learn.microsoft.com/en-us/exchange/mail-flow-best-practices/how-to-set-up-a-multifunction-device-or-application-to-send-email-using-microsoft-365-or-office-365
- Google, service account OAuth (JWT header and claims, token endpoint, errors, domain-wide delegation): https://developers.google.com/identity/protocols/oauth2/service-account
- Google, XOAUTH2 protocol (format, scope `https://mail.google.com/`): https://developers.google.com/workspace/gmail/imap/xoauth2-protocol
- Google, Gmail SMTP server (`smtp.gmail.com`, 587 TLS / 465 SSL): https://knowledge.workspace.google.com/admin/gmail/send-email-from-a-printer-scanner-or-app
- IETF RFC 6749 (2.3.1 client password, 4.4 client credentials, 5.2 errors): https://datatracker.ietf.org/doc/html/rfc6749
- MailKit 4.17.0 `SaslMechanismOAuth2` and `AuthenticateAsync(SaslMechanism, ...)`: the package's `MailKit.xml`; Exchange client-credentials example: https://github.com/jstedfast/MailKit/blob/master/ExchangeOAuth2.md

## Implementation Status

**Implemented, D-115 (2026-09-06).** All four open questions resolved (dedicated admin-managed recipient list; .NET background service + MailKit, not Database Mail; STARTTLS + username/password only, not OAuth2; the general extensible framework, not a DevFakeAuth-only build) -- see the Decision Register entry for the full resolution and verification detail.

Built as proposed above, with no changes to the data model: `04_BlueTrack_Baseline_WebSchema.sql` (`web.notification_config`, `web.notification_recipient`, `web.dim_notification_type` seeded with `DevFakeAuthEnabledTooLong`, `web.notification_log`, plus a new `ManageNotifications` permission explicitly granted to Admin). `NotificationRepository` (redacted vs. unredacted config reads, matching `IdentityProviderRepository`'s own pattern), `SmtpNotificationSender` (MailKit, decrypts the password via `ILocalSecretProtector`), `INotificationCheck`/`DevFakeAuthEnabledCheck` (the one wired trigger today), `NotificationCheckBackgroundService` (daily cycle, its own DI scope per run, cooldown-gated via `notification_log`), `NotificationsController` (`/api/admin/notifications`, gated by `ManageNotifications`), and a new `Notifications.vue` admin page (SMTP config form, Send Test Email, recipients CRUD).

Verified against the real `BlueTrack` database: config/recipient CRUD, the password column holding genuine DPAPI ciphertext (not plaintext), and a real MailKit connection attempt against an unreachable host failing gracefully (`200 OK`, `{success:false, error:...}`) rather than a 500. No real SMTP server was available in this environment to confirm an actual delivered email end to end. `dotnet test` 253/253, Vitest 18/18 (unaffected -- purely additive, no existing code path changed).

OAuth2/XOAUTH2, out of scope at D-115, was built by D-223 (above).
