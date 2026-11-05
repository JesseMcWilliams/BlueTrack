# BlueTrack Settings Reference

**For BlueTrack administrators**

## About this guide

BlueTrack tracks how privileged accounts are onboarded into CyberArk against the CyberArk PAM Blueprint. It is a web application hosted on IIS, with a SQL Server database loaded each night from CyberArk exports.

This guide lists every setting an administrator can change: on the **Admin** pages in the web application, and in the choices made when installing with `Install-BlueTrack.ps1`. For each setting it gives what the setting does, its default, the values allowed, a recommended value, and the reason for that recommendation.

The recommendations favour least privilege, encrypted connections, short-lived access and simple operation. Many settings depend on your organization's policies. Where that is the case, the recommendation says so, and the reason explains when to choose differently.

## How to read the tables

Each section has a table with these columns:

- **Setting**: the label shown on the page, or the installer parameter name.
- **What it does**: the effect of the setting.
- **Default**: the value on a new install. "Blank" means no value is set.
- **Recommended**: the suggested value. "Depends" means it depends on your organization; the reason column explains how to choose.
- **Why**: the reason for the recommendation, including the trade-off.

Allowed values and ranges are given in the "What it does" column or in the notes after a table. Where a setting needs more explanation, a short paragraph follows the table.

Every Admin page needs its own permission (see "Roles & Permissions"). A user sees only the Admin pages their roles allow. When a save is refused, the page gives the reason (for example a name already in use, or something that still uses the record); a save that returns to a list says so there.

This guide has three parts. This file covers sections 1 (Global Application Configuration) and 2 (Sign-in and access). Sections 3 (Data and risk scoring), 4 (Deployment page and health checks) and 5 (Installer and server settings) are in [Admin_SettingsReference_Data.md](Admin_SettingsReference_Data.md). Sections 6 (Secrets Store and Credentials, including LDAP), 7 (Notifications), 8 (SIEM Forwarding) and 9 (API Keys) are in [Admin_SettingsReference_Notifications.md](Admin_SettingsReference_Notifications.md). All three are published together as the Administrator Settings Guide.

## 1. Global Application Configuration

**Admin > Global Application Configuration** (permission `ManageApplicationConfiguration`). The page is one form with one **Save** button. Every change is recorded in the audit log with the old and new values.

### General

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Site URL | The address people open BlueTrack at, for example `https://bluetrack.company.com`. Notification emails link to the pages they mention under it; blank means they name the pages instead. Must start with `https://` (`http://` only for localhost), with no user name, query or `#` part. | Filled in by the installer (`https://<Hostname>`, plus `:<HttpsPort>` when that isn't 443) when blank | The address users type, matching the certificate | Links in emails only work if they point where users can reach the site. The installer never changes a value you've set; blank it here to clear it. |
| Idle Timeout (minutes) | Signs a user out after this many minutes without activity in any BlueTrack tab. OIDC and SAML sessions also end on the server after this long without a request, even if the tab was closed. | 30 | 15 to 30 | Short enough that an unattended screen doesn't stay signed in to privileged-access data, long enough not to interrupt normal work. Allowed: 5 to 720. |
| Breadcrumb Position | Where the breadcrumb trail appears on every page: Top Left or Top Right. Applies to all users. | Top Left | Top Left | A matter of taste; Top Left is the usual place for breadcrumbs. |
| Number and date format | The number and date format of text the server writes: notification emails and the data feed and Self-Hosted pull summaries. A culture name such as `en-US`, `en-GB`, `de-DE` or `fr-FR`; the field suggests common ones and shows an example (a number and a date) as you type. | `en-US` | The format your administrators and email recipients read | An email goes to an address, not to a browser, so it can't follow each reader's language. The pages themselves always use each person's own browser or chosen language. CSV and Excel exports and audit details keep a fixed format. |
| Exception ID Pattern | The format of new Risk Exception IDs. Tokens: `{yyyy}` (year), `{yy}` (two-digit year), `{seq:0000}` (running number, padded to the number of zeros). | `EXC-{yyyy}-{seq:0000}` | Keep the default, or your own prefix with a year token and `{seq:...}` | The running number restarts at 1 each year, so a pattern without a year token, or without `{seq:...}`, produces duplicate IDs, which are refused. |
| Account Progress Lock Timeout (minutes) | How long an Account Progress edit lock survives after the editor's page stops renewing it, for example when a browser is closed. Allowed: 1 to 720. | 30 | 30 (10 to 30 suits most teams) | An open edit page renews its lock every minute, so this only controls how long an abandoned lock blocks others. Keep it well above 1 minute. |
| Enforce segregation of duties on Risk Exception approval | When on, the person who approved a Risk Exception cannot also be the one who links it to an account. | Off | On, if you have separate staff | Stops one person both approving and applying an exception. Leave it off for a small team, and review the Risk Exception segregation-of-duties report instead. |
| Bulk edit limit (accounts) | The most accounts one bulk edit, bulk action, delete or undelete may change. Allowed: 1 to 10,000. | 500 | 500 | Large enough for normal work, small enough that a mistaken bulk change is limited. Raise it only for planned clean-ups. |
| Export Max Rows | The most rows one report export (**Export CSV** or **Export Excel** on any report, or on the Audit Log Viewer) may hold. When more rows match the filters, the export is refused with the number of rows, never cut short. Allowed: 1 to 1,000,000. | 100,000 | 100,000 | Large enough for any normal report, small enough that one export can't tie up the server. Users narrow the filters when they hit it. |
| API key default lifetime (days) | How long a new API key lasts before it expires. Allowed: 1 to 365. You can choose another lifetime for each key when you create it (see section 9, API Keys). | 90 | 90 | Short enough that a forgotten key stops working, long enough that you aren't replacing keys constantly. Raise it only if a system can't be given a new key as often. |
| API requests per minute, per key | The most requests one API key may make in a minute. Allowed: 1 to 100,000. Over it, the API answers "too many requests" (HTTP 429) until the minute is up. | 600 | 600 | Far above what a normal script needs, low enough that a runaway loop can't flood the server. Raise it for a busy integration. |
| API key expiry reminder lead time (days) | How many days before an API key expires its owner is emailed (the owner is emailed again on the day, and the administrators on the day). Allowed: 1 to 90. | 7 | 7 | A week is enough to get a new key into a script. Lengthen it for scripts that are changed only at set times. |
| Delete expired API keys after (days) | How many days an expired API key stays on the API Keys list before it is deleted automatically. Allowed: 0 to 3,650; 0 keeps them for ever. A key that is only disabled is never deleted automatically. | 365 | 365 | Long enough to see which key expired and when, short enough that the list doesn't fill with dead keys. Each deletion is in the Audit Log. |

**Idle timeout.** In the browser, BlueTrack signs the user out after this many minutes with no mouse, keyboard or touch activity in any of its tabs, and the sign-in page says why. OIDC and SAML sessions are also ended on the server once no request has arrived for this long, so a closed tab doesn't leave a usable session behind; each OIDC session also ends after 8 hours regardless. Windows sign-in has no session to end: if Windows Integrated is the default method, the user is signed in again as soon as they leave the sign-in page. Separately, a user's cached roles and permissions last at most 15 minutes, and any change to a role, group mapping or identity provider clears every user's cache.

### Audit

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Audit Retention (days, blank = keep forever) | How many days of audit log to keep. Older entries are deleted by the nightly Audit Log Purge job. | Blank (keep forever) | Depends: your audit retention policy, for example 365 or more | Keeping the log forever is safest for investigations but grows the database. Set it to the period your policy requires. |
| Log read/view events | Also records a "viewed" event each time someone opens an Account Progress record or a Risk Exception. List pages and reports are never logged. | Off | Off, unless your policy requires read auditing | Write, approval and logon events are always logged. Read logging adds an entry for every record opened, which makes the log much larger. Report exports are always logged (event type `ReportExport`), whatever this setting says. |

**Audit retention needs the purge job.** Nothing is deleted until the SQL Server Agent job "Audit Log Purge" is installed. The installer does not install it: run `Database/Manual/05_BlueTrack_ScheduleAuditLogPurgeJob.sql` by hand (it runs nightly at 03:00). Until a retention value is set, the job deletes nothing. The application's own database account cannot delete audit entries; only the job can. While SIEM forwarding is on (section 8), the job keeps events not yet forwarded, however old.

**Seals and Verify.** BlueTrack seals every audit event within seconds of writing it: a fingerprint of the event, made with a key only BlueTrack can read and linked to the event before it. **Verify**, at the top of the Audit Log page, checks every seal and lists any event that was changed or deleted outside BlueTrack, and any seal that was removed. The **AuditChainBroken** notification runs the same check once a day (section 7). The purge job deletes only sealed events and records what it deleted, so the check still passes after a purge. Nothing here is configurable. To record who changes the audit log as well, the installer can add a SQL Server audit to the Windows Security log (section 5, *Record audit log changes in the Windows Security log*).

### Backups

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Backup Folder | The folder that the Deployment page's **Backup App** button and scheduled backups write the full database backup to. | Blank | A dedicated local folder on a separate disk, listed in the allowed folders | A backup holds the whole database, including stored credentials, so keep it in a folder only specific accounts can read. |
| Scheduled backup time | A daily backup of the BlueTrack database at this server-local time. Blank means no scheduled backup. | Blank (off) | Set it, for a quiet hour outside the 02:00 to 04:00 import window, for example 01:00 or 05:00 | A daily backup protects your tracking data and audit log. A time away from the nightly import keeps a backup from competing with the load. |
| Keep scheduled backups (days) | After each scheduled backup, scheduled backup files older than this are deleted. Allowed: 1 to 3650. | 14 | 14, or your backup policy | Two weeks gives several restore points without filling the disk. Backups made with **Backup App** are never deleted automatically. |

Notes:

- A Backup Folder is required before a scheduled backup time can be saved.
- The folder must be a full path. It must be inside one of the server's allowed folders when those are set (see "Installer and server settings"); with none set, any local folder is allowed and network shares are refused.
- Before every backup, BlueTrack checks the folder. If Everyone, Users, Authenticated Users, Guests, Domain Users, Domain Guests, INTERACTIVE or ANONYMOUS LOGON can read the folder or a backup in it, BlueTrack removes that access. If it isn't allowed to change the permissions, it refuses the backup and records why.
- SQL Server writes the backup file using its own service account, which needs write access to the folder. The App Pool's account needs **Modify** on the folder to delete old scheduled backups. If SQL Server runs on another machine, the path is on that machine, and the old-backup clean-up is skipped.
- Scheduled backup files are named `<database>_scheduled_<UTC date and time>.bak`; manual backups are `<database>_<UTC date and time>.bak`.

### Risk scoring

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Risk Score Algorithm | How an account's risk score (0 to 1000) is combined from the targets and access groups it can reach. Dominant Plus Tail: the highest reachable risk plus a shrinking amount for each additional one. Combined Exposure: the chance that at least one reachable item is exposed. | Dominant Plus Tail | Dominant Plus Tail | Easier to explain to analysts: the score is mostly the single riskiest thing an account can reach. Choose Combined Exposure if you prefer scores that rise more steeply as access accumulates. |

Changing the algorithm changes the scores shown after the next recalculation, so review the bands on Admin > Risk Score at the same time.

### Decommissioned and ignored safes

Three name patterns, each with a type and a value. The types are **Off**, **Prefix** (matches the start of the name), **Suffix** (matches the end) and **Regex** (a regular expression). All three types ignore case. Regex needs SQL Server 2025; on an older SQL Server the Regex choice is unavailable. Values are up to 200 characters.

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Safe decommission pattern | Marks safes being retired (for example a `DEL_` prefix). They appear on the "flagged for deletion" reports, and their accounts are marked. | Off | Your safe-retirement naming convention, if you have one | Lets the reports show safes being retired without anyone flagging them by hand. |
| Account decommission pattern | Marks accounts whose own name shows they are due for deletion. | Off | Your account-retirement convention, if you have one | Same benefit at account level. Leave Off if account names carry no such marker. |
| Ignored safe pattern | Safes left out of the nightly import (for example a `ZZ_` prefix). Accounts already imported from them are flagged as deleted. | Off | Off, unless you have safes that should never be tracked | Excluding a safe hides its accounts from all tracking, so use it only for safes that are truly out of scope. |

Use **Test patterns** with sample names (one per line, up to 200) to see which names each unsaved pattern matches before you save. Prefer Prefix or Suffix: they are simpler to read and to check. A pattern that can't be used (for example an invalid regular expression) is refused when you save.

### Data feed schedule

These settings belong to **Admin > Data Sources** but are set here.

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Nightly run time | When every enabled data feed runs each day, server-local time (24-hour HH:mm). | 04:00 | 04:00 | It runs after the 02:00 Import and Load job, so feeds work on fresh CyberArk data. Keep it after that job finishes. |
| Keep run history (days) | How long data feed run history is kept. Allowed: 1 to 365. | 15 | 30 | A month of history makes it easier to see when a feed started failing. Shorten it only if the history grows large. |
| Business hours start / end | The hours in which **Run now** and **Run all** ask for confirmation first. The end must be later than the start; overnight windows aren't supported. | 07:00 to 18:00 | Your team's working hours | An import changes data people are working with, so a prompt during working hours prevents surprises. |
| Business days | The days the business hours apply to. | Mon to Fri | Your team's working days | Same reason as business hours. |

## 2. Sign-in and access

### Identity Providers

**Admin > Identity Providers** (permission `ManageIdentityProviders`). A new install has four provider rows: Windows Integrated (enabled, order 1), an OIDC row named "Microsoft Entra ID" (disabled, order 2), a SAML row named "Okta" (disabled, order 3) and DevFakeAuth (disabled, order 99). The display names are only labels; rename them to match your identity provider.

**Settings for every provider**

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Provider Type | WindowsIntegrated, OIDC, SAML or DevFakeAuth. | OIDC (new row) | WindowsIntegrated, OIDC or SAML | DevFakeAuth is for development and testing only (see below). |
| Display Name | The name shown for this method. | Blank (new row) | The name users know, for example your company's single sign-on name | Users recognise which sign-in to pick. |
| Enabled | Whether users can sign in with this provider. | Off (new row) | On only for providers you use | Each enabled provider is another way in. A disabled provider grants nothing, even to sessions signed in before it was disabled. |
| Display Order | The order of providers. When two enabled providers share a type, the lower number is used. | 0 (new row) | One enabled provider per type | Avoids any doubt about which configuration is used. |
| Secret (e.g. OIDC client secret) | The OIDC client secret. Write-only: leave blank to keep the stored one. | Blank | Set for OIDC | Stored encrypted on the server, never shown again. |

**OIDC settings**

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Authority | The identity provider's issuer address. | Blank | The issuer URL from your identity provider, `https://` | Must match the issuer in the provider's tokens. |
| Client ID | The application's client ID at the identity provider. | Blank | From your identity provider | Identifies BlueTrack to the provider. |
| Callback Path | The path the identity provider returns users to. | `/signin-oidc` | `/signin-oidc` | Change it only if your identity provider requires a different redirect path. Register the full redirect URI at the provider. |
| Groups Claim Type | The claim that carries the user's group names. | `groups` | The claim your provider uses | Group → Role Mapping matches against this claim; the wrong name means nobody gets a role. |

Enabling OIDC, or changing its Authority, Client ID or secret, takes effect only after the application restarts. If the OIDC row is enabled but its Authority or Client ID is empty, OIDC doesn't start.

**SAML settings**

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| SP Entity ID | BlueTrack's own SAML identity. | Blank | A stable value, for example `https://<your-BlueTrack-hostname>/BlueTrack` | The identity provider uses it as the audience; changing it later breaks sign-in until the provider is updated. |
| SP Certificate Thumbprint | BlueTrack's signing certificate, found by thumbprint in the App Pool account's own certificate store, then `LocalMachine\My`. | Blank | A certificate made for SAML signing | Its private key must be readable by the App Pool's account. Don't reuse the IIS TLS certificate. |
| IdP Entity ID | The identity provider's entity ID. | Blank | From your identity provider | Assertions are accepted only from this issuer. |
| IdP Single Sign-On Destination | The identity provider's sign-on URL. | Blank | From your identity provider, `https://` | Where users are sent to sign in. |
| IdP Single Logout Destination | The identity provider's Single Logout URL (Redirect binding). Optional. | Blank | Set it, and give the IdP BlueTrack's logout address (below) | With it set, **Sign out** of a SAML session also signs out at the identity provider, and signing out at the identity provider ends the BlueTrack session too (SAML Single Logout). Without it, Sign out ends only the BlueTrack session. |
| IdP Certificate Thumbprint | The identity provider's signing certificate, by thumbprint in the Windows certificate store. | Blank | The provider's current signing certificate, imported into `LocalMachine\My` | BlueTrack checks assertion signatures against it. Update it before the provider rotates its certificate. |
| Group Claim Type | The attribute that carries group names. | `http://schemas.xmlsoap.org/claims/Group` | The attribute your provider sends | Same as the OIDC groups claim. |

SAML changes take effect without a restart. Certificates are referenced by thumbprint only; there is no certificate upload.

**Default sign-in method**

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Default sign-in method | What happens when someone without a session opens BlueTrack: go straight to one method (WindowsIntegrated, OIDC or SAML), or "None", which lists the enabled methods. | WindowsIntegrated on a new install | Your main single sign-on method | Users go straight to the method you want them to use, including its multi-factor prompt. |
| Remember each browser's last method | When ticked, the sign-in page starts the method that browser last signed in with (while that method is still enabled) instead of the default. A browser that hasn't signed in since uses the default. Saved with the default method. | Off | Off, unless people regularly use different methods | Saves a click for people who sign in with a method other than the default. It is remembered in the browser (a cookie that grants nothing); signing out keeps it, and the sign-in page still lists every method after a sign-out. The methods are listed in each provider's Display Order. |

Each method also has a direct sign-in address (`/login/windows`, `/login/oidc`, `/login/saml`) that always uses that method. Use the OIDC or SAML address as the launch URL of the BlueTrack tile in your identity provider's portal, so users arriving from the tile aren't signed in with Windows instead.

**DevFakeAuth.** This provider simulates users for development and automated testing. Its sign-in works only when BlueTrack runs as the ASP.NET Core Development environment, which happens only on a developer's machine (`dotnet run` and the automated tests). A site installed on IIS always runs as Production, whatever the installer's `-Environment` answer, so DevFakeAuth sign-in never works there. Keep it disabled on every installed site. BlueTrack sends a "DevFakeAuth enabled too long" alert if it stays enabled for more than 7 days.

**Break-glass accounts** (at the bottom of the page). Emergency accounts, such as the server's local Administrator, whose credential you keep in CyberArk. They sign in like any other account; BlueTrack adds no separate sign-in. Each sign-in by one is recorded in the Audit Log as **BreakGlassLogon** and emailed to the **BreakGlassLogon** notification's recipients, every time, with no cooldown.

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Account | The account: `DOMAIN\name`, or `.\name` for a local account on the BlueTrack server (both stored as the account's SID), a SID, or an OIDC/SAML user identifier. | None | The accounts in your emergency procedure | Only these accounts' sign-ins alert. |
| Display name | How the account appears in the list and the alert. | The account as entered | A name people recognize | The alert email names it. |
| Notes | Free text. | Blank | Where the credential is kept in CyberArk | Helps whoever responds to the alert. |

### Group → Role Mapping

**Admin > Group → Role Mapping** (permission `ManageGroupRoleMapping`). Users get roles only through their groups; BlueTrack ignores any permissions an identity provider sends in its tokens.

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Provider | Which identity provider the mapping applies to. | Windows Integrated | The provider the group comes from | A mapping applies only to sign-ins through that provider. |
| Group Name | For Windows Integrated: an AD group as `DOMAIN\GroupName`, resolved to its SID when saved. For OIDC or SAML: the group name exactly as the provider sends it. | Blank | A dedicated group per BlueTrack role | Dedicated groups keep BlueTrack access separate from other access. OIDC and SAML names aren't checked when saved, so a typo never matches. |
| Role | The BlueTrack role the group grants. | First role in the list | The narrowest role that covers the job | Least privilege. A user in several mapped groups gets every permission from all of them. |

A new install maps `BUILTIN\Administrators` (local administrators of the server) to the Admin role, so the installer's smoke test and the first sign-in work. Once your own Admin group is mapped and tested, delete that mapping: local administrators of the server shouldn't automatically be BlueTrack administrators. Use the **Lookup / Test Tool** to check what a group grants before and after a change.

### Roles & Permissions

**Admin > Roles & Permissions** (permission `ManageRolesAndPermissions`). The list of permissions is fixed. You choose which permissions each role holds, and you can add roles.

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Role Name | The role's name. | Blank (new role) | A name that says what the role is for | Clear names make reviews easier. |
| Description | A free-text description. | Blank (new role) | What the role is for and who holds it | Helps access reviewers. |
| Notification Email | An email address for the role. Used when the role is a notification type's target role. | Blank | A team mailbox, if the role receives alerts | Alerts reach a monitored mailbox rather than one person. |
| Permissions | The permissions the role grants. | See below | The fewest the role's job needs | Least privilege. |

**Permission catalog**

| Permission | What it allows | Seeded in |
|---|---|---|
| `ViewDashboard` | View the dashboard and read tracked data | Admin, Viewer, Analyst, Approver |
| `EditAccountProgress` | Edit an account's Blueprint progress record, import changes to Accounts from a file, and set risk score overrides (one account or in bulk on Reports > Risk Score) | Admin, Analyst, Approver |
| `ApproveExceptions` | Add or approve a risk exception | Admin, Approver |
| `ConfirmReconciliation` | Confirm an account reconciliation match | Admin, Approver |
| `ManageTargets` | Manage the Target inventory; also opens Target Match Review and Import Mapping Profiles | Admin, Analyst |
| `ManageAccessGroups` | Manage Access Groups for risk scoring | Admin, Analyst |
| `ViewRiskReport` | View the account risk score report | Admin, Analyst |
| `ManageRiskScoreBands` | Manage Admin > Risk Score: bands, default scores and finding values | Admin |
| `ViewDiscoveredAccounts` | View the Discovered Accounts and DNA Accounts reports | Admin |
| `ManageDiscoveredAccounts` | Accept or dismiss a Discovered Account, and **Track in BlueTrack** on Reports > DNA Accounts | Admin |
| `ViewDnaSensitiveColumns` | See the DNA account columns marked as needing it on Admin > Data Sources, and open and export Reports > DNA Hard-Coded Credentials | Admin |
| `ViewRiskExceptionSodReport` | View the Risk Exception segregation-of-duties report | Admin |
| `DeleteAccounts` | Delete and undelete accounts in BlueTrack, with a reason | Admin |
| `CuratePlatformMapping` | Curate the platform to account-type mapping | Admin |
| `CurateApplicationMapping` | Manage Applications and Application ↔ Safe Mapping | Admin |
| `ManageFieldMetadata` | Manage Field Metadata | Admin |
| `ManageDataSources` | Manage, test and run data feeds, the CyberArk sources, AD account discovery, DNA imports, DNA tracking rules and DNA settings | Admin |
| `ViewAuditLog` | View and export the audit log | Admin, Auditor |
| `ViewDeploymentInfo` | View the Deployment page, health checks and backup status, and Admin > Power BI | Admin |
| `TriggerBackup` | Run **Backup App** on the Deployment page | Admin |
| `ManageApplicationConfiguration` | Change Global Application Configuration | Admin |
| `ManageIdentityProviders` | Configure identity providers | Admin |
| `ManageGroupRoleMapping` | Manage Group → Role Mapping | Admin |
| `ManageRolesAndPermissions` | Manage roles and their permissions | Admin |
| `ReloadRights` | Reload another user's rights | Admin |
| `ManageSecretsStore` | Configure Secrets Store backends | Admin |
| `ManageCredentials` | Manage credentials and LDAP configuration | Admin |
| `ManageNotifications` | Configure SMTP, notification types and recipients | Admin |

**Seeded roles**

| Role | Permissions on a new install | Recommended use |
|---|---|---|
| Admin | Every permission | A small group of BlueTrack administrators only. |
| Viewer | `ViewDashboard` | Anyone who only needs to see progress. |
| Analyst | `ViewDashboard`, `EditAccountProgress`, `ManageTargets`, `ManageAccessGroups` | Staff who update onboarding progress and risk inventory. |
| Approver | `ViewDashboard`, `EditAccountProgress`, `ConfirmReconciliation`, `ApproveExceptions` | Staff who approve risk exceptions and confirm matches. |
| Auditor | `ViewAuditLog` | Auditors; gives no access to tracked data. |

Recommendations:

- **Split the Admin role.** Admin holds every permission. Consider separate roles for security settings (identity providers, group mapping, roles, secrets, credentials) and for day-to-day data administration (data sources, field metadata, risk bands, application mapping), so no one holds more than their job needs.
- **Grant reports by need.** The risk score report, Discovered Accounts and the segregation-of-duties report are Admin-only on a new install. Add them to Analyst or Approver only if those staff use them.
- **Keep `DeleteAccounts` narrow.** It removes accounts from tracking. Grant it only to people who should do that.
- **Mind `ManageTargets`.** It also lets Analysts edit Import Mapping Profiles, which change how data feeds read files. Remove it from Analyst if that should be an administrator task.
- **Sending a stored credential to a new SMTP server** needs `ManageCredentials` as well as `ManageNotifications`, so keep `ManageCredentials` with security administrators.
- A role still mapped to a group can't be deleted. Permission changes apply to every user on their next request.
- Each page opens only with its permission; otherwise the user sees **Access Denied**. A signed-in user with no role at all sees only a "You don't have access to BlueTrack yet" page naming their account, with **Check again** to re-read their roles once you've mapped one.
