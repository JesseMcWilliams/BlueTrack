# Admin Guide: Ongoing Data Management

**Blueprint Progress Tracking Web Interface**

## Who this is for

An admin (or whoever holds elevated SQL Server access) handling the recurring, day-2 tasks on an already-configured environment — picking up after [Admin_Configuration.md](Admin_Configuration.md). These are the things that come up repeatedly once BlueTrack is in regular use, roughly in the order you'd typically check them.

## 1. Confirm the nightly Import + Load job ran

The Import+Load SQL Agent job runs at 2:00 AM, refreshing staging data from your CyberArk exports (Privilege Cloud, Self-Hosted, or both, as chosen at install) and then recalculating risk scores and running AD Account Discovery. Each run also creates an *Other*-type Target (risk score 0, discovery source *CyberArk ETL*) for any account address that has no Target yet, and links each account to the Targets matching its address. Those placeholder Targets haven't been reviewed, so check the **Targets** page for new ones now and then. There's no in-app UI showing whether last night's run succeeded — check via SQL directly. See `Admin_OperationsGuide.md`'s "The Nightly Import + Load Job" section for the exact query.

## 2. Work the Target Match Review queue

Weak import matches (e.g. an identifier that's only an IP address) land in **Admin > Target Match Review** for a human decision — **Merge**, **New Target**, or **Ignore**. Check this periodically so ambiguous imports don't sit unresolved.

## 3. Review Discovered Accounts

**Reports > Discovered Accounts** lists real AD accounts found nightly that aren't yet onboarded into CyberArk. If you hold `ManageDiscoveredAccounts`, use **Accept** to move a real candidate into Blueprint tracking, or **Dismiss** to resolve a false positive.

## 4. Maintain Import Mapping Profiles

If a data source's export format doesn't match this app's own generated CSV templates, define a reusable profile under **Admin > Import Mapping Profiles** rather than reshaping the source file by hand every time.

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

From an elevated PowerShell session, run `Deploy\Update-BlueTrack.ps1` to download the latest signed release and redeploy from it; the redeploy takes a pre-deployment backup first. See `Admin_OperationsGuide.md`'s "Updating to a New Version" section.

## See also

- `Admin_OperationsGuide.md` — the authoritative reference for every task above with no UI of its own.
- `Admin_DeploymentRunbook.md` — the First Data Load procedure, if you're loading a brand-new data source for the first time.
- `Claude_Docs/Design_Risk-Exception-Tracking.md` — the full Risk Exception approval workflow, including the segregation-of-duties check.
