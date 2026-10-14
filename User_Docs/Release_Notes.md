# Release Notes

What changed in each BlueTrack release, newest first, for users and administrators. Each release lists what's new, what changed, what was fixed and what to do when upgrading. Details are in the guides named alongside each item.

## Unreleased

### New

- **CyberArk sources on the Data Sources page.** Admin > Data Sources now shows which CyberArk sources the nightly import uses, and **Edit CyberArk sources** changes them: the Privilege Cloud export folder and file names, and the Self-Hosted EVD database. **Test** checks them first. Until now they could only be changed by re-running the installer. See the Administrator Settings Guide, "Data Sources".
- **Accounts from other systems.** A new data feed import, **Account inventory**, adds the accounts another system has (a CSV of user names and addresses) to Account Progress at Discovered, with the system they came from. Accounts that disappear from the file are marked deleted. When CyberArk has the same account, the two are linked: the account moves to Onboarded to Vault and is counted once, as its CyberArk account. The new **Linked feed accounts** filter on **Accounts** shows the linked ones. Accounts that only nearly match (the same user name, with addresses that agree by host name or DNS) are suggested on the new **Reports > Possible Account Matches**, where Approvers and Admins link or dismiss them. See the Administrator Settings Guide, "Data Sources (feeds)".

- **Break-glass sign-in alerts.** On Identity Providers, list your emergency accounts (for example the server's local Administrator). Each time one signs in, BlueTrack records it in the Audit Log and emails the recipients of the new **BreakGlassLogon** notification. See the Administrator Settings Guide, "Identity Providers".
- **SAML Single Logout.** With the identity provider's logout address set (Identity Providers, SAML, **IdP Single Logout Destination**), **Sign out** of a SAML session also signs you out at the identity provider, and signing out at the identity provider ends your BlueTrack session too. The identity provider needs BlueTrack's logout address; see the Configuration Guide, "SAML".

- **Export reports.** Every report under **Reports** has **Export CSV** and **Export Excel** buttons. The file holds every row that matches the report's current filters and sort, not just the page you see, with the same columns as on the page. If more rows match than the new **Export Max Rows** setting allows (100,000 unless an administrator changes it), the export is refused with the number of rows, so narrow the filters first. Administrators set that limit with the new **Export Max Rows** setting on **Global Application Configuration** (1 to 1,000,000). Each export is recorded in the Audit Log as a `ReportExport` event.

- **Trends report.** **Reports > Trends** shows how accounts have moved over time: accounts at each Blueprint stage, % complete (the KPI Summary's rule: accounts at the final stage with status Complete, out of those in scope) and accounts by risk level. Choose the dates (the last 90 days unless you change them), by day or by week, and narrow it to one Application or Safe. Each chart has a **Show data table** button, and the report can be exported like the others. It draws on the nightly progress snapshot, so it starts filling from the first night after installing. The upgrade adds a database index (script 18).

- **More email notifications.** Three new notifications on **Admin > Notifications**: **NightlyImportFailed** (within about 5 minutes of the nightly Import and Load failing, or not finishing within 4 hours, naming the step, file and error), **OverdueRiskExceptions** (a daily list of Risk Exceptions past their review date) and **WeeklySummary** (KPIs, the change since last week, overdue counts and the last import). They start turned off: tick **Enabled** for each one you want, and set when the digests go out in the new **Schedule** section (server time; 07:00 daily and Monday 07:00 unless you change them). Every notification type can now be turned off the same way. The upgrade (script 19) also makes the Import and Load record each run in the database; no other step is needed.
- **Sign in to Microsoft 365 or Google Workspace for email.** On **Admin > Notifications**, **Auth Method** has three new choices for mailboxes where username and password sign-in is turned off: **Microsoft 365**, **Google Workspace** and **Generic OAuth2**. Each shows only the settings it needs (for example the tenant and client ID), and the secret (client secret, certificate and key, or the Google service account key) is kept as a credential on **Credentials & LDAP**, like the Basic password. **Send Test Email** works with each and says what went wrong (for example "Token endpoint returned invalid_client"), and is now recorded in the Audit Log as a `ConnectionTest` event. The provider needs setting up first; see the Administrator Settings Guide, "Notifications". The upgrade adds database script 20.

- **Links in emails.** New **Site URL** setting on Global Application Configuration: the address people open BlueTrack at (for example `https://bluetrack.company.com`; it must start with `https://`, or `http://` only for localhost). When it's set, the notification emails link straight to the page they mention (Overdue Reviews, Trends, the KPI Summary, CyberArk Sources, the Audit Log); when it's blank they name the page as before. The installer fills it in from the site's host name and HTTPS port when it's blank, and never changes one you've set.

- **Language setting.** **Profile & Preferences** has a new **Language** choice, saved for your account like the theme. English is the only language for now; others will be added once a native speaker has reviewed each. Until you choose, BlueTrack uses your browser's language when it has it, and English otherwise. Numbers and dates still follow your browser's format unless you pick a different language. No upgrade step.

- **Command-line checks** in the new `Tools` folder: the install's health, SAML/OIDC sign-in and sign-out, an LDAPS bind before adding a domain, and the Test buttons. See the Operations Guide, "Checking a Running Install", and `Tools\README.md`.

- **Find the target to link to, and detect CSV columns.** On **Target Match Review**, **Link To** searches targets as you type (by name or any identifier, at least 2 characters) and shows each one's type and identifiers; leave it empty to use the candidate. On **Import Mapping Profiles**, pick a **Sample CSV file** to fill in the source columns from its header row (read in your browser, not uploaded); a field you already set is only changed if you choose **Replace**. The Targets and Access Groups **Bulk Actions** uploads show which required columns a file is missing and can open a new profile pre-filled from it.

- **Linked targets.** When an imported target matches an existing one only by IP address, **Target Match Review** now shows what was imported (name, type, source, risk score and its other identifiers) and offers **Link**, **New Target** or **Ignore**. **Link** keeps it as its own target, linked to the existing one: the IP address stays where it is, and accounts reaching either count it once, at the existing target's risk score. **New Target** keeps it as a separate target. Later imports of the same row (same name and discovery source) go straight to that target, and an ignored row isn't asked about again; the Bulk Actions result says how many were skipped. Linked targets are hidden on **Targets** unless **Linked targets** is set to **Show**; a target's page lists the targets linked to it, or the one it's linked to, with **Unlink**. Merging, which moved the IP address to another target, is gone. The upgrade adds database script 23.

### Changed

- **Numbers:** counts on the pages (totals, pages, selections, import results, dashboard and report figures) use your browser language's number format, for example 1,234 or 1.234.
- **Navigation:** Reports and Admin are now at the end of the top navigation, with Admin last.
- **Reports** are listed in a column on the left, like the Admin pages, with the report you open beside the list.
- **About:** the menu with your name has a new **About** item that shows BlueTrack's version.
- **Version on About and the Deployment page:** both now show the same version. A release shows its number (for example `1.1.0`); a build made between releases shows the release it follows, how many changes came after it and which one (for example `1.0.0-dev.12+abc1234`, ending `.dirty` if built with uncommitted changes), so a test build can't be mistaken for a release. The Deployment page no longer adds a fourth number (`1.0.0.0`).
- **Application ↔ Safe Mapping** shows the applications and the safes a page at a time, like Accounts, at your page size.
- **Required Permission** for a field is now set on the **Edit Field** page (Admin > Field Metadata Management) and shown in the field list, instead of with SQL.
- **Reporting views:** the CSV export view, the Power BI accounts view and the daily progress snapshot count an account from another system that's linked to a CyberArk account once, as the app does.
- **Secrets Store (Azure Key Vault, AWS Secrets Manager, CyberArk Conjur):** a stored client secret, access key or API key is now encrypted so only the application's own account can read it, and a lookup gives up after about half a minute instead of several minutes when the service can't be reached. Conjur's access token is sent in the encoded form CyberArk's own client uses.
- **Installer transcripts** in `Deploy\Logs` older than 90 days are removed at the start of each installer run.

- **Test buttons are audit-logged.** Each Test on the Secrets Store Configuration and Credentials & LDAP pages is recorded in the Audit Log as a `ConnectionTest` event, with what was looked up and whether it worked. The Secrets Store test no longer shows the password's length.

### Fixed

- **Security updates.** Updated components that had published security advisories: Windows sign-in (Negotiate), the XML signature and certificate libraries used by SAML and email, and one web build library. See Upgrading for the matching .NET update on the server.
- **The nightly Privilege Cloud import works with the new export layout.** CyberArk changed the Users export (Email removed; Enabled, Suspended and Location added) and the Groups export (Directory added), and the import failed with "Cannot obtain the required interface ("IID_IColumnsInfo")". The import now reads each file's columns by their header names, so the old and the new layouts both load and columns it doesn't use are ignored. If a file lacks a column the import needs, the run fails with a message naming the file and the column, which the NightlyImportFailed email shows. **Test** on **Edit CyberArk sources** now opens each file and lists any columns the import needs that are missing, instead of only checking that the file exists. BlueTrack also keeps the users' Enabled, Suspended and Location and the groups' Directory, for later use.
- **Trends no longer has a gap after a failed nightly import.** The daily snapshot behind Reports > Trends is now taken every night, even when the Import or Load fails (it then records the unchanged data). A failed snapshot is reported by the NightlyImportFailed email, and the weekly summary lists the last snapshot run.
- **SAML sign-in with an unreadable signing key** now says "SAML sign-in is not available" instead of a server error, and the Identity Providers health check reports the problem. The Installation Guide says how to give the App Pool's account read access to the key.

### Upgrading

- **Install the .NET 10.0.13 Hosting Bundle on the server** (from Microsoft's .NET download page) before or with this upgrade. Some of the security fixes are in .NET itself, not in BlueTrack, so they only take effect once the server runs .NET 10.0.13 or later. Installing it restarts IIS.
- Run the installer as usual; it applies the new database scripts (`11_BlueTrack_ConnectionTestAudit.sql`, `12_BlueTrack_CyberArkSourceConfig.sql`, `13_BlueTrack_AccountInventoryFeed.sql`, `14_BlueTrack_FeedAccountLinking.sql`, `15_BlueTrack_ReportingViewsSkipLinkedFeedAccounts.sql`, `16_BlueTrack_BreakGlassAccounts.sql`, `17_BlueTrack_ReportExport.sql`).
- **If you use Azure Key Vault, AWS Secrets Manager or CyberArk Conjur** in the Secrets Store, enter the stored secret again and save, so it's re-encrypted the new way (the old form still works until then).
- **Break-glass alerts:** after upgrading, add your break-glass accounts on Identity Providers and give the BreakGlassLogon notification a target role (Notifications).
- **If you use the nightly import job**, also run the installer with `-Step Db.NightlyJob`. It saves your current CyberArk source settings for the Data Sources page and recreates the job to read them, as one step that also takes the daily Trends snapshot when the import fails (`21_BlueTrack_SnapshotAndSiteUrl.sql`). Until you do, the job keeps working with its old settings, changes made on the page have no effect, and a night whose import fails gets no snapshot.
- **Site URL:** a full installer run sets it. If you only apply the database scripts, set it on Global Application Configuration, or run the installer with `-Step Db.SiteUrl`.
- **Privilege Cloud import fix:** the installer, or the Migrator on its own, applies `22_BlueTrack_PcImportByHeader.sql`. The nightly job needs no change; the next run uses the new import.

## 1.0.0

The first signed release. BlueTrack tracks your progress onboarding privileged accounts into CyberArk, following the CyberArk PAM Blueprint: it loads your CyberArk data each night, and your team works each account through its stages, records risk exceptions, and reports on progress. New users should start with the Getting Started Guide; administrators with the Installation Guide and the Administrator Settings Guide.

### What's in this release

- **Account Progress**: every account and its stage (Discovered, Assessed / Prioritized, Onboarded to Vault, Managed / Rotation Enabled, Verified / Compliant), status, owner and dates, with edit locks, bulk edit, and delete and undelete with a reason.
- **Risk exceptions**: request, approve, extend and revoke exceptions, with segregation of duties and review dates; bulk import of exceptions approved in another tool.
- **Risk scoring**: Targets and Access Groups with risk scores, an account risk score from what each account can reach, risk bands, and the Risk Score report.
- **Reports and dashboard**: stage and status summaries, KPIs, overdue and at-risk accounts, safes and accounts flagged for deletion, discovered accounts.
- **Data**: the nightly import and load from Privilege Cloud and/or Self-Hosted CyberArk exports, scheduled data feeds (Admin > Data Sources) and CSV bulk imports.
- **Sign-in**: Windows Integrated, OIDC and SAML, with group-to-role mapping for each, a default sign-in method, and direct sign-in addresses (`/login/windows`, `/login/oidc`, `/login/saml`) for an identity provider's launch URL.
- **Administration**: roles and permissions, identity providers, secrets store (CyberArk CP/CCP, Windows DPAPI and others), notifications, scheduled backups and a Deployment page with health checks.
- **Installation and updates**: a resumable installer, and `Update-BlueTrack.ps1`, which installs only signed release tags.

### New since the pre-release builds

- **A user menu** at the right of the top navigation shows your name, with **Profile & Preferences** (formerly My Profile) and **Sign out**.
- **Signing out after inactivity**: BlueTrack signs you out after the Idle Timeout (30 minutes by default) without activity, in every tab. Idle sign-in sessions also end on the server.
- **Sign-in page and default method**: administrators choose the default sign-in method on the Identity Providers page; with none, the sign-in page lists the enabled methods.
- **OIDC and SAML groups** can be mapped to roles on the Group → Role Mapping page.
- **Mapping profiles on Bulk Actions**: the Targets and Access Groups CSV imports let you choose an Import Mapping Profile.
- **Analysts can see the Risk Score report.**
- **Backups**: scheduled backups (a daily time and days to keep), and the backup folder is kept private: BlueTrack removes broad groups' access before each backup, or refuses the backup and says why. A Backup Folder health check shows the folder's state.
- **Secrets store**: backend URLs must be `https://`; when CyberArk is briefly unreachable, the last value fetched in the past 24 hours is used.

### Changed

- **Account Progress lock timeout** is 30 minutes on a new install (it was 5); it must be 1 to 720 minutes. Existing installs keep their value.
- **Idle Timeout** must be 5 to 720 minutes, and now signs people out (before, it didn't).
- **No mapping profile chosen** now means the file uses the template's own column names. Before, the oldest active profile for that import was used without asking.
- **Seeing tracked data needs a role with ViewDashboard.**
- **Updates**: `Update-BlueTrack.ps1` installs only signed release tags; testing a branch needs `-AllowUnsignedBranch`.
- **The site certificate needs a Subject Alternative Name** for its hostname; Chrome and Edge reject one without. `-Step Iis.Site -CertificateThumbprint <new>` replaces the certificate of an existing site.

### Fixed

- SAML sign-in ended in an error after signing in at the identity provider.
- A direct SSO address showed a Windows sign-in prompt first, in browsers that don't sign in to the site automatically.
- Signing out didn't stick when Windows isn't the default method.
- OIDC users with no full name were shown with an empty name; their username is shown instead.
- Windows sign-in looped on some servers.
- Re-saving a secrets backend without retyping its credential removed the stored credential.

### Upgrading from a pre-release build

1. Run `Update-BlueTrack.ps1` (elevated) and the installer it offers; the database changes apply themselves.
2. Re-run the installer's `Db.AppPoolAccess` step once, so the app's database account gets its new grants (append-only audit log, backup operator).
3. If a data feed or the backup folder is on a network share, list it in the installer's `-AllowedFileRoots`.
4. Check the **Default sign-in method** on Identity Providers. The upgrade sets it to the method you were already defaulting to.
5. If your site certificate has no Subject Alternative Name, replace it (see the Installation Guide, "Certificates").
6. Reload any BlueTrack page left open from before the update.
