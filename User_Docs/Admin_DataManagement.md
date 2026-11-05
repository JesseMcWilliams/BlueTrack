# Admin Guide: Ongoing Data Management

**Blueprint Progress Tracking Web Interface**

## Who this is for

An admin (or whoever holds elevated SQL Server access) handling the recurring, day-2 tasks on an already-configured environment — picking up after [Admin_Configuration.md](Admin_Configuration.md). These are the things that come up repeatedly once BlueTrack is in regular use, roughly in the order you'd typically check them.

## 1. Confirm the nightly Import + Load job ran

The Import+Load SQL Agent job runs at 2:00 AM, refreshing staging data from CyberArk (the Privilege Cloud exports, the Self-Hosted pull from PVWA, or both, as chosen at install) and then recalculating risk scores. AD Account Discovery is separate: BlueTrack runs it itself at the time set on **Admin > Data Sources** (default 04:30), and lists each run there. Each run also creates an *Other*-type Target (risk score 0, discovery source *CyberArk ETL*) for any account address that has no Target yet, and links each account to the Targets matching its address. Those placeholder Targets haven't been reviewed, so check the **Targets** page for new ones now and then. The same job then takes the daily snapshot behind **Reports > Trends**, even on a night the Import or Load fails.

**Active Directory account discovery** (on **Admin > Data Sources**) reads the members of the inventoried Access Groups from every enabled LDAP domain ticked **Use as a data source** (the domains are set up on **Admin > Credentials & LDAP**; Data Sources lists them with that tick, and **Add a domain for discovery only** opens a new one there that BlueTrack's own lookups won't use) and lists them on **Reports > Discovered Accounts**. It runs daily at **Runs at** (server time, default 04:30, after the 04:00 data feeds), and **Run now** runs it at once. Each run is listed with who started it, how it ended (**Succeeded**; **Partial** when some domains couldn't be read; **Failed**; **Skipped** when there's no enabled domain or no inventoried Access Group), how many accounts it found from how many domains, and what went wrong. Runs are kept as long as the data feeds' runs (**Data feed run history**, Global Configuration).

**CyberArk Discovery & Audit (DNA)** (on **Admin > Data Sources**): upload DNA's account-list workbook (.xlsx). Both the older workbook and the newer DNA report (its Windows, Unix, Domain and Database scan sheets) are read, with the newer report's extra columns (machine type, OS version, description, threat cause, pass-the-hash, service account, SPN, roles and permissions). BlueTrack stores the accounts and lists them on **Reports > DNA Accounts**, each with the BlueTrack account it matched (see "DNA accounts and BlueTrack accounts" below). Each upload replaces the one before, since it is the latest scan; the section shows the latest upload's file, time, who uploaded it and its counts. The raw database scan and any column of credentials on an account sheet are never read. The Audit Log records each upload with its counts.

- **Every night instead of by hand:** add a data feed of type **DNA (Discovery & Audit) workbook (.xlsx)**, pointing at the folder DNA saves its report to. Each night it imports the newest matching workbook; its **Test** reads the workbook and lists its account sheets.
- **Hard-coded credentials:** the workbook's hard-coded credentials sheet is stored for **Reports > DNA Hard-Coded Credentials**: where each credential is (machine, application, file), the account and target system, and DNA's line from the file with the password masked. BlueTrack masks again any password-style value it can still read, so no readable password is stored. The section says how many the latest import held; **See them** opens the report for holders of `ViewDnaSensitiveColumns`, the only permission that opens or exports it.
- **DNA tracking rules:** after each DNA import, each enabled rule tracks the DNA accounts that match all its conditions: each becomes a BlueTrack account at the first stage. Conditions test DNA fields (such as finding, machine, account name, account type, compliance status, threat cause, domain account, password last set, last login and **Unresolved SID (trusted domain or deleted account)**) with the Progress Rules' operators, and ignore upper and lower case. **Would track now** shows how many each rule matches; **New tracking rule** adds one, and **Apply the rules now** runs them at once. Deleting a rule leaves the accounts it tracked in BlueTrack.
- **Purge DNA data** (after a confirmation) deletes every DNA account and the import history now; accounts already tracked in BlueTrack stay. Under **DNA settings**, **Keep DNA data (days)** deletes imports older than that each night (blank keeps them until the next import replaces them, or a purge). Both are in the Audit Log.
- **Columns that need the ViewDnaSensitiveColumns permission:** tick the DNA columns only holders of `ViewDnaSensitiveColumns` (Admin on a new install) may see, then **Save DNA settings**. Anyone without it sees those columns blank on Reports > DNA Accounts and can't search or sort by them; the report says how many columns are hidden.

**DNA accounts and BlueTrack accounts.** A DNA account is matched to a BlueTrack account from another source with the same user name (ignoring case and any `DOMAIN\` prefix), by its Account Type: a local account (including a SQL login) when the BlueTrack account's address is its machine (full or short name), and a domain account whose domain is given when the address is that domain or its first part. Groups aren't matched. An account DNA could only give as a SID (an account in a trusted domain, or a deleted one) is marked as an unresolved SID and matched only to a BlueTrack account kept under that SID, such as an accepted discovered account. **Track in BlueTrack** on Reports > DNA Accounts (`ManageDiscoveredAccounts`) and the tracking rules make one BlueTrack account per machine, of source DNA: the same account on two machines is two accounts.

To hear about a failure without checking, turn on the **NightlyImportFailed** notification on **Admin > Notifications** (off on a new install): it emails the failing step, the error and the files that failed, within about 5 minutes. A Privilege Cloud export missing a column the import needs fails the run with the file and column named; **Test** on **Admin > Data Sources > Edit CyberArk sources** checks the files' columns ahead of time. To check by hand, see `Admin_OperationsGuide.md`'s "The Nightly Import + Load Job" section for the query.

**Self-Hosted pulls.** With Self-Hosted imported, BlueTrack pulls it from PVWA at the pull time (01:30 by default), before the job. The import fails if the latest pull didn't succeed or is more than 20 hours old, so check **Admin > Data Sources**, "Self-Hosted Direct pulls" (a ⚠ next to **Data Sources** in the Admin menu, and a banner on the page, mean the latest pull failed or was rejected). Turn on the **SelfHostedPullFailed** notification to be emailed instead. Open the pull's **Details** for its messages, then:

1. **Rejected**: the number of Safes or accounts changed by more than the **Allowed change** since the last good pull. The message names the Safes no longer returned and the new ones. If the change is expected (Safes deleted or added in CyberArk), click **Accept the change and pull**; accounts no longer returned are marked deleted at the next load. If it isn't, check the PVWA user is still a member of the Safes it should see (`Admin_OperationsGuide.md`, "Self-Hosted Direct Pulls"), then **Pull now**.
2. **Failed**: the pull didn't finish. Common causes: the credential was refused or its CP/CCP lookup failed (**Test** it on **Admin > Credentials & LDAP**), PVWA can't be reached from the server, or the PVWA address is wrong. A pull still running at the **Pull time limit (minutes)** (45 by default) is stopped and logged as failed; if your pulls are simply slow, raise the limit on **Edit CyberArk sources**. Fix the cause, then **Pull now**.
3. **Cancelled**: **Cancel pull** stops a running pull, logged as failed. Run **Pull now** again before the import.

In each case the staged data is left as it was, and once a pull succeeds, run the import again (`Admin_OperationsGuide.md`, "Re-running it manually") or let the next night's job run.

**Where a pull or load is.** A running pull's line names the step it's on and how far it has got, for example "A pull is running: Account details (1,200 of 2,454), for 12 min." A pull's **Details** lists its **Steps**, with what each read and how long it took. A pull stopped by its time limit or by **Cancel pull** says which step it was in ("It stopped during: ..."). The **Import and Load** section lists the nightly job's recent runs (Import, Load and Snapshot) with when each started, how long it took and its result; **Procedures** opens a Load's procedures one by one. For a run still going, it shows the procedure it's in, what its SQL Server session is waiting on and which session is blocking it. If the installer's `Db.SessionActivity` step hasn't signed the procedure that reads those waits (it needs a SQL Server administrator), the section says so and shows the running step without them. See `Admin_OperationsGuide.md`, "A Slow or Stuck Pull or Load".

**What a Self-Hosted Direct load fills.** Besides the accounts, Safes, members, users and groups, each account gets its creation time and its last CPM action, result and time (shown on its page), its CPM status and last reconcile; Safe members include CyberArk's predefined members, such as Administrator and Auditors; and members that match no user or group are listed on **Reports > Unresolved Entitlement Members**, where they can be resolved like Privilege Cloud members. An account in one of CyberArk's internal Safes, which PVWA doesn't return, is shown as **Deleted in CyberArk (internal Safe)**.

**New platforms.** An import can bring platforms BlueTrack hasn't seen (Self-Hosted gets one per policy, from each account's PolicyID). Open **Admin > Platform Mapping**: the platforms with no mapping are listed first. Give each an account type and, where it implies one, a source of record, and leave **also fill accounts already tracked** ticked the first time so the accounts already on the platform get them too (an account that has one is never changed). See *Administrator Settings Guide*, "Platform Mapping".

## 2. Work the Target Match Review queue

Weak import matches (e.g. an identifier that's only an IP address) land in **Admin > Target Match Review** for a human decision. Each row shows the imported target (name, source and risk score) beside the existing candidate:

- **Link** keeps the imported row as its own target, linked to the candidate, or to another target you find by typing in **Link To** (at least 2 characters of a name or identifier). The IP address stays where it is, and the pair counts once, at the risk score of the target it's linked to.
- **New Target** keeps it as a separate target.
- **Ignore** creates nothing, and later imports skip the same row.

Later imports of a row you've linked or kept go straight to that target, so each row is asked about once. Linked targets are hidden on **Targets** unless **Linked targets** is set to **Show**; open one with **Edit Target** to see what it's linked to, and **Unlink** it if the link was wrong. Check the queue periodically so ambiguous imports don't sit unresolved. Details: `Admin_SettingsReference.md`, "Target Match Review".

## 3. Review Discovered Accounts

**Reports > Discovered Accounts** lists real AD accounts found nightly that aren't yet onboarded into CyberArk. If you hold `ManageDiscoveredAccounts`, use **Accept** to move a real candidate into Blueprint tracking, or **Dismiss** to resolve a false positive.

## 4. Maintain Import Mapping Profiles

If a data source's export format doesn't match this app's own generated CSV templates, define a reusable profile under **Admin > Import Mapping Profiles** rather than reshaping the source file by hand every time. On the profile's page, pick a **Sample CSV file** to fill in the columns from its header row (read in your browser, not uploaded). On the Targets and Access Groups **Bulk Actions** pages, a file without a profile shows which required columns are missing, and **Create a mapping profile from these columns** starts a profile from it.

## 5. Check backups, and back up before risky changes

BlueTrack's rollback mechanism is full-database backup/restore, not per-script undo.

- **Scheduled backups:** set a **Scheduled backup time** and **Keep scheduled backups (days)** on **Admin > Global Application Configuration** (a blank time means none). Check the **Recent backups** table on **Admin > Deployment** now and then; a failed run gives its reason in the Note column.
- **Before a risky schema or configuration change**, take a backup on demand: the Deployment page's **Backup App** button (needs `TriggerBackup`), or from PowerShell:

  ```powershell
  .\Backup-BlueTrack.ps1 -SqlServerInstance "SQLSERVER01" -DatabaseName BlueTrack -BackupFolder "D:\Backups\BlueTrack"
  ```

- **If a backup is refused**, a broad group (such as Users or Everyone) can read the Backup Folder and BlueTrack wasn't allowed to remove that access. The Deployment page's **Backup Folder** health check shows this ahead of time. Fix the folder's permissions, or let the App Pool's account change them.

See `Admin_OperationsGuide.md`'s "Rollback" section for what the Backup Folder needs, and for the restore procedure if you ever need it — restoring is destructive and requires explicit confirmation by design.

## 6. Grant `db_backupstatus_reader` (one-time, per environment)

If the Deployment page's SQL Server Backup Status section shows an error instead of real backup history, a SQL Server account needs to be a member of `db_backupstatus_reader` in `msdb` — something BlueTrack can never grant itself. Resolve the target group on **Admin > Group → Role Mapping**'s Lookup / Test Tool, click **Generate db_backupstatus_reader Script**, and hand the downloaded `.sql` file to whoever administers the SQL Server instance to run manually via `sqlcmd`. See `Admin_OperationsGuide.md` for the exact command and the two real prerequisites (a SQL Server login, and an `msdb` database user for that login).

## 7. Review the audit log

**Admin > Audit Log Viewer** is read-only — filter by Event Type (from a list), Entity, **By** (who did it) and a date range, then **Filter**; **Clear filters** starts again. Times are shown in your local time (point at one for UTC), and the filters are kept in the page address, so a view can be bookmarked or shared. Click any row's **Occurred At** to expand the Reason and the specific Field / Old Value / New Value change.

## 8. Review Risk Exception segregation-of-duties cases

**Reports > Risk Exception SoD** lists every historical case where the same person both approved a Risk Exception and later linked it to an account — regardless of whether the segregation-of-duties checkbox (`Admin > Global Application Configuration`) was on at the time. Worth a periodic look even with enforcement off, since it's a detective control, not just a preventive one.

## 9. Update to a new version

From an elevated PowerShell session, run `Deploy\Update-BlueTrack.ps1` to download the latest signed release and redeploy from it; the redeploy takes a pre-deployment backup first. Afterwards, **Admin > Deployment** (or **About**, in the menu with your name) shows the version now running. See `Admin_OperationsGuide.md`'s "Updating to a New Version" section, and `Admin_DeploymentRunbook.md`, "After upgrading", for steps a particular upgrade needs.

## See also

- `Admin_OperationsGuide.md` — the authoritative reference for every task above with no UI of its own.
- `Admin_DeploymentRunbook.md` — the First Data Load procedure, if you're loading a brand-new data source for the first time.
- `Claude_Docs/Design_Risk-Exception-Tracking.md` — the full Risk Exception approval workflow, including the segregation-of-duties check.
