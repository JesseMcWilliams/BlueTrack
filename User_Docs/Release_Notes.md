# Release Notes

What changed in each BlueTrack release, newest first, for users and administrators. Each release lists what's new, what changed, what was fixed and what to do when upgrading. Details are in the guides named alongside each item.

## Unreleased

### New

### Changed

- **Moving from the EVD to Self-Hosted Direct** has its own section in the Deployment Runbook: what to check before switching, the switch and first pull, what the first Direct night should look like, and how to switch back.

### Fixed

### Upgrading

## 1.3.3 (2026-10-21)

### New

- **Cancel pull** on Admin > Data Sources stops a Self-Hosted Direct pull that is running (nightly, shadow or Pull now). It's logged as failed, saying it was cancelled; the data the nightly load reads is left as it was.
- **Test an LDAP domain's bind** with the new **Test** button in the domain form on Admin > Credentials & LDAP. It binds with the values in the form, saved or not, and shows the name it bound as and the domain controller, or the directory's error.

### Changed

- **Self-Hosted Direct sites get more account and entitlement detail.** With Self-Hosted Direct as the source, each account now has its CPM status, whether it is managed automatically (and why not), its secret type, whether access is restricted to remote machines, its last change, reconcile and verify dates from PVWA, and new fields for when it was created and its last CPM action and result, shown on the account's page and in the Power BI accounts view. Safe members include CyberArk's built-in ones (such as Administrator), and the View Audit Log, Use Accounts, View Safe Members and Requests Authorization permissions are now recorded, so entitlement reports show more grants than under the EVD. A Safe member PVWA has no user or group for is listed on the Unresolved Entitlement Members report (with its source) instead of stopping the nightly load.
- **CPM status is stored in lower case** (for example "success") for Privilege Cloud and Self-Hosted Direct alike.
- **Accounts in CyberArk's internal Safes** show "Deleted in CyberArk (internal Safe)" after a site moves to Self-Hosted Direct, since PVWA never returns those Safes.
- **The first Self-Hosted Direct pull is checked against the EVD.** With no earlier Direct pull, its Safe and account counts are compared with what the last EVD import staged (internal Safes, deleted and dependent accounts left out); a change over the allowed percentage is rejected for an admin to accept.

### Upgrading

- The upgrade adds database scripts 45 and 46 (the Load reading the PVWA data, locations keyed by their path, and the new columns in the Power BI accounts view); the installer applies them. Nothing changes for a site on the EVD. A site on Self-Hosted Direct sees the new detail after its next nightly load, including dependent accounts and accounts in internal Safes shown as deleted in CyberArk.

## 1.3.2 (2026-10-21)

### New

- **Shadow comparison, for moving Self-Hosted from the EVD to Self-Hosted Direct.** With the EVD as the source, tick **Shadow pull** on Edit CyberArk sources and give the PVWA address and credential: BlueTrack then also reads PVWA each night (at the **Shadow pull time**, after the import) and compares it with what the EVD import staged. The EVD stays the source and reports don't change. Data Sources shows the shadow pulls, how many clean nights in a row there have been (7 are needed before switching), and every difference. CyberArk's internal Safes and the PVWA gateway members are marked automatically, and directory mappings and dependent accounts are left out. Mark any other difference you expect for one item, a whole Safe or every membership of a user or group; marks are audit-logged. A list shows the Safes PVWA can't fully see.
- **Pull time limit for Self-Hosted Direct.** A PVWA pull (nightly, shadow or **Pull now**) still running after the **Pull time limit** on Edit CyberArk sources (45 minutes unless changed) is stopped and logged as failed, and the failure email is sent. The data the nightly load reads is left as it was.

### Changed

- **LDAP binds with a bind account now use the account's UPN where they can.** A CyberArk CP or CCP credential's LogonDomain (or, failing that, its Address, or the LDAP domain's Search Base) is joined to the user name, so an account whose name is longer than the 20-character Windows logon name now binds. A user name that already includes a domain is used as it is. If your bind account's LogonDomain isn't a DNS domain name (for example `COMPANY` rather than `company.com`), the Address or Search Base is used instead.

### Upgrading

- The upgrade adds database scripts 42, 43 and 44 (the shadow pull, the comparison tables and its automatic rules, and the pull time limit); the installer applies them. The pull time limit starts at 45 minutes; if your Self-Hosted Direct pulls take longer, raise it on Edit CyberArk sources. Nothing changes until you turn on the shadow pull. To start the comparison on a site that uses the EVD: put the PVWA user in the **Auditors** and **Vault Admins** groups, then on Edit CyberArk sources tick **Shadow pull**, enter the PVWA address and credential, set the shadow pull time after the nightly import has finished, **Test** and **Save**. Details: `Admin_DeploymentRunbook.md`, "After upgrading".

## 1.3.1 (2026-10-20)

### Changed

- Self-Hosted Direct also keeps everything PVWA returns in new staging tables: each account's CPM status and last reconcile time, the latest CPM verify, change and reconcile of every account the CPM has worked on, platform policy, every account property, predefined Safe members and user details. Nothing uses them yet, so reports don't change. A pull makes one more call per account the CPM has worked on, and one for target platform settings.
- Saving the CyberArk sources keeps the settings of the Self-Hosted source not in use: choosing Direct no longer clears the EVD database name, and choosing the EVD keeps the PVWA address and credential, so switching back needs nothing typed again.

### Upgrading

- The upgrade adds database script 41 (new Self-Hosted Direct staging tables); the installer's database migration applies it, and the app's existing database permissions cover the new tables. No setting changes, and nothing to re-run.
- **If you use Self-Hosted Direct**, open Admin > Data Sources after the first pull following the upgrade (or click **Pull now**) and open that pull's **Details**. It should succeed with the same Safe and account counts as before, and more Safe members (predefined members such as Administrator are now read too; the change guard doesn't check members). Warnings that the CPM activity or target platform settings weren't read mean the PVWA user lacks the role for them: membership of the Vault's **Auditors** group for Safes and accounts, and of **Vault Admins** for platform details. The pull still succeeds and reports are unaffected, but tell whoever plans the move off the EVD. Details: `Admin_DeploymentRunbook.md`, "After upgrading".
- **If you use the EVD**, nothing changes. Saving the CyberArk sources page no longer clears the PVWA address and credential you entered before; that's intended.

## 1.3.0 (2026-10-19)

### New

- **Filters, sort and page are kept in the page address** on the Accounts list, and on the Trends, Discovered Accounts and Risk Score reports. Opening an account and coming back (Save, Cancel, or the Accounts breadcrumb) returns you to the list as you left it, and a copied address opens the same view.
- **The stage counts on the Dashboard, and the counts on Reports > Stage / Status Summary, open the accounts behind them.** (They show the Accounts list's default view, which hides deleted accounts, as the counts do. The KPI counts don't open a list yet.)
- **Saving now says so.** After you save and return to a list, a line such as "Payroll saved." shows under the page title until you move on or dismiss it. When a save is refused, the Application, Role, Identity Provider, Import Mapping Profile, Notification recipient and Field definition pages now show the reason instead of only a status number.
- **Empty lists explain themselves.** Targets, Access Groups, Accounts, Discovered Accounts, Risk Score Bands, Identity Providers, Field Metadata, Roles and Group-Role Mapping say "none yet" (or, with filters on, "none match the filters") instead of showing only column headings.
- **No roles? You land on a page that explains.** If you sign in and your account has no roles, the menu shows only your user menu and every page takes you to a No Access page: who you're signed in as, what to ask an administrator for, **Check again** (so you don't have to sign out once a role is added) and Sign out. A page your roles don't cover, typed or bookmarked, opens Access Denied, which links to the Dashboard and My Profile; the Admin menu opens the first section you can use. An address that matches no page shows Page Not Found.
- **Three more actions ask first:** Apply on the Accounts bulk edit (it lists the fields it will change), Dismiss on Possible Account Matches and Discovered Accounts, and Rotate on an API key.
- **New Risk Exception: find the account by typing.** No more looking up an account number: type part of the user name, account name or address and choose from the list.
- **Smaller list fixes.** Accounts has a **Clear filters** button; the Approval Worklist's Exception ID is a link; the Risk Exceptions list links to the Overdue Reviews and Approval Worklist pages; Identity Providers and Field Metadata show Yes/No instead of true/false.
- **Targets and Access Groups keep their filters.** Filters, sort and page are in the page address, so opening a target or group and saving or cancelling brings you back to the list as you left it. A failed load no longer leaves old rows on screen. Each row's Delete button names the row for screen readers. **Link a Single Account to a Target** now finds the account and the target by typing, and shows why a link was refused.
- **Risk Exceptions and the Audit Log keep their filters** in the page address too, so a view can be shared and reloaded. An exception's detail page now names the account (as a link to it) or the application, instead of showing a number.
- **A lost edit lock no longer surprises you at Save.** If someone else takes the account you are editing, or your lock expires, the page says so at once, keeps what you typed, and lets you take the lock again.
- **A warning before the idle sign-out.** For the last two minutes before you are signed out for inactivity, a notice shows how long is left and has a **Stay signed in** button.
- **KPI Summary counts open the accounts.** Each count on Reports > KPI Summary is a link to the Accounts list showing exactly those accounts; the list says which KPI it is showing and has a button to show all accounts again.
- **Reports explain themselves and act more smoothly.** Reconciliation Review says it is read-only and points to Possible Account Matches; the two decommission reports say BlueTrack only reports and you delete in CyberArk; Unresolved Members brings the Resolve form into view and confirms a save; Discovered Accounts links the account it may already match and announces its expand toggle; Account Classification links to Discovered Accounts.
- **More small fixes.** KPI Summary explains each count and shows when it was read; Risk Score has an empty message and links to the account; Application email fields are email inputs; **Backup App** asks first and names the downloaded file; the run history's time link announces whether it is open.
- **Notifications and security setup.** Acting in one Notifications section no longer discards what you typed in another, and the test email waits until the SMTP settings are saved. The Identity Provider page says OpenID Connect changes need an application restart (SAML ones don't) and warns before you delete the only enabled provider or the default sign-in method. API key Copy tells you when it can't copy; SIEM messages appear beside its buttons; the Secrets Store and Role forms are better labelled for screen readers.
- **Audit log, fields, accounts and your profile.** The Audit Log offers the event types as a choice, shows times in your local time and has Clear filters. Field Type and Reference Table are choices. Account Detail explains view-only access and that the risk override saves on its own. My Profile keeps the page when you reload your rights, says whether each preference was saved, and lets you set the page size.
- **Application mapping.** Applications show what uses them and can be deleted when nothing does; the Safes table can be searched and narrowed to Safes with no application; the page explains the four ways an account gets its application; bulk imports list row errors one per line and say when the server didn't answer; Platform Mapping explains a disabled Save and ignores a second click on Remove.
- **Decommission and overdue reports.** Accounts Flagged for Deletion can be searched, sorted and paged, and opens one Safe from the Safes report; the Safes report links its application to the application page; the Overdue / At-Risk Worklist shows days overdue and can be searched, sorted and paged; Trends no longer asks for an invalid date range. **Upgrade step:** none beyond the normal deployment (the Migrator applies one new script, 40).
- **Failures stand out, and the key page protects you.** Failed feed runs, failed backups and unhealthy checks are marked; leaving an API key's page before copying it asks first. An application-scoped risk exception lists the accounts it covers. The Audit Log can be filtered by who did it. After an idle sign-out, signing in again returns you to the page you were on (text you hadn't saved is not kept).
- **Undo a dismiss, and check an import first.** Possible Account Matches and Discovered Accounts can show what was dismissed and Restore it. The Application mapping imports have a **Check file** button that shows what the file would do without changing anything. Risk exception lists and worklists link the account; a revoked exception says who revoked it, when and why; an import's pending matches link to Target Match Review; the Accounts bulk edit says which empty fields will be cleared.

- **Self-Hosted Direct.** BlueTrack can read your Self-Hosted CyberArk Vault straight from PVWA's REST API instead of the EVD database (PAM Self-Hosted 14.2 or later). On Admin > Data Sources > Edit CyberArk sources, choose the Self-Hosted source, then give the PVWA address, a credential from Admin > Credentials (a CyberArk CP or CCP credential is fetched from the Vault at each pull), the logon method (CyberArk or LDAP), the pull time and how much the Safe or account count may change. It reads Safes, accounts, Safe members, users, groups and platforms (platform names and descriptions now come from PVWA). Pending (discovered) accounts are not read.
- **A pull that looks wrong is stopped.** PVWA only returns what the service account can see. If the number of Safes or accounts changes by more than the allowed percentage from the last good pull, the pull is rejected, the nightly import fails and you get the usual failed-import email. Data Sources lists recent pulls, with **Pull now** and, after a rejection, **Accept the change and pull**.

### Changed

- These now ask before they act: **Revoke** on a risk exception, **Make active** on Secrets Store, **Ignore** on Target Match Review (it is remembered), and **Force Release Lock** on an account being edited by someone else.
- Target Match Review says what it decided (linked, new target, ignored), and its buttons name the row for screen readers. An account's "being edited since" time is now a local date and time.

- Self-Hosted Direct: a pull that fails or is rejected is now emailed when it happens (turn on the new **SelfHostedPullFailed** type on Admin > Notifications and add recipients; it starts off), shown as a banner on Data Sources, and marked with a warning on the Admin menu. A rejected pull lists the Safes it no longer sees, and the log shows how the Safe and account counts changed.
- Self-Hosted Direct: the CyberArk sources page warns when the chosen CyberArk CP or CCP credential's backend has no settings yet (for CP, the AppID on Admin > Secrets Store), with a link to fix it. Admin > Credentials shows what uses each credential and won't delete one that is in use, shows the reason when a save fails, and offers **Cancel and return to CyberArk sources** when you came from there.

- A CyberArk CP or CCP credential with the Folder left blank now uses Root, instead of being refused by CP.
- Self-Hosted Direct: setting it up is quicker. **Create a credential** on the CyberArk sources page opens Admin > Credentials and brings you back with the new credential chosen, and each credential has **Use for Self-Hosted Direct**.
- Self-Hosted Direct: each pull in the Data Sources log has a **Details** button with the whole message, the counts, and what to do.

### Fixed

- **Discovered Accounts**: the columns now line up under their headings (Risk Score, Risk Band, First Discovered, Last Seen and Matched by were shifted).
- **Bulk imports** on Targets and Access Groups now show why an import failed, instead of a summary of blanks.
- **Access Group > Found On Target** is now a search by name or identifier. It used to list only the first 50 targets.
- **Risk Exceptions**: a failed create, extend or revoke keeps what you typed and shows the reason; a successful extend or revoke says so.
- **Sign-in**: after a Windows sign-in that didn't complete, the other sign-in methods and Continue stay on the page. "No identity providers are enabled" no longer flashes while the page loads.
- **Data feeds**: pressing Enter in the New source system boxes adds the source system instead of saving the feed.
- **API keys**: an expired key can be edited and saved; the page says it has expired.
- **Application Safe Mapping** and **Notifications**: after a refused change, the dropdown goes back to what is saved, and Safe Mapping says what was assigned.
- **Audit Log**: an error loading an event's field-level changes is shown as an error, not as "no changes".

- Self-Hosted Direct no longer fails with "PVWA reported 44 users but returned 43". PVWA's detailed user list leaves out the built-in Master user; BlueTrack now reads the plain list, and a remaining difference is a warning.

### Upgrading

- Nothing changes until you choose Self-Hosted Direct; the EVD stays the default. Script 38 (applied by the Migrator) adds the settings and a pull log. To use Direct, the credential's PVWA user needs List Accounts and View Safe Members in each Safe to read, and the Audit Users Vault authorization; set the pull time earlier than the nightly import job's time.

## 1.2.0 (2026-10-17)

### New

- **Platform Mapping page.** **Admin > Platform Mapping** lists every CyberArk platform (for Self-Hosted, one per policy, added by the nightly load), the ones with no mapping first, and lets you give each an account type and source of record that its new accounts start with. Saving can also fill the accounts already tracked on the platform that have none (an account that has one is never changed). Until now this was done in SQL.

- **Go back to your browser's language.** **Profile & Preferences > Language** has **Use browser default**: it removes the language you saved, and BlueTrack follows your browser's language again (and its number and date format).

- **Link two targets by hand.** A target's page has **Link to another target**: search for the target it is the same as, choose it and click **Link**. They then count once, at the primary's risk score, like a link made in Target Match Review, and **Unlink** undoes it. A target that is linked already, or that has targets linked to it, can't be linked (unlink first), and a pair you unlinked can be linked again this way.

### Changed

- **Self-Hosted accounts now have a platform, a logon domain and a manual-management reason.** Until now every Self-Hosted account showed no platform, because the export has no platform list. The nightly load now adds each policy (PolicyID) it finds as a Self-Hosted platform and gives the accounts that platform; it also fills the platform's logon domain, and, for an account whose automatic management is switched off (the export's CPMDisabled reason), shows it as not auto-managed with that reason. The new platforms start unmapped. There is no page for mapping platforms yet: it is done in SQL, in the `platform_account_type_map` table, and the `vw_review_platform_sor_accounttype` view lists the platforms still unmapped; new accounts then get the account type and source of record you give each platform.

### Fixed

- **A list could show the wrong rows after a quick change of filter.** On Accounts, Risk Exceptions, Targets, Access Groups, the Audit Log and Discovered Accounts, typing a search and then changing a filter straight away could, when the search's answer came back last, leave the list showing the search's rows instead of the filter's. Only the newest request's answer is shown now.

- **A target can be deleted after Target Match Review used it.** Deleting a target that Target Match Review had matched imports to failed with a conflict. Its pending reviews are now removed (the next import queues them again if another target matches), and its resolved reviews stay as history without the deleted target's name.

### Upgrading

- Run database script 37 (the installer's database step does).

- Run database script 36 (the installer's database step does). The Self-Hosted platforms and properties appear after the next nightly load (or a manual Import and Load).

## 1.1.0 (2026-10-17)

### New

- **API keys for scripts.** A script can exchange an API key for a short-lived token (`POST /api/auth/token`) and call the API with it, acting as one role of your choice (any role except Admin). **Admin > API Keys** creates, lists, disables and deletes keys; a key is shown once, when it is created. Keys last 90 days by default (a new setting on **Global Application Configuration**, 1 to 365 days, also set for each key when it is created), can be disabled at once, and every use is in the Audit Log. Signing in with a token from your identity provider follows in a later release.

- **API keys: rotation, editing, expiry emails and a rate limit.** **Rotate** on **Admin > API Keys** makes a new key with the same role, so a script can be switched over before the old key is disabled. Click a key's name to change its role, expiry, owner email or limit without making a new key. Give a key an **owner email** and the owner is emailed before it expires (**API key expiry reminder lead time** on **Global Application Configuration**, 7 days by default) and on the day; the administrators are emailed on the day. Both are new notification types, off until you enable **ApiKeyExpiring** and **ApiKeyExpired** on **Admin > Notifications**. Each key is limited to 600 requests a minute (**API requests per minute, per key** on **Global Application Configuration**), or its own limit; over it the API answers 429. Expired keys are deleted automatically a year after they expire (**Delete expired API keys after (days)** on **Global Application Configuration**; 0 keeps them for ever).

- **Scripts can sign in with a token from your identity provider.** A script can send a token from an OIDC provider set up under **Identity Providers** (for example a client-credentials token from authentik or Entra ID) instead of an API key, and gets the same short-lived API token. **Admin > API Keys > API client mappings** says which role it gets: by the token's client, or by a group in the token. The Admin role can't be given. Nothing is accepted from a provider you haven't mapped.

- **Schedule the account-to-application assignments.** **Admin > Data Sources** has a new import, **Account → application assignments**: the same file as the Account → Application Assignments import on Application ↔ Safe Mapping > Bulk Actions (`UserName`, `Address`, `Application`, optional `Source`), run on a feed's schedule.

### Changed

- **The installer checks the Hosting Bundle's version.** A server whose ASP.NET Core runtime is older than 10.0.13 now counts as missing the Hosting Bundle, and the installer offers to install the current one (`-Step Prereq.HostingBundle`), instead of reporting it as done.

### Upgrading

- Run database scripts 30 to 35 (the installer's database step does). To get the API key expiry emails, enable **ApiKeyExpiring** (to key owners) and **ApiKeyExpired** (to administrators) on **Admin > Notifications**.

## 1.0.7 (2026-10-15)

### New

- **Assign many accounts to an application at once.** **Bulk edit** on Accounts can set (or clear) the **Application** of every selected account. **Admin > Application ↔ Safe Mapping > Bulk Actions** has a third import, **Account → Application Assignments**: a CSV of UserName, Address, Application (its code or name) and an optional Source. It matches accounts by username and address, ignoring case, and replaces an account's own application; accounts not in the file are left alone. No upgrade step.

- **Assign an account to an application.** The Account Progress edit form has a new **Application** field. An account is covered by an application's risk exception through its own application and through its safe's, so accounts with no safe, such as pending accounts, can now be covered. An application has many accounts; an account has at most one application of its own, in addition to its safe's. When the two differ, both apply, and Reports > Trends' Application filter counts the account under either. Bulk edit leaves the field alone for now. The upgrade adds database script 29.

- **Skip group rows on an account inventory feed.** A pending-accounts export lists local and domain groups beside the accounts, written as `*<name> Group*` in the UserName column. A new **Skip group rows** setting on the feed (off until you turn it on) skips them: they aren't imported, aren't errors, and the run's summary says how many were skipped. A group imported by an earlier run is marked deleted by the next run. The upgrade adds database script 28.

### Fixed

- **Account inventory feeds accept long user names and addresses.** A pending-accounts export, whose user names are SIDs, had rows rejected with "UserName and Address together are longer than 99 characters". An account's user name and address together can now be up to 400 characters. The upgrade (script 27) widens the database column; no other step is needed.

## 1.0.6 (2026-10-15)

### Changed

- **Update-BlueTrack.ps1 no longer changes the download folder's permissions.** It used to take ownership of the whole download folder and limit it to Administrators and SYSTEM on every run, which also reset the permissions of anything else kept in it, such as the backup folder. Now it only warns when someone other than Administrators, SYSTEM or TrustedInstaller can change the folder (they could run code as an administrator through the installer), and leaves it alone. The new **-LockDownFolder** switch limits the folder as before, but skips a folder that is already restricted.

### Upgrading

- **If you used Update-BlueTrack.ps1 before**, its earlier runs already limited your download folder; nothing changes there. If the folder and its contents should be locked down and the script warns about it, run it once with `-LockDownFolder`, using a folder that holds only downloaded versions.

## 1.0.5 (2026-10-14)

### New

- **Forward the audit log to your SIEM.** New page **Admin > SIEM Forwarding** (permission `ManageApplicationConfiguration`): every new audit event is sent to your SIEM's syslog collector within a few seconds, as CEF (Common Event Format) in RFC 5424 syslog, over TLS (with the Windows trust store, a pinned certificate fingerprint or your CA) or TCP. Choose which event types are sent and each one's severity; **Send Test Event** checks the connection. Events wait in the database while the collector is down and are sent when it's back, and the audit purge keeps them until then. Only events written after it's turned on are sent. A new **SIEM Forwarding** health check on the Deployment page warns when sending fails or falls more than 15 minutes behind. **Upgrading:** run the Migrator (adds database script 26), set up the page, and open the firewall from the BlueTrack server to the collector's port. See the Administrator Settings Guide, "SIEM Forwarding".
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

- **Export the Audit Log.** **Admin > Audit Log Viewer** has **Export CSV** and **Export Excel**, like the reports: every event matching the filters you applied, with the time of each event in UTC. Anyone who can view the Audit Log can export it, up to the **Export Max Rows** limit. See the User Guide, "Audit Log Viewer".

- **Resolve unresolved entitlement members.** **Reports > Unresolved Entitlement Members** now shows each member's name. The nightly load links a member to the user or group with exactly the same name (ignoring case) when there is only one, so most of them leave the report. Approvers and Admins (`ConfirmReconciliation`) can **Resolve** the rest: link the member to a user or group, or mark it as an external or cloud role with a note. A resolution applies to the member on every safe, is kept through the nightly load, and can be undone with **Undo**. Tick **Show resolved** to see resolved members and how each was resolved. Resolve and Undo are recorded in the Audit Log as `EntitlementMemberResolution` events. See the User Guide, "Reports".

- **Account classes on Discovered Accounts.** The report shows each account's **Account Class** (Primary, Secondary, Admin or Unknown), its **Owner** (the person's primary account) and what decided it, with a **Class** filter (also on the export) and a per-row **Classify** override. The rules (AD attributes such as adminCount, a name prefix or suffix, an OU, and a shared employee ID) are set on the new **Admin > Account Classification** page (permission `ManageDiscoveredAccounts`); it is off with no rules until you set it up.

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
- **The installer's settings now take effect on every install.** The installer saved the database connection, HSTS and allowed folders in a file named after the environment (for example `appsettings.Test.json`), but under IIS BlueTrack always runs as Production and read that file only on a Production install. Elsewhere it used its built-in database connection, didn't send HSTS and allowed any folder. The installer now writes `appsettings.Site.json`, which BlueTrack always reads, and **Admin > Deployment** shows the environment you chose in the installer instead of "Production". Development-only sign-in stays off.

### Upgrading

- **Install the .NET 10.0.13 Hosting Bundle on the server** (from Microsoft's .NET download page) before or with this upgrade. Some of the security fixes are in .NET itself, not in BlueTrack, so they only take effect once the server runs .NET 10.0.13 or later. Installing it restarts IIS. **Install it by hand:** the installer only checks that a Hosting Bundle is present, not its version, so neither a full run nor `-Step Prereq.HostingBundle` updates an older one.
- Run the installer as usual; it applies the new database scripts 11 to 26 (`11_BlueTrack_ConnectionTestAudit.sql` to `26_BlueTrack_SiemForwarding.sql`). The items below say which of them need a step of their own.
- **If you use Azure Key Vault, AWS Secrets Manager or CyberArk Conjur** in the Secrets Store, enter the stored secret again and save, so it's re-encrypted the new way (the old form still works until then).
- **Break-glass alerts:** after upgrading, add your break-glass accounts on Identity Providers and give the BreakGlassLogon notification a target role (Notifications).
- **If you use the nightly import job**, also run the installer with `-Step Db.NightlyJob`. It saves your current CyberArk source settings for the Data Sources page and recreates the job to read them, as one step that also takes the daily Trends snapshot when the import fails (`21_BlueTrack_SnapshotAndSiteUrl.sql`). Until you do, the job keeps working with its old settings, changes made on the page have no effect, and a night whose import fails gets no snapshot.
- **Site URL:** a full installer run sets it. If you only apply the database scripts, set it on Global Application Configuration, or run the installer with `-Step Db.SiteUrl`.
- **Privilege Cloud import fix:** the installer, or the Migrator on its own, applies `22_BlueTrack_PcImportByHeader.sql`. The nightly job needs no change; the next run uses the new import.
- **Settings file:** run the installer as usual, or on its own with `.\Install-BlueTrack.ps1 -Step Db.AppSettings,Iis.WebConfig`. It writes `appsettings.Site.json` and removes the old `appsettings.<Environment>.json`; an old `appsettings.Production.json` that holds settings the installer doesn't write is kept, with a warning naming them, so you can move them into `appsettings.Site.json`. Check the database connection afterwards: a Test or Staging install now uses the database you gave the installer. **If the installer found a trusted certificate**, BlueTrack starts sending HSTS, so browsers will then refuse plain HTTP and certificate warnings for that host name.
- **Entitlement members:** the installer, or the Migrator on its own, applies `24_BlueTrack_EntitlementMemberResolution.sql`. The report shows names at once; the next nightly Import and Load resolves members by name.
- **Account classification:** the installer, or the Migrator on its own, applies `25_BlueTrack_AccountClassification.sql`. Nothing changes until you turn classification on and add rules on **Admin > Account Classification**; the next AD Account Discovery run then reads the attributes the rules need (until then, only sAMAccountName and OU rules can match).

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
