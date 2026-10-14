# Admin Guide: Ongoing Data Management

**Blueprint Progress Tracking Web Interface**

## Who this is for

An admin (or whoever holds elevated SQL Server access) handling the recurring, day-2 tasks on an already-configured environment — picking up after [Admin_Configuration.md](Admin_Configuration.md). These are the things that come up repeatedly once BlueTrack is in regular use, roughly in the order you'd typically check them.

## 1. Confirm the nightly Import + Load job ran

The Import+Load SQL Agent job runs at 2:00 AM, refreshing staging data from your CyberArk exports (Privilege Cloud, Self-Hosted, or both, as chosen at install) and then recalculating risk scores and running AD Account Discovery. Each run also creates an *Other*-type Target (risk score 0, discovery source *CyberArk ETL*) for any account address that has no Target yet, and links each account to the Targets matching its address. Those placeholder Targets haven't been reviewed, so check the **Targets** page for new ones now and then. The same job then takes the daily snapshot behind **Reports > Trends**, even on a night the Import or Load fails.

To hear about a failure without checking, turn on the **NightlyImportFailed** notification on **Admin > Notifications** (off on a new install): it emails the failing step, the error and the files that failed, within about 5 minutes. A Privilege Cloud export missing a column the import needs fails the run with the file and column named; **Test** on **Admin > Data Sources > Edit CyberArk sources** checks the files' columns ahead of time. To check by hand, see `Admin_OperationsGuide.md`'s "The Nightly Import + Load Job" section for the query.

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

**Admin > Audit Log Viewer** is read-only — filter by Event Type, Entity, and a date range, and click any row's **Occurred At** to expand the Reason and the specific Field / Old Value / New Value change.

## 8. Review Risk Exception segregation-of-duties cases

**Reports > Risk Exception SoD** lists every historical case where the same person both approved a Risk Exception and later linked it to an account — regardless of whether the segregation-of-duties checkbox (`Admin > Global Application Configuration`) was on at the time. Worth a periodic look even with enforcement off, since it's a detective control, not just a preventive one.

## 9. Update to a new version

From an elevated PowerShell session, run `Deploy\Update-BlueTrack.ps1` to download the latest signed release and redeploy from it; the redeploy takes a pre-deployment backup first. Afterwards, **Admin > Deployment** (or **About**, in the menu with your name) shows the version now running. See `Admin_OperationsGuide.md`'s "Updating to a New Version" section, and `Admin_DeploymentRunbook.md`, "After upgrading", for steps a particular upgrade needs.

## See also

- `Admin_OperationsGuide.md` — the authoritative reference for every task above with no UI of its own.
- `Admin_DeploymentRunbook.md` — the First Data Load procedure, if you're loading a brand-new data source for the first time.
- `Claude_Docs/Design_Risk-Exception-Tracking.md` — the full Risk Exception approval workflow, including the segregation-of-duties check.
