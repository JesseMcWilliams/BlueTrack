# Release Notes

What changed in each BlueTrack release, newest first, for users and administrators. Each release lists what's new, what changed, what was fixed and what to do when upgrading. Details are in the guides named alongside each item.

## Unreleased

### New

- **CyberArk sources on the Data Sources page.** Admin > Data Sources now shows which CyberArk sources the nightly import uses, and **Edit CyberArk sources** changes them: the Privilege Cloud export folder and file names, and the Self-Hosted EVD database. **Test** checks them first. Until now they could only be changed by re-running the installer. See the Administrator Settings Guide, "Data Sources".
- **Accounts from other systems.** A new data feed import, **Account inventory**, adds the accounts another system has (a CSV of user names and addresses) to Account Progress at Discovered, with the system they came from. Accounts that disappear from the file are marked deleted. When CyberArk has the same account, the two are linked: the account moves to Onboarded to Vault and is counted once, as its CyberArk account. The new **Linked feed accounts** filter on **Accounts** shows the linked ones. Accounts that only nearly match (the same user name, with addresses that agree by host name or DNS) are suggested on the new **Reports > Possible Account Matches**, where Approvers and Admins link or dismiss them. See the Administrator Settings Guide, "Data Sources (feeds)".

- **Break-glass sign-in alerts.** On Identity Providers, list your emergency accounts (for example the server's local Administrator). Each time one signs in, BlueTrack records it in the Audit Log and emails the recipients of the new **BreakGlassLogon** notification. See the Administrator Settings Guide, "Identity Providers".
- **SAML Single Logout.** With the identity provider's logout address set (Identity Providers, SAML, **IdP Single Logout Destination**), **Sign out** of a SAML session also signs you out at the identity provider, and signing out at the identity provider ends your BlueTrack session too. The identity provider needs BlueTrack's logout address; see the Configuration Guide, "SAML".

- **Export reports.** Every report under **Reports** has **Export CSV** and **Export Excel** buttons. The file holds every row that matches the report's current filters and sort, not just the page you see, with the same columns as on the page. If more rows match than the new **Export Max Rows** setting allows (100,000 unless an administrator changes it), the export is refused with the number of rows, so narrow the filters first. Administrators set that limit with the new **Export Max Rows** setting on **Global Application Configuration** (1 to 1,000,000). Each export is recorded in the Audit Log as a `ReportExport` event.

- **Command-line checks** in the new `Tools` folder: the install's health, SAML/OIDC sign-in and sign-out, an LDAPS bind before adding a domain, and the Test buttons. See the Operations Guide, "Checking a Running Install", and `Tools\README.md`.

### Changed

- **Numbers:** counts on the pages (totals, pages, selections, import results, dashboard and report figures) use your browser language's number format, for example 1,234 or 1.234.
- **Navigation:** Reports and Admin are now at the end of the top navigation, with Admin last.
- **Reports** are listed in a column on the left, like the Admin pages, with the report you open beside the list.
- **About:** the menu with your name has a new **About** item that shows BlueTrack's version.
- **Application ↔ Safe Mapping** shows the applications and the safes a page at a time, like Accounts, at your page size.
- **Required Permission** for a field is now set on the **Edit Field** page (Admin > Field Metadata Management) and shown in the field list, instead of with SQL.
- **Reporting views:** the CSV export view, the Power BI accounts view and the daily progress snapshot count an account from another system that's linked to a CyberArk account once, as the app does.
- **Secrets Store (Azure Key Vault, AWS Secrets Manager, CyberArk Conjur):** a stored client secret, access key or API key is now encrypted so only the application's own account can read it, and a lookup gives up after about half a minute instead of several minutes when the service can't be reached. Conjur's access token is sent in the encoded form CyberArk's own client uses.
- **Installer transcripts** in `Deploy\Logs` older than 90 days are removed at the start of each installer run.

- **Test buttons are audit-logged.** Each Test on the Secrets Store Configuration and Credentials & LDAP pages is recorded in the Audit Log as a `ConnectionTest` event, with what was looked up and whether it worked. The Secrets Store test no longer shows the password's length.

### Fixed

- **SAML sign-in with an unreadable signing key** now says "SAML sign-in is not available" instead of a server error, and the Identity Providers health check reports the problem. The Installation Guide says how to give the App Pool's account read access to the key.

### Upgrading

- Run the installer as usual; it applies the new database scripts (`11_BlueTrack_ConnectionTestAudit.sql`, `12_BlueTrack_CyberArkSourceConfig.sql`, `13_BlueTrack_AccountInventoryFeed.sql`, `14_BlueTrack_FeedAccountLinking.sql`, `15_BlueTrack_ReportingViewsSkipLinkedFeedAccounts.sql`, `16_BlueTrack_BreakGlassAccounts.sql`, `17_BlueTrack_ReportExport.sql`).
- **If you use Azure Key Vault, AWS Secrets Manager or CyberArk Conjur** in the Secrets Store, enter the stored secret again and save, so it's re-encrypted the new way (the old form still works until then).
- **Break-glass alerts:** after upgrading, add your break-glass accounts on Identity Providers and give the BreakGlassLogon notification a target role (Notifications).
- **If you use the nightly import job**, also run the installer with `-Step Db.NightlyJob`. It saves your current CyberArk source settings for the Data Sources page and recreates the job to read them. Until you do, the job keeps working with its old settings, and changes made on the page have no effect.

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
