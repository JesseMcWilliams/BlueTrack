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

Every Admin page needs its own permission (see "Roles & Permissions"). A user sees only the Admin pages their roles allow.

This guide has two parts. This file covers sections 1 to 5. Sections 6 (Secrets Store and Credentials, including LDAP), 7 (Notifications) 8 (SIEM Forwarding) and 9 (API Keys) are in [Admin_SettingsReference_Notifications.md](Admin_SettingsReference_Notifications.md). Both are published together as the Administrator Settings Guide.

## 1. Global Application Configuration

**Admin > Global Application Configuration** (permission `ManageApplicationConfiguration`). The page is one form with one **Save** button. Every change is recorded in the audit log with the old and new values.

### General

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Site URL | The address people open BlueTrack at, for example `https://bluetrack.company.com`. Notification emails link to the pages they mention under it; blank means they name the pages instead. Must start with `https://` (`http://` only for localhost), with no user name, query or `#` part. | Filled in by the installer (`https://<Hostname>`, plus `:<HttpsPort>` when that isn't 443) when blank | The address users type, matching the certificate | Links in emails only work if they point where users can reach the site. The installer never changes a value you've set; blank it here to clear it. |
| Idle Timeout (minutes) | Signs a user out after this many minutes without activity in any BlueTrack tab. OIDC and SAML sessions also end on the server after this long without a request, even if the tab was closed. | 30 | 15 to 30 | Short enough that an unattended screen doesn't stay signed in to privileged-access data, long enough not to interrupt normal work. Allowed: 5 to 720. |
| Breadcrumb Position | Where the breadcrumb trail appears on every page: Top Left or Top Right. Applies to all users. | Top Left | Top Left | A matter of taste; Top Left is the usual place for breadcrumbs. |
| Exception ID Pattern | The format of new Risk Exception IDs. Tokens: `{yyyy}` (year), `{yy}` (two-digit year), `{seq:0000}` (running number, padded to the number of zeros). | `EXC-{yyyy}-{seq:0000}` | Keep the default, or your own prefix with a year token and `{seq:...}` | The running number restarts at 1 each year, so a pattern without a year token, or without `{seq:...}`, produces duplicate IDs, which are refused. |
| Account Progress Lock Timeout (minutes) | How long an Account Progress edit lock survives after the editor's page stops renewing it, for example when a browser is closed. Allowed: 1 to 720. | 30 | 30 (10 to 30 suits most teams) | An open edit page renews its lock every minute, so this only controls how long an abandoned lock blocks others. Keep it well above 1 minute. |
| Enforce segregation of duties on Risk Exception approval | When on, the person who approved a Risk Exception cannot also be the one who links it to an account. | Off | On, if you have separate staff | Stops one person both approving and applying an exception. Leave it off for a small team, and review the Risk Exception segregation-of-duties report instead. |
| Bulk edit limit (accounts) | The most accounts one bulk edit, bulk action, delete or undelete may change. Allowed: 1 to 10,000. | 500 | 500 | Large enough for normal work, small enough that a mistaken bulk change is limited. Raise it only for planned clean-ups. |
| Export Max Rows | The most rows one report export (**Export CSV** or **Export Excel** on any report, or on the Audit Log Viewer) may hold. When more rows match the filters, the export is refused with the number of rows, never cut short. Allowed: 1 to 1,000,000. | 100,000 | 100,000 | Large enough for any normal report, small enough that one export can't tie up the server. Users narrow the filters when they hit it. |
| API key default lifetime (days) | How long a new API key lasts before it expires. Allowed: 1 to 365. You can choose another lifetime for each key when you create it (see section 9, API Keys). | 90 | 90 | Short enough that a forgotten key stops working, long enough that you aren't replacing keys constantly. Raise it only if a system can't be given a new key as often. |
| API requests per minute, per key | The most requests one API key may make in a minute. Allowed: 1 to 100,000. Over it, the API answers "too many requests" (HTTP 429) until the minute is up. | 600 | 600 | Far above what a normal script needs, low enough that a runaway loop can't flood the server. Raise it for a busy integration. |
| API key expiry reminder lead time (days) | How many days before an API key expires its owner is emailed (the owner is emailed again on the day, and the administrators on the day). Allowed: 1 to 90. | 7 | 7 | A week is enough to get a new key into a script. Lengthen it for scripts that are changed only at set times. |

**Idle timeout.** In the browser, BlueTrack signs the user out after this many minutes with no mouse, keyboard or touch activity in any of its tabs, and the sign-in page says why. OIDC and SAML sessions are also ended on the server once no request has arrived for this long, so a closed tab doesn't leave a usable session behind; each OIDC session also ends after 8 hours regardless. Windows sign-in has no session to end: if Windows Integrated is the default method, the user is signed in again as soon as they leave the sign-in page. Separately, a user's cached roles and permissions last at most 15 minutes, and any change to a role, group mapping or identity provider clears every user's cache.

### Audit

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Audit Retention (days, blank = keep forever) | How many days of audit log to keep. Older entries are deleted by the nightly Audit Log Purge job. | Blank (keep forever) | Depends: your audit retention policy, for example 365 or more | Keeping the log forever is safest for investigations but grows the database. Set it to the period your policy requires. |
| Log read/view events | Also records a "viewed" event each time someone opens an Account Progress record or a Risk Exception. List pages and reports are never logged. | Off | Off, unless your policy requires read auditing | Write, approval and logon events are always logged. Read logging adds an entry for every record opened, which makes the log much larger. Report exports are always logged (event type `ReportExport`), whatever this setting says. |

**Audit retention needs the purge job.** Nothing is deleted until the SQL Server Agent job "Audit Log Purge" is installed. The installer does not install it: run `Database/Manual/05_BlueTrack_ScheduleAuditLogPurgeJob.sql` by hand (it runs nightly at 03:00). Until a retention value is set, the job deletes nothing. The application's own database account cannot delete audit entries; only the job can. While SIEM forwarding is on (section 8), the job keeps events not yet forwarded, however old.

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

Changing the algorithm changes the scores shown after the next recalculation, so review the Risk Score Bands at the same time.

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
| `EditAccountProgress` | Edit an account's Blueprint progress record | Admin, Analyst, Approver |
| `ApproveExceptions` | Add or approve a risk exception | Admin, Approver |
| `ConfirmReconciliation` | Confirm an account reconciliation match | Admin, Approver |
| `ManageTargets` | Manage the Target inventory; also opens Target Match Review and Import Mapping Profiles | Admin, Analyst |
| `ManageAccessGroups` | Manage Access Groups for risk scoring | Admin, Analyst |
| `ViewRiskReport` | View the account risk score report | Admin, Analyst |
| `ManageRiskScoreBands` | Manage Risk Score Bands | Admin |
| `ViewDiscoveredAccounts` | View the Discovered Accounts report | Admin |
| `ManageDiscoveredAccounts` | Accept or dismiss a Discovered Account | Admin |
| `ViewRiskExceptionSodReport` | View the Risk Exception segregation-of-duties report | Admin |
| `DeleteAccounts` | Delete and undelete accounts in BlueTrack, with a reason | Admin |
| `CuratePlatformMapping` | Curate the platform to account-type mapping | Admin |
| `CurateApplicationMapping` | Manage Applications and Application ↔ Safe Mapping | Admin |
| `ManageFieldMetadata` | Manage Field Metadata | Admin |
| `ManageDataSources` | Manage, test and run data feeds | Admin |
| `ViewAuditLog` | View and export the audit log | Admin, Auditor |
| `ViewDeploymentInfo` | View the Deployment page, health checks and backup status | Admin |
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

## 3. Data and risk scoring

### Data Sources (feeds)

**Admin > Data Sources** (permission `ManageDataSources`). Each feed imports the newest matching CSV file from a folder, using the same imports as the Bulk Actions uploads. The schedule and business hours are on Global Application Configuration.

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Name | The feed's name, up to 200 characters. | Blank | Say what the file is and where it comes from | Makes run history easy to read. |
| Import | What the file holds: Target inventory, Access group inventory, Access group → target map, Account → access group membership, Account → target map, Applications, Safe → application assignments, Account → application assignments, or Account inventory (accounts from another system). | None | The import the file matches | The file must have the columns of that import's Bulk Actions template, or match a mapping profile. Account inventory has no template: see below. |
| Source system | Account inventory only: the system the accounts come from. **+ New source system** adds one (a name, and a short code of letters, digits and underscores). | None | One source system per system you import from | Each source system can have only one account inventory feed, because its accounts missing from the file are marked deleted. |
| Skip group rows | Account inventory only: skip rows whose `UserName` starts and ends with `*` and contains the word `group`, in any case. A pending-accounts export lists its local and domain groups that way, for example `*Domain Admins Group*`. | Off | On for a pending-accounts export | Skipped rows aren't accounts and aren't errors; the run's summary says how many were skipped. A group an earlier run imported is marked deleted by the next run. |
| Mapping profile | For the five risk-scoring imports: which Import Mapping Profile translates the file's column names. | None | Choose the profile when the file isn't in template format | None means the file uses the template's own column names. |
| Folder path | A full local or UNC path, up to 500 characters. | Blank | A dedicated feed folder inside the server's allowed folders | The App Pool's account reads it, so it needs read access. Network shares need to be in the allowed folders. |
| File name pattern | The file to pick, up to 260 characters. A date format in braces, such as `{yyyy-MM-dd}`, becomes today's date; `*` and `?` are wildcards. | Blank | Include the date, for example `targets_{yyyy-MM-dd}.csv` | A dated name stops yesterday's file being imported again by mistake. |
| Run order | The order enabled feeds run in. | 0 | Inventories first, then the maps that refer to them | A map that refers to targets or groups loads cleanly once they exist. |
| Enabled (runs nightly) | Whether the feed runs at the nightly run time. | On | On once **Test** passes | Test shows the file a run would pick and any missing required columns. |

The matched file is left in place after import. Only one run happens at a time.

**Account inventory feeds** add another system's accounts to Account Progress, at **Discovered**, under the feed's source system. The file needs `UserName` and `Address`; `AccountName` (default `username@address`), `PlatformName` (a CyberArk platform's name or ID, used to suggest the account type), `OwnerName` and `Notes` are optional. An account is identified by its user name and address (ignoring case) within the source system. The two together can be up to 400 characters; a longer row is reported as a row error. On later runs, the account's name, address and platform are updated from the file; its owner, notes and stage are BlueTrack's and aren't overwritten. An account missing from the file is marked deleted, and comes back if it reappears. A file with no valid rows fails the run and changes nothing. These accounts never move past Discovered on their own; after each run, an account CyberArk also has (the same user name and address, ignoring case and spaces) is **linked** to the CyberArk account and moves to Onboarded to Vault, recorded in the Audit Log. A linked account is hidden from Account Progress and the reports (the **Linked feed accounts** filter shows it). Feeds run after the nightly CyberArk load, so an account onboarded today is linked that night. An account that only nearly matches (the same user name, with addresses that agree by host name or DNS) is suggested on **Reports > Possible Account Matches** instead, for someone with `ConfirmReconciliation` to link or dismiss. The DNS lookups are made by the App Pool's account during the run.

### CyberArk sources

**Admin > Data Sources**, the "CyberArk sources" section at the top, then **Edit CyberArk sources** (permission `ManageDataSources`). These settings are read by the nightly Import+Load job's Import step (02:00); changes apply from the next nightly run. There is no Run now for them. The installer sets them on first install (section 5, "Nightly import"); until they're saved, the page shows "Not set up yet" and the nightly import fails.

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Import Privilege Cloud | Import the Privilege Cloud CSV exports. At least one source must be on. | Installer's answer | On if you have Privilege Cloud | A source that is on but missing fails the job each night. |
| Export folder | The folder holding the exports, up to 400 characters. Must be inside the server's allowed folders (without any set: local folders only, no network shares). | Installer's answer | A local folder on the SQL Server | SQL Server's service account reads the files, so it needs read access. |
| Platforms, Users, Groups, Local group members, Safes, Accounts, Entitlements file | The file name of each export, up to 200 characters. A date format in braces, such as `{yyyy-MM-dd}`, becomes the run's date; `*` and `?` aren't allowed. | `Export_PlatformsList.csv`, `Export_UsersList.csv`, `Export_GroupsList.csv`, `Export Local Group Members {yyyy-MM-dd}.csv`, `Export_SafesList.csv`, `Export_AccountsList.csv`, `Export Entitlements {yyyy-MM-dd}.csv` | The defaults, unless your exports are named differently | SQL Server needs the exact file name. |
| Import Self-Hosted | Import from the Self-Hosted EVD database. | Installer's answer | On if you have Self-Hosted | As above. |
| EVD database | The EVD database on BlueTrack's SQL Server instance: letters, digits and underscores only. | Installer's answer | Your EVD database | Read with a cross-database query, so it must be on the same instance. |

**Test** checks the values on the page, saved or not: that the EVD database exists on BlueTrack's SQL Server, and that the export folder and today's seven files exist as the App Pool's account sees them. It also reads the header row of each Privilege Cloud file and reports any column the import needs that the file lacks ("the import would fail"), and lists optional columns that will import as empty. During the import it's SQL Server's service account that reads the files, so it needs read access too. Every save is recorded in the audit log, with each changed field.

**Columns are read by name.** The Privilege Cloud import finds each column by its header, so the order of the columns doesn't matter, columns BlueTrack doesn't use are ignored, and both the older and the current Users and Groups export layouts load. A missing optional column loads as empty. A missing required column fails the nightly import with a message naming the file and the columns; the **NightlyImportFailed** email (section 7) carries that message. The Self-Hosted import is unchanged.

### Import Mapping Profiles

**Admin > Import Mapping Profiles** (permission `ManageTargets`). A profile maps a source file's column names to BlueTrack's internal field names, for the five risk-scoring imports (TargetInventory, AccessGroupInventory, AccessGroupTargetMap, AccountAccessGroupMembership, AccountTargetMap).

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Feed Type | Which import the profile is for. | TargetInventory | The import the file feeds | A profile only applies to its own import. |
| Profile Name | The profile's name; unique per feed type. | Blank | The source tool and version, for example "CMDB export v2" | A new file format gets a new profile. |
| Active | Whether the profile is offered when a data feed or a Bulk Actions upload chooses a profile. | On | On for the profiles in use | A profile is used only when it's chosen; none chosen means the template's own column names. |
| Description | Free text. | Blank | Where the file comes from | Helps the next administrator. |
| Source Column | The column header exactly as it appears in the file. | Blank | Copy it from the file | Must match the header. |
| Internal Field | The BlueTrack field the column feeds. | Blank | A field name from that import's template | Only the template's field names are used. |
| Required | A row with this value missing is reported as an error. | Off | On for identifying fields | Catches incomplete rows instead of loading them. |
| Default | The value used when the column is blank or missing and not required. | Blank | Only where a sensible default exists | A default fills gaps silently, so use it sparingly. |

A file in the template's own format needs no profile.

**Fill a profile from a sample file.** Under **Detect from a sample file** on the profile's edit page, pick a **Sample CSV file**. Only its header row is read, in your browser; the file isn't uploaded. Each field is matched to a column with the same name (ignoring case, spaces, underscores and punctuation), then to a common alternative name, and the page lists what it filled in, what it couldn't match and the columns it didn't use. A field you've already mapped to a different column is changed only if you choose **Replace with the file's columns** (**Keep my mappings** leaves it). Check the result before you save.

**Bulk Actions column check.** On the Targets and Access Groups **Bulk Actions** pages, with no mapping profile chosen, picking a file lists the columns read by name and any required ones missing. **Create a mapping profile from these columns** opens a new profile filled in from the file's headers.

### Target Match Review

**Admin > Target Match Review** (permission `ManageTargets`) has no settings. When an imported target matches an existing one only weakly (for example by IP address alone, which DHCP or NAT can reuse), the row waits here instead of being merged. Each row shows the identifier, the candidate target and the imported target (its name, source and risk score). Resolve it with:

- **Link**: keeps the imported row as its own target, linked to the candidate, or to another target found by typing at least 2 characters of its name or any identifier in **Link To**. The IP address stays on the existing target. Anything that reaches either target counts once, at the risk score of the target it's linked to.
- **New Target**: keeps it as a separate, unlinked target.
- **Ignore**: creates nothing.

Every choice is remembered. A later import of the same row (the same name and discovery source) goes straight to the target that Link or New Target made, and an ignored row is skipped (the Bulk Actions result says how many). Linked targets are hidden on **Targets** unless **Linked targets** is set to **Show**. The **Edit Target** page shows the target a linked target belongs to, or a target's **Linked Targets**, with **Unlink**: an unlinked target counts at its own risk score again and isn't linked again automatically. Every resolution, link and unlink is recorded in the audit log.

### Field Metadata

**Admin > Field Metadata Management** (permission `ManageFieldMetadata`). The list of fields shown on the Account Progress edit form. A new install has ten fields: Blueprint Stage and Status (both required), Risk Level, Account Type, Source of Record, Owner Name, Business Unit, Target Remediation Date, Actual Completion Date and Notes.

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Field Name | The underlying field. | As seeded | Don't change the seeded names | The edit form matches each field to its stored value by this name. |
| Display Label | The label users see. | As seeded | Your organization's terms | Change labels freely; it changes nothing stored. |
| Field Type | How the input is shown: `Text`, `Date`, `Dropdown` or `TextArea`. | As seeded | Keep the seeded type | The type must suit the stored value. |
| Reference Table | For Dropdown fields, the list the choices come from. | As seeded | Keep the seeded table | The choices come from that list. |
| Required | The form can't be saved without a value. | Stage and Status | Stage and Status, plus any your process requires | Required fields keep the reports complete. |
| Display Order | The order of fields on the form. | 10, 20 ... 100 | Your preferred order | Gaps of 10 make reordering easy. |
| Required permission | The permission needed to change this field, on top of `EditAccountProgress`. Enforced on the edit page, in bulk edit and in the Risk Exception import. | None | None, unless a field needs tighter control | Set with **Required Permission** on the Edit Field page, and shown in the field list. |

### Risk Score Bands

**Admin > Risk Score Bands** (permission `ManageRiskScoreBands`). Bands turn a risk score (0 to 1000) into a label.

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Name | The band's label. | Low, Medium, High, Critical | Your organization's risk terms | Matches language people already use. |
| Min Score / Max Score | The band's score range. Min must not be greater than Max; bands may not overlap. | 0-250, 251-500, 501-750, 751-1000 | Cover 0 to 1000 with no gaps | A score in a gap gets no label. |
| Risk Order | The sort order, lowest risk first. | 10, 20, 30, 40 | Keep gaps of 10 | Reports sort by it. |

### Account Classification

**Admin > Account Classification** (permission `ManageDiscoveredAccounts`). Separates a person's day-to-day account from their secondary and admin accounts on **Reports > Discovered Accounts**: each discovered account gets an **Account Class** (Primary, Secondary, Admin or Unknown) and an **Owner** (the person's primary account in the same domain). Nothing changes until you turn it on and add rules.

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Classify discovered accounts | Works out the class and owner. Off: the columns stay empty (a manual override still shows) and discovery reads no extra attributes. | Off | On, once rules are added | The next discovery run reads the attributes the rules need. |
| Class when no rule matches | Unknown or Primary. | Unknown | Unknown | Primary would also make every unmatched service account a person's main account. |
| Find the owner by a shared employee ID | For an account that isn't Primary, the owner is the one enabled account in the same domain with the same employee ID whose class is Primary; for a Secondary or Admin account with none, the one that matches no rule. If more than one could be, the owner is left empty (ambiguous). | Off | On if your directory fills the ID on both accounts | Finds owners whose admin account isn't named after them. |
| Employee ID attribute | employeeID or employeeNumber. | employeeID | Whichever your HR feed fills | |

**Rules.** Each rule has a condition and a class (Primary, Secondary or Admin), can be enabled or disabled, and is edited on its own page (click its condition). Attribute rules are checked first, then Name rules, then OU rules; within a type, in the order shown (**Up** / **Down**). The first enabled rule that matches sets the class.

| Rule type | Condition | Example |
|---|---|---|
| Attribute | One of adminCount, employeeType, extensionAttribute1 to 15, description, title, department; **equals**, **contains**, **starts with** (case ignored) or **is set**. No other attribute can be used. | adminCount equals 1 → Admin; extensionAttribute5 equals Admin → Admin |
| Name | sAMAccountName or userPrincipalName **starts with** (prefix), **ends with** (suffix) or **matches pattern** (`*` any text, `?` one character). | sAMAccountName starts with `adm.` → Admin; pattern `*-a` → Secondary |
| OU | The distinguished name **contains** or **ends with** an OU path (case and spaces after commas ignored). | contains `OU=Admin Accounts` → Admin |

A Secondary or Admin **Name** rule on sAMAccountName also gives the owner: the name without the prefix or suffix (`adm.jsmith` is owned by `jsmith`), or the text a pattern's single `*` matched. The owner is used only if that account was found: it's a discovered account, or the discovery run read it from AD. The name rule is tried before the employee ID. A Primary account is its own owner.

Saving the settings, or adding, changing, moving or deleting a rule, reclassifies every discovered account at once from what the last discovery run read; it doesn't contact AD. **Reclassify Now** does the same on request. An owner outside the discovered accounts for a new name rule appears after the next discovery run. Every change is recorded in the Audit Log (event **AccountClassification**).

### Application ↔ Safe Mapping

**Admin > Application ↔ Safe Mapping** (permission `CurateApplicationMapping`) has no settings. It holds data: each application's code, name, description, owner and technical contact, and which application each safe belongs to. Keep it current, because the risk reports group accounts by application. An account can also be assigned to an application on its own edit form (**Application**), in addition to its safe's; that is how an account with no safe, such as a pending account, belongs to one. When an account's own application and its safe's differ, both apply: an Active risk exception on either one covers the account, and Reports > Trends' Application filter counts it under either.

## 4. Deployment page and health checks

**Admin > Deployment** (permission `ViewDeploymentInfo`; **Backup App** also needs `TriggerBackup`) shows the environment name (the installer's `-Environment` answer), the version and build time, the health checks, SQL Server's last backup times and recent BlueTrack backups.

**Version.** The Deployment page and **About** (in the menu with your name) show the same version. A release shows its number, for example `1.1.0`. A build made between releases shows the release it follows, how many changes came after it and which one, for example `1.1.0-dev.12+abc1234`, ending `.dirty` if it was built with uncommitted changes. A copy downloaded by `Update-BlueTrack.ps1` shows the plain version number.

| Health check | Healthy means | A problem means | What to do |
|---|---|---|---|
| SQL Server | BlueTrack connected to its database. | Unhealthy: the connection failed. | Check SQL Server is running and the App Pool's account has its login. |
| Secrets Store | The active backend has an implementation in this build. | Unhealthy: no implementation, for example CyberArkCP without the SDK. | Make another backend active. This check doesn't fetch a secret; use **Test Connection** for that. |
| Identity Providers | At least one provider is enabled, and enabled OIDC and SAML rows have their required fields. | Unhealthy: nothing is enabled. Degraded: OIDC lacks Authority or Client ID; SAML lacks SP Entity ID, IdP Entity ID or sign-on destination; or the SAML SP certificate isn't installed or the App Pool's account can't use its private key. | Fill in the fields or disable the provider; for the SP key, see `Admin_Installation.md`, "SAML signing certificate". The check doesn't test a real sign-in or an OIDC secret: always test one by hand. |
| Backup Folder | No folder is set, the folder can't be seen from this server, or only specific accounts can read it. | Degraded: a broad group can read the folder, or the App Pool's account can't read its permissions. | The next backup removes broad access, or is refused if it can't. Fix the permissions so backups aren't refused. |
| SIEM Forwarding | Forwarding is off, or on and keeping up. | Degraded: the last send to the collector failed, or the oldest event waiting to be sent is more than 15 minutes old. | Check the error on **Admin > SIEM Forwarding** and use **Send Test Event**; check the collector is running and the firewall allows the server out to it. Waiting events are sent once it's reachable. |

**Backup App** writes an immediate full backup to the Backup Folder; it is never deleted automatically. Recommended: run it before any configuration change you might want to undo, and copy important backups off the server.

## 5. Installer and server settings

These are chosen when running `Deploy\Install-BlueTrack.ps1`. Each can be a parameter, a value in an answer file (`-ConfigFile`, see `Deploy\answers.sample.json`), or answered at a prompt. Never put a password in an answer file; the SQL credential is always prompted.

### Environment and database

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| `-Environment` | Development, Test, Staging or Production. A label: it names the default install folders and is shown on the Deployment page, and a Production install is never offered test data. It doesn't change how BlueTrack runs: on IIS it always runs as Production, with development-only sign-in off. | Prompted | Production for live use; Test or Staging for others | The label tells people which environment they're looking at. |
| `-SqlServerInstance` | The SQL Server instance. | Prompted | The local instance on the BlueTrack server | BlueTrack is designed for a single server. |
| `-DatabaseName` | The database name. | `BlueTrack` | `BlueTrack` | Scripts and jobs name the database; keep the default unless you host several environments on one instance. |
| `-UseWindowsAuth` | Connect to SQL Server with Windows authentication. | `$true` | `$true` | No password is stored. With SQL authentication the password sits in the settings file. |
| `-SqlCredential` | A SQL login, only when Windows authentication is off. | Prompted | Not used | See above. |
| `-TrustServerCertificate` | Skip checking SQL Server's TLS certificate. | Unset: trusted only when SQL Server is on this machine | Leave unset | A remote SQL Server's certificate is then checked, protecting the connection. |
| `-GrantAppPoolSqlAccess` | Give the App Pool's account a SQL login and only the rights BlueTrack needs (not `db_owner`). | Prompted | `$true` | The site can't reach the database without it. The account can add audit entries but not change or delete them. |
| `-AppPoolSqlLogin` | Override the account that gets the login. | Worked out from the App Pool | Leave unset | Only needed on a server that isn't domain-joined, or similar. |
| `-SeedTestData` | Load test users and synthetic data. | Prompted (never asked for Production) | `$false` | Test data and test sign-ins don't belong in a live install. |

### Nightly import

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| `-InstallNightlyJob` | Install the nightly Import and Load SQL Server Agent job (runs at 02:00). | Prompted | `$true` once export files are in place | Without it, data is loaded only by hand. |
| `-ImportSources` | Which CyberArk sources the job imports: Both, PrivilegeCloud or SelfHosted. Sets the CyberArk sources on first install only. | Prompted | Only the sources you have | A source that is switched on but missing fails the job each night. |
| `-ExportFolderPath` | The Privilege Cloud export folder, as seen by the SQL Server service account. First install only. | Prompted if needed | A local folder on the SQL Server | SQL Server reads it directly. |
| `-EvdDatabaseName` | The Self-Hosted EVD database, on the same SQL Server instance. First install only. | Prompted if needed | Your EVD database | Needed for the Self-Hosted import. |

After the first install, these three are changed on **Admin > Data Sources** ("CyberArk sources", in section 3); re-running the installer doesn't overwrite what's saved there.

### IIS and certificate

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| `-SiteName` | The IIS site name. The App Pool is named `<SiteName>-AppPool`. | `BlueTrack` | `BlueTrack` | Also names the installer's saved answers. |
| `-ApiInstallPath` / `-WebInstallPath` | Where the API and web pages are installed. | `<system drive>\inetpub\BlueTrack\<Environment>\api` and `...\web` | The defaults | Kept separate per environment. |
| `-Hostname` | The site's host name. | Prompted | The DNS name users will type | It must match the certificate. |
| `-HttpPort` / `-HttpsPort` | The site bindings. | 80 / 443 | 443 for HTTPS | Users should always use HTTPS; the OIDC sign-in cookie is never sent over plain HTTP. |
| `-CertificateThumbprint` | An existing certificate in `LocalMachine\My` for the HTTPS binding. | Prompted; blank generates a self-signed certificate | A certificate from your organization's CA, with a Subject Alternative Name for the host name | Browsers trust it without warnings; Chrome and Edge reject a certificate without a SAN. With a CA-issued certificate the installer also turns on HSTS. |
| `-GenerateSelfSignedCert` | Create a self-signed certificate. | Off | Development and Test only | Browsers warn about it, and users learn to click through warnings. |
| `-ResetIis` | Run `iisreset` after the IIS steps. | Prompted | `$true` for a first install, if no other site is busy | Makes every IIS change take effect, but briefly stops every site on the server. |
| `-Force` | Recreate the App Pool, site and application even if they exist. | Off | Off | Only to repair a broken IIS setup. |

To replace a certificate later, run the installer with `-Step Iis.Site -CertificateThumbprint <new thumbprint>`.

### Allowed folders and other server settings

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| `-AllowedFileRoots` | The only folders data feeds may read and backups may be written to. Written to `FileAccess:AllowedRoots` in the settings file. | None: any local folder, no network shares | List only your feed folder and backup folder, for example `'D:\Feeds','D:\Backups\BlueTrack'` | The server reads and writes these paths as itself, so a short list stops an admin page reaching other files or shares. Add a share here only if you need one. |
| `-BackupFolder` | Where the installer's automatic pre-update backup goes, when the database already exists. | Prompted only if needed | The same backup folder | Keeps all backups together under the same access rules. |
| `-SkipPreDeployBackup` | Skip that automatic backup. | Off | Off | The backup is your way back if an update fails. |
| `-SkipSmokeTest` / `-SkipPrerequisiteInstall` | Skip the post-install check, or only report missing prerequisites. | Off | Off | The smoke test confirms the site works before users arrive. |
| `-PrerequisiteSource` / `-InstallerSourcePath` | Where prerequisite installers come from: Auto, Offline, Winget or Download, and an offline folder. | Auto | Offline, from a folder you've checked, for servers without internet | Every installer must also carry a valid vendor signature, whichever source is used. |

**Settings file.** The installer's `Db.AppSettings` step writes `appsettings.Site.json` into the API folder with four settings: the database connection string, `Security:EnableHsts` (on only when the certificate isn't self-signed), `FileAccess:AllowedRoots` and `BlueTrack:EnvironmentName` (the `-Environment` answer, shown on the Deployment page). BlueTrack always reads this file, whatever the environment. The web pages can't change any of it. To change allowed folders later, re-run the installer with the new `-AllowedFileRoots` (steps `Db.AppSettings,Iis.WebConfig`: the API reads the file when it starts, and `Iis.WebConfig` recycles the App Pool) rather than editing the file by hand, so the saved answers stay in step. When the connection string holds a SQL password, the installer limits the file to Administrators, SYSTEM and IIS worker processes.

Earlier installers wrote `appsettings.<Environment>.json` instead, which BlueTrack read only on a Production install. `Db.AppSettings` removes that old file. An old `appsettings.Production.json` that holds settings the installer didn't write is kept, with a warning naming them: move those settings into `appsettings.Site.json` by hand, then delete it. After this upgrade, a site with a trusted certificate starts sending HSTS.

**Site URL.** The installer's `Db.SiteUrl` step sets the Site URL on Global Application Configuration to `https://<Hostname>` (with `:<HttpsPort>` when that isn't 443), only when it's blank.

### App Pool identity

The installer creates the App Pool with the built-in `ApplicationPoolIdentity`. You can change it afterwards in IIS Manager (the App Pool's **Advanced Settings > Identity**).

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| App Pool identity | The Windows account BlueTrack runs as: it connects to SQL Server, reads feed folders, manages backup folder permissions and decrypts User-scope credentials. | `ApplicationPoolIdentity` | A dedicated gMSA (group managed service account) | Its access belongs to BlueTrack alone, it is the same account whether SQL Server is local or remote, and Windows manages its password. With a remote SQL Server, `ApplicationPoolIdentity` connects as the computer account, which every other service on the server shares. |
| Load User Profile | Lets the App Pool's account use its own certificate store. | IIS default | On, if the SAML SP certificate is in the account's own store | Otherwise put the certificate in `LocalMachine\My`. |

After changing the identity: give the new account its SQL login (`-Step Db.AppPoolAccess`), read access to feed folders, Modify on the backup folder, and access to the SAML certificate's private key. Re-enter User-scope DPAPI credentials. Then check Windows sign-in still works; if you register the site's `HTTP/<hostname>` SPN on the gMSA, IIS's `useAppPoolCredentials` must also be on. `Admin_Installation.md` has the step-by-step gMSA setup.

### Fixed behaviour (not configurable)

These values are built in. They are listed so you know what to expect.

| Behaviour | Value |
|---|---|
| Cached rights, longest lifetime | 15 minutes, whatever the idle timeout |
| OIDC session cookie | 8 hours; HTTPS only |
| Account Progress lock renewal while a page is open | Every minute |
| CyberArk CP and CCP fallback to last fetched value | Up to 24 hours |
| Nightly Import and Load job | 02:00 (when installed) |
| Audit Log Purge job | 03:00 (when installed by hand) |
| Notification checks | Every 5 minutes (DevFakeAuthEnabledTooLong repeats at most every 7 days) |
| OIDC changes | Need an application restart |
