# Release Notes

What changed in each BlueTrack release, newest first, for users and administrators. Each release lists what's new, what changed, what was fixed and what to do when upgrading. Details are in the guides named alongside each item.

## Unreleased

### New

### Changed

- **The DNA reports and DNA imports use less memory.** Reports > DNA Accounts and DNA Hard-Coded Credentials ask SQL Server for a small fraction of the memory they did for each page (in testing, 6 MB instead of 98 MB), with the same rows in the same order; a DNA import keeps its accounts in memory once instead of twice. (Administrator Operations Guide)
- **`Get-BlueTrackResourceUsage.ps1` explains more.** It shows where the server's memory is (processes, Windows' own use, the file cache) and when the server started, and warns when SQL Server is short of memory or has kept less than an hour of statement history, so a report that can't name the slow query says why. Without `-SampleSeconds` it reminds you to sample while using the slow page. (Tools README)

### Fixed

- **A Progress Rule or DNA tracking rule condition on a date field (a number of days) saves again.** Typing the number of days made the rule fail to save or test; it only worked when the number had been typed into another field first. Changing a condition's field to one with another kind of value (Yes/No, a source, a number of days, text) now also clears the old value. (Administrator Settings)
- **Safe access policies now match the move permission across Privilege Cloud and Self-Hosted.** Self-Hosted reports "Move accounts and folders" as two permissions (move from, move into) where Privilege Cloud has one, so a Safe role naming the one failed on every Self-Hosted Safe, and with Exact match the two counted as extra. They now count as the same permission, both ways; the next check (nightly, or Check now) shows the corrected result. (Administrator Settings)

### Upgrading

## 1.5.4 (2026-11-12)

### New

- **`Tools\Get-BlueTrackResourceUsage.ps1`** collects what BlueTrack and SQL Server use of the server's memory and CPU (optionally sampled while you run a slow page) and SQL Server's heaviest statements, read-only, into a file to send with a performance question. (Tools README)

- **See which file an Import is on.** Under Import and Load on Admin > Data Sources, an Import row has **Files**: each Privilege Cloud export file it imported, with its rows, how long it took and its result or error, and the Self-Hosted Direct pull it checked. A running Import names the file it is on. (Administrator Operations Guide)

### Changed

- **A DNA import uses much less memory.** The workbook is read a row at a time instead of being loaded whole; on real DNA reports the import took about a third of the memory and half the time. The accounts read are the same. (Administrator Operations Guide)
- **The API uses less memory.** It now runs .NET's workstation garbage collector instead of the server one, which kept more memory per CPU core than a single BlueTrack site needs; in testing, the same reports peaked about 14% lower and settled about 30% lower, at the same speed. No setting to change. (Installation Guide)
- **Audit sealing and SIEM forwarding check every 10 seconds** (was 5), halving their idle load. Audit events reach the SIEM up to 10 seconds after they happen; after a failed send it retries from 10 seconds, up to every 5 minutes. (Administrator Operations Guide)

### Fixed

- **An upgrade no longer fails its last step because of last night's data.** The installer's smoke test now warns about a health check that reports a problem with the data (for example, Pull and Load when the latest Import failed) instead of failing the install; it still fails when the site doesn't answer or a check is unhealthy. Check the warning on Admin > Data Sources. (Upgrade Guide, Installation Guide)
- **The installer's "continue" hint shows the full command.** When an install stops, it prints the installer's full path with `-SiteName` and `-Resume`. The short `-Resume` hint didn't work when the install was started by `Update-BlueTrack.ps1`, which has no `-Resume`. (Upgrade Guide)

- A Self-Hosted account that other accounts use as their logon or reconcile account now shows its **folder** (taken from those accounts), and the nightly pull no longer warns that its details weren't found. Its device type and creation method stay blank: CyberArk's search can't return such an account. (User Guide)

## 1.5.3 (2026-11-05)

### New

- **DNA:** the newer DNA report (its Windows, Unix, Domain and Database scan sheets) is imported, with more of its columns (machine type, OS version, description, threat cause, pass-the-hash, service account, SPN, roles and permissions). A data feed can import the DNA workbook from a folder every night. On Reports > DNA Accounts, click a heading to sort, and tick accounts and **Track in BlueTrack** to make them BlueTrack accounts (one per machine; they appear in Account Progress at the first stage). DNA tracking rules on Admin > Data Sources track matching accounts after each import. Admin > Data Sources can purge the DNA data, delete imports after a number of days, and choose DNA columns that only holders of the new **ViewDnaSensitiveColumns** permission may see. A new report, **Reports > DNA Hard-Coded Credentials**, lists the hard-coded credentials DNA found (machine, application, file, account, target, and DNA's line from the file with the password masked); only holders of ViewDnaSensitiveColumns can open or export it. (Administrator Data Management; User Guide)
- **See where a pull or load is.** On Admin > Data Sources, a running Self-Hosted Direct pull shows the step it's on and how far it has got (for example "Account details (1,200 of 2,454), for 12 min"), and **Pull details** lists every step of a pull with what it read and how long it took. A pull stopped by its time limit or by **Cancel pull** says which step it was in. A new **Import and Load** section lists the nightly job's recent runs and the Load's procedures one by one, and for a run still going, the procedure it's in, what its SQL Server session is waiting on and which session is blocking it. (Administrator Data Management)

- **A stuck pull or load is reported:** under Import and Load on Admin > Data Sources, set **Alert after (minutes)** (30 by default). A pull that reads nothing, or a Load procedure, Import or Snapshot still running, for that long sends the new **RunStalled** email (turn it on under Notifications) with what the load is waiting on, and makes the new **Pull and Load** check on Admin > Deployment (and `Test-BlueTrackHealth.ps1`) unhealthy. It is degraded when the latest pull or load failed. **Cancel load** stops a running Import and Load job; the Load is rolled back and the run is logged as failed. (Administrator Operations Guide; Settings Reference)

- **Risk scores use DNA findings.** **Admin > Risk Score** (was Risk Score Bands) now has three tabs. **Bands** is as before. **Defaults** sets the score of a target made from an account's address, the starting score of a new target and access group, and a risk value for each kind of finding. **Findings** lists what raises scores now. A hard-coded secret DNA found raises the system it was found on (every account that reaches it) and the account it names, more when the account is privileged; Pass-the-Hash and non-compliant DNA accounts raise the BlueTrack account they are linked to. A finding counts like a target in the risk algorithm, and the Risk Score report's drill-down lists it. Finding values start at 300 (hard-coded secret), 600 (privileged), 400 (Pass-the-Hash) and 200 (non-compliant); set them to 0 or untick them to leave scores as they were. (Administrator Settings Reference; User Guide)

### Changed

- **DNA accounts are matched to BlueTrack accounts by their Account Type:** a local account (including a SQL login) by user name and machine, a domain account by user name and its domain. Groups aren't matched. An account DNA could only give as a SID (an account in a trusted domain, or a deleted one) is marked as an unresolved SID, can be filtered on, and is matched to a BlueTrack account kept under that SID, such as an accepted discovered account. (Before, a domain account matched any BlueTrack account with its user name.) (User Guide)
- **Progress Rules:** a **Source** condition's value is picked from the source systems set up here (one for is / is not, any number for is one of), the field list is in alphabetical order, and conditions ignore upper and lower case on any SQL Server. Drop-down lists across BlueTrack are in alphabetical order, except where the order means something (stages, risk levels). (Administrator Settings Reference, Progress Rules)
- The account's **folder** is shown beside its Safe; for a Self-Hosted account whose details haven't been read yet it says "(not read yet)". (User Guide)
- **Who can use this account** on an account's page lists each group that is a member of the account's Safe (Vault or AD) as one row with its rights and member count; open it to see the people in it. A group whose AD members weren't read is still listed and says why. (User Guide)

### Fixed

- The DNA section of Admin > Data Sources no longer says the hard-coded credentials sheet is never read; it is listed on Reports > DNA Hard-Coded Credentials.
- Typing in the Search or Owner box on Accounts no longer makes the list reload and lose characters when you type quickly: the address now changes once you pause, with the search.
- The installer now saves the Power BI reader account answer, so an unattended run (or an update) no longer stops to ask for it each time. Adding `"PowerBiReaderAccount"` to a saved answers file, as the 1.5.2 notes said, didn't work before this fix: the installer ignored it as an unknown setting.
- Running `Update-BlueTrack.ps1` again for a version it already downloaded no longer stops: it uses that download again and offers its installer, which keeps the answers it saved, so an install that stopped part-way can be picked up.

### Upgrading

- The upgrade adds database script 70 (DNA: the newer format's columns, tracking, the tracking rules, retention and restricted columns, and the ViewDnaSensitiveColumns permission, given to Admin); the installer applies it. An Admin who was signed in sees the new permission after **Reload My Rights**.
- The upgrade adds database script 69 (Progress Rule conditions ignore case); the installer applies it.
- The upgrade adds database script 68 (the pull and load progress); the installer applies it. The installer's new step `Db.SessionActivity` lets Data Sources show what a running load waits on: it signs one database procedure with a certificate that holds SQL Server's VIEW SERVER STATE permission, instead of giving that permission to BlueTrack's own login. It needs the installer to run as a SQL Server sysadmin; otherwise it warns and the install goes on, and Data Sources shows the running step without its waits.

## 1.5.2 (2026-11-05)

### New

- **Progress against target dates** on Reports > Trends: planned (target date reached), completed and overdue accounts over time, for sponsors. The Trends export adds two columns at the end. (User Guide)
- **Reports > Safe Administrators**: who can manage Safes (Manage Safe, Manage Safe Members) and who holds full rights, by Safe or by person, with CyberArk's built-in users hidden unless you ask for them. (User Guide)
- **Emails to application owners.** With the **OverdueRiskExceptions** notification on, each application's owner (its Owner Email) gets a daily email of their applications' exceptions that are overdue for review. Two options on Admin > Notifications, both off: also list exceptions on single accounts in the application, and remind the owner some days before the review date. (Administrator Settings Reference)
- **AD domains for discovery only**: an LDAP domain can be a place AD account discovery looks without BlueTrack's own lookups using it (untick **Use for BlueTrack's own lookups**); Data Sources' **Add a domain for discovery only** opens such a domain. (Administrator Settings Reference)
- **Include CyberArk's built-in Safes** on Admin > Data Sources > CyberArk sources (off by default): Self-Hosted Direct also reads the Vault's internal Safes and their members, to review whether that data is useful. (Administrator Settings Reference)
- **CyberArk Discovery & Audit (DNA) as a data source**: upload DNA's account-list workbook on Admin > Data Sources, and see its accounts on **Reports > DNA Accounts**, with whether BlueTrack tracks each. Its hard-coded credentials are never read. (Administrator Data Management)
- **Use as a data source** on each LDAP domain (Admin > Credentials & LDAP, and listed on Admin > Data Sources with **Add a domain**): AD account discovery reads only the domains ticked. Every existing domain starts ticked, so nothing changes until you untick one. (Administrator Data Management)
- **Power BI reader role**: the installer's `Db.PowerBiAccess` step (`-PowerBiReaderAccount DOMAIN\Name`) creates the database role `BlueTrack_PowerBI_Reader`, which may read only the Power BI views and tables, and adds that account or group to it. Admin > Power BI says whether it's set up. (Administrator Operations Guide)
- **Other PVWA addresses** on Admin > Data Sources > CyberArk sources: Self-Hosted Direct tries them in order when the PVWA address can't be reached, and says when it did. A refused credential is never tried on another. (Administrator Settings Reference)
- **Accounts export and import**: **Export CSV / Excel** on Accounts downloads the filtered list with its editable fields, and **Import changes...** reads such a CSV back, saving each row's changed values with the edit page's rules and reporting any row it couldn't apply. (User Guide)
- **Active Directory account discovery on Admin > Data Sources**: when it runs (default 04:30), **Run now**, and its recent runs with what each found and any domain it couldn't read. Before, it ran a day after whenever the application last started, and kept no record. (Administrator Data Management)
- **Bulk override on Reports > Risk Score**: select accounts and set an override score (with a reason) on all of them, or clear it. Needs `EditAccountProgress`, as on one account. (User Guide)
- **Admin > Power BI** (permission `ViewDeploymentInfo`): the server and database to connect Power BI to, the views and tables to import with their columns, how to relate them, and a script that gives an account read access to just those. (Administrator Operations Guide)
- **Version files.** A `VERSION` file at the top of a release's folder names its version, and the installer writes `BlueTrack.version.txt` (the version, and when it was built) into the API's install folder. (Upgrade Guide)
- **Reason for CyberArk CP and CCP retrievals.** Admin > Secrets Store Configuration takes an optional **Reason** for the Credential Provider and the Central Credential Provider; BlueTrack sends it with each retrieval, and the Credential Provider records it in its audit log. (Administrator Settings Reference)
- **Safe pages.** An account's page and the Accounts list (a new **Safe** column, sortable) name the account's Safe; the name opens the Safe's page, with the BlueTrack application mapped to it, whether it is flagged for deletion or ignored, its account counts and its first 50 accounts, and **Show all on Accounts**, which filters the list to that Safe. (User Guide)
- **Home page.** Signing in opens Home: a greeting, your roles, a card for each area you can open and, if you can see tracked data, the KPI funnel (all accounts, in scope, onboarded, managed, compliant; each count opens Accounts filtered to it). The Dashboard moved to its own top-nav entry, at `/dashboard`. (User Guide)
- **Archived status.** An account can be Archived: out of scope for the KPIs like Risk Accepted / Excluded, but with no Risk Exception, and counted on its own on the KPI Summary and the Dashboard. A Progress Rule can now archive accounts at any stage (Admin > Progress Rules, "Archives accounts"), for example accounts whose CPM disable reason is ADArchive in an Archive folder. An archived account stays Archived until someone changes its status. (User Guide; Administrator Settings, Progress Rules)
- **Folder** of a Self-Hosted account, read from PVWA, on the account page and as a Progress Rule field, with **Why the CPM doesn't manage it** (the CPM disable reason) as another rule field. (User Guide; Administrator Settings, Progress Rules)
- **Re-read account details**: an option on Admin > Data Sources > CyberArk sources to re-read the folder, device type and linked accounts of accounts read more than a number of days ago, up to a number of accounts per pull. Off by default. (Administrator Settings, CyberArk sources)
- **Safe Access Policies** (Admin > Safe Access Policies, new permission `ManageSafeAccessPolicies`, Admin): Safe roles (sets of Safe permissions) and policies, chosen by Safe name pattern, saying which members a Safe must have with which role (optionally with people in them, from the Vault or AD) and which it must not have (for example Vault Admins). Every Safe with accounts is checked each night, or with **Check now**; the results are on the new **Reports > Safe Access**. Progress Rules can use **Safe access compliant**. A disabled example policy is included to edit. (Administrator Settings, Safe Access Policies; User Guide, Reports)
- **Compliance review**: holders of the new `ReviewAccountCompliance` permission (Admin and Approver) mark an account's compliance reviewed on its page (**Compliance** section), or every account of a Safe at once on Reports > Safe Access. A review lasts until the account moves to another Safe or someone clears it. Progress Rules can use **Compliance reviewed**. (User Guide)

### Changed

- **Who can use this account** names the nested groups for someone who reaches a Safe through a group inside a group, for example **Via AD group:** Safe Admins > Team A > Sub-team (the shortest such chain). It shows after the first Self-Hosted Direct pull after the upgrade. (User Guide)
- **Page layout**: pages use the system's own font; the top nav marks the area you're in and wraps on a narrow window, where tables scroll sideways instead of widening the page; the Admin and Reports lists are in titled groups, with the open page marked; column headings are plain text with the sort arrow; the page controls show only when there is more than one page; on Accounts, **Deleted accounts** and **Linked feed accounts** are under **More filters**, and Export and Import changes sit on one row; the Dashboard puts what needs attention first and keys its pie charts; Home's cards show what needs attention (overdue accounts, exceptions past review or awaiting approval); on an account's page the fields sit side by side, Save and Cancel stay in view, and **Delete account...** is at the end under **Deletion**; the sign-in page shows no menu. Admin is in the menu only for someone with an Admin page. (User Guide)
- **Everything discovered** counts an account that both AD discovery and DNA found once; dismissed AD candidates count, and only duplicates and linked accounts are left out. (User Guide)
- The KPI funnel now starts with **Everything discovered**: every tracked account plus the AD-discovered and DNA accounts not tracked yet, on Home, the Dashboard and Reports > KPI Summary (and its export). (User Guide)
- The Accounts import reads Excel files as well as CSV, can clear fields whose cells are blank when you tick that option (off by default), and sets or clears risk score overrides from the export's new **Risk Score Override** and **Override Reason** columns. (User Guide)
- Re-importing **Target Inventory** also updates a matched Target's name, type and (when the file has one) description, as well as its risk score. (User Guide)
- More of CyberArk's built-in Safes are recognised as internal (AccountsFeedDiscoveryLogs, AccountsFeedADAccounts, the PasswordManager Safes, PVWAReports and others the CyberArk documentation names), so their accounts and Safe pages say so.
- Re-importing **Target Inventory** (or its feed) updates an existing Target's **Risk Score** to the file's, and the accounts that reach it are recalculated. Its name, type and description still aren't changed by an import. (User Guide)
- **Test** on Admin > Data Sources > CyberArk sources is quick for Self-Hosted: it reads one Safe and one account and PVWA's totals, instead of everything a pull reads. **Test with a full read** does the full read. (Administrator Settings Reference)
- The Settings Reference says how to point BlueTrack at several PVWAs behind a load balancer (its address, with sticky sessions, as CyberArk requires). (Administrator Settings Reference)
- Choosing the CyberArk CP secrets backend on a server where BlueTrack was built without the CyberArk Application Password SDK now says so, and how to add it (re-run the installer's `Build.Api` step once the Credential Provider is installed). (Administrator Operations Guide)
- "In scope" on the KPI Summary, Dashboard and Trends now also leaves out Archived accounts.
- The nightly load now loads Safe members before it applies the Progress Rules, so rules see the same night's Safe access results.
- **Pull now** on Admin > Data Sources now runs the Import and Load once the pull succeeds (by starting the nightly SQL Agent job), so the accounts change within minutes instead of at the next night. The pull's row shows how the load went. (Administrator Settings, CyberArk sources)
- **Reload My Rights** reads a Windows user's groups from Active Directory (through the LDAP domains on Admin > Credentials & LDAP), so a group change applies without signing out of Windows; it keeps reading AD until the user's next Windows sign-in. With OIDC or SAML sign-in the page says a group change needs signing out and back in. (User Guide, Profile & Preferences)
- The Dashboard, Accounts, Exceptions and the reports of tracked data open **Access Denied** for someone without the permission they need, instead of showing "Request failed: 403". Access Denied offers **Go to the Dashboard** only to those who can open it. (User Guide)
- The top nav no longer shows Dashboard, Accounts, Exceptions or Reports to someone who can't open them. **Reports** opens the first report they can see. (User Guide)
- Saving or deleting an LDAP domain, changing a notification type, updating or deleting a recipient, and saving an account now show the server's reason when they fail, not only the status code.

### Fixed

- The nightly Self-Hosted Direct pull, AD account discovery, data feeds and notifications run on a server where nobody uses BlueTrack overnight. IIS stopped the BlueTrack application after 20 idle minutes, so they didn't run, and the nightly import then failed because the pull was too old. The installer now keeps the application running (Installation Guide).
- **Recalculate Now** on Reports > Risk Score recalculates every score. Before, it redid only scores already marked as needing it, so after a change of risk algorithm it changed nothing. Changing the risk algorithm in Global Configuration now has the next nightly load redo every score too.
- The **Pull details** dialog on Admin > Data Sources follows the theme; it was always white, which left light text on white in the Dark theme. Failed results on Data Sources and Edit CyberArk sources use the theme's error color.
- On Admin > Secrets Store Configuration, the **Credential** column shows a box only for a backend that uses one (Conjur, Azure Key Vault as a service principal, AWS with an access key); the others say "(not used)".
- The **Search** and **Owner** boxes on Accounts search once you pause typing, not on every keystroke.
- The installer's `Db.SqlServerAudit` step no longer re-grants "Generate security audits" on every run and then asks for a SQL Server restart that isn't needed.
- When the installer runs where it can't ask a question (a scheduled task, or with its input redirected) and an answer is missing, it stops and names the parameter or `-ConfigFile` entry to add. Before, it failed with "You cannot call a method on a null-valued expression", or took a yes/no answer as its default without asking.

### Upgrading

- The installer adds the IIS **Application Initialization** feature (step `Prereq.IisAppInit`) and sets BlueTrack's application pool never to stop for being idle and the API to start with it. Nothing to do by hand; say yes when it offers to install the feature.
- The upgrade adds database script 67 (the application owners' email options); the installer applies it. With **OverdueRiskExceptions** on, application owners with an Owner Email start getting their own email the day after the upgrade; clear an Owner Email to leave that owner out.
- The upgrade adds database script 66 (the nested groups on Via AD group); the installer applies it.
- The upgrade adds database script 65 (Use for BlueTrack's own lookups, on for every existing LDAP domain); the installer applies it.
- The upgrade adds database script 64 (the built-in Safes option, off); the installer applies it.
- The upgrade adds database script 63 (the DNA accounts and their imports); the installer applies it.
- The upgrade adds database script 62 (the LDAP domains' Use as a data source, on for every existing domain); the installer applies it.
- The upgrade adds database script 61 (the other PVWA addresses); the installer applies it.
- The upgrade adds database script 60 (the longer list of CyberArk built-in Safes); the installer applies it.
- The upgrade adds database script 59 (AD Account Discovery's run time and run log); the installer applies it. AD Account Discovery now runs at 04:30 server time; change it on Admin > Data Sources.
- The installer asks one new question, the Power BI reader account (leave it blank to skip). For an unattended upgrade, add `"PowerBiReaderAccount": ""` (or the account) to the saved answers first, or the installer stops and asks you to.
- Re-run the installer's `Db.AppPoolAccess` step: it adds the app's SQL account to msdb's `SQLAgentOperatorRole`, which Pull now needs to start the Import and Load job. Without it, Pull now still pulls and says the job couldn't be started.
- The first Self-Hosted Direct pull after the upgrade reads every account's details once more (one PVWA call per account) to fill in the folder, so it takes longer than usual; on a large Vault it may finish over a few nights within the pull time limit.

## 1.5.1 (2026-10-22)

### New

- **Admin > Identity Providers**: **Remember each browser's last method**. When ticked, the sign-in page starts the method that browser last signed in with instead of the default.
- **Admin > Platform Mapping** suggests an account type and source of record for each unmapped platform: the mapping of the same platform in the other CyberArk source, or one that fits a word in the platform's name (Windows domain, Unix, Oracle and so on). **Use** fills in one, **Fill in suggestions** fills in all of them; nothing is saved until you click Save on the row. Group platforms get no suggestion.
- **Global Application Configuration**: **Number and date format** (for example en-US, en-GB or de-DE) for the emails and the run summaries the server writes. It shows an example as you type.
- **SBOMs**: each release now includes CycloneDX software bills of materials for the API, the database migrator and the web app (`Sbom/` in the repository), listing every package with its version and licence.
- **Tamper-evident audit log**: BlueTrack seals every audit event as it is written. **Verify** on the Audit Log page checks the seals and shows any event that was changed or deleted outside BlueTrack, and the new **AuditChainBroken** notification runs that check every day. The installer can also record every change to the audit log in the Windows Security log (SQL Server Audit).

### Changed

- Dates on the pages are shown in your browser's or chosen language's format (for example 10/21/2026 or 21.10.2026) instead of 2026-10-21, and more counts, percentages and risk scores use its number format (1,234 or 1.234). The Deployment page's backup runs and SIEM Forwarding show times in your own time zone.
- Notification emails and the data feed and Self-Hosted pull summaries use the installation's number and date format (en-US unless changed), with thousands separators.
- Some account fields now need a permission of their own to change by hand: **Source of Record** needs the Admin role's Curate Platform Mapping, **Actual Completion Date** needs Confirm Reconciliation (Admin, Approver), and **Risk Level** needs Manage Targets (Admin, Analyst). The account page shows such a field read-only, with the permission it needs, to anyone without it, and bulk edit doesn't offer it. Progress Rules still move stages each night.

### Upgrading

- The upgrade sets those field permissions where none was set. To keep a field open to every editor, clear its **Required permission** on Admin > Field Metadata after upgrading.
- Re-run the installer's `Db.AppPoolAccess` step so the app's SQL account can't change the new audit seals, and run the new `Db.SqlServerAudit` step (answer yes). If it says SQL Server needs a restart, restart the SQL Server service at a quiet time and run `Db.SqlServerAudit` again. The existing audit log is sealed in the background after the upgrade, oldest first.

## 1.5.0 (2026-10-21)

### New

- **Reports > CPM Health**: every account whose CPM status is failure, longest failing first, with its last CPM action. Each account's page has a **CPM** section (status, managed by the CPM, last verified, reconciled and changed, and whether its platform requires PSM).
- New **CpmHealth** notification: once a day, the accounts that newly fail.
- Progress Rules can test **Platform requires PSM** (Self-Hosted).
- **Admin > Progress Rules**: rules that move accounts to a later Blueprint stage each night when all their conditions match (CPM status, last verified, Safe, platform, owner and more), with **Test** to see what a rule would move. The old automatic move to Onboarded to Vault is the first rule.
- **Admin > Blueprint Stages**: rename, reorder, add and delete the stages, choose where new accounts start and which stages the Onboarded, Managed and Compliant KPIs count from.
- Rejected SAML and OIDC sign-ins are recorded in the Audit Log (LogonFailed) and can send the new **SignInRejected** notification.
- New **SamlCertificateExpiring** notification: a warning 30 and 7 days before the SAML identity provider's or BlueTrack's own SAML certificate expires.
- **Reports > Unresolved Entitlement Members**: **Resolve** and **Undo** now work for Self-Hosted members too; a member is linked to a user or group of its own source.
- **Account page, Self-Hosted**: the account's **Device type**, how it was created, and its linked **Logon account** and **Reconcile account** (a link when BlueTrack tracks it). Read from PVWA's classic API, only for new or changed accounts after the first pull. Also in `vw_powerbi_accounts`.
- **Who can use this account** on the account page: everyone who reaches its Safe, directly, through a Vault group, or through an AD group whose users the nightly pull reads from AD, with Use, Retrieve and List; and the CyberArk Applications that can retrieve it.
- **Reports > CyberArk Applications**: the application IDs in Self-Hosted CyberArk, with their authentication methods (no hash values) and how many accounts and Safes each can retrieve from.

### Changed

- The automatic move to Onboarded to Vault now also moves accounts whose status isn't **Not Started** (any status but Risk Accepted / Excluded), and the Compliant KPI counts the Compliant stage or any later stage you add.

### Fixed

- A Self-Hosted Direct pull now tries a failed account-detail call once more before warning about it.

### Upgrading

- Database script 50 is applied. Turn on **CpmHealth** on Admin > Notifications to get the daily email.
- Database script 49 is applied. Turn on **SignInRejected** and **SamlCertificateExpiring** on Admin > Notifications (both start off) if you use SAML or OIDC. Step-up MFA for admin work is left to your identity provider (for example a conditional access policy).
- Run the installer as usual; database script 48 is applied. The first Self-Hosted Direct pull afterwards reads every account's classic details (one PVWA call per account), so it takes longer than usual; raise **Pull time limit** on Edit CyberArk sources if it stops, or let it finish over the next nights. The CyberArk Applications need the PVWA user's Audit Users authorization, and the AD group read needs an enabled LDAP domain on Admin > Credentials & LDAP.

## 1.4.0 (2026-10-21)

### Changed

- **The EVD is no longer a Self-Hosted source.** Self-Hosted comes only from PVWA (Self-Hosted Direct), so the EVD database setting, the source choice on Edit CyberArk sources, the shadow pull and its comparison, and the installer's `-EvdDatabaseName` answer are gone. The installer's `-ImportSources SelfHosted` now turns on Self-Hosted Direct; enter its PVWA address and credential on Admin > Data Sources.
- **Data Sources** no longer says "Self-Hosted: off" beside the Self-Hosted Direct line when Direct is on.

### Upgrading

- **A site still importing Self-Hosted from the EVD can't take this upgrade.** The installer checks first and stops, changing nothing; switch to Self-Hosted Direct on the version you have (its Deployment Runbook, "Moving Self-Hosted from the EVD to Direct"), then upgrade. Any other site: the upgrade adds database script 47 (removes the EVD and the shadow pull; the comparison's stored history stays in the database), and the installer applies it.

## 1.3.4 (2026-10-21)

### New

- **Upgrade Guide** (`User_Docs/Admin_UpgradeGuide.md`, also published as a Word document): which version to install from the one you run now (a site on the EVD stops at 1.3.4 and moves to Self-Hosted Direct first), what to do before and after an upgrade, the hand-made steps of each version, and how to go back if an upgrade breaks something (restore the pre-upgrade backup and reinstall the previous version).

### Changed

- **Moving from the EVD to Self-Hosted Direct** has its own section in the Deployment Runbook: what to check before switching, the switch and first pull, what the first Direct night should look like, and how to switch back.

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
