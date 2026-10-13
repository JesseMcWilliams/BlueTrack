# Operations Guide

**Blueprint Progress Tracking Web Interface**

## Scope

Task-oriented instructions for whoever deploys and operates BlueTrack (a DBA, or whoever holds elevated SQL Server access) — distinct from `Admin_DeploymentRunbook.md`, which walks through building or rebuilding an environment from scratch. This guide is about **day 2**: things a DBA/operator does occasionally on a running environment, outside of a full install, that have no UI of their own inside the application (or where the UI is only half the story).

It covers backups (scripted and scheduled) and restore, updating to a new version, the SQL Server grants BlueTrack can't give itself, when role changes take effect, the folders BlueTrack may read and write, settings that carry a security check, the nightly Import+Load job, the audit log purge job, changing identity provider/secrets store configuration on a running environment, and replacing the bootstrap admin group. `Admin_DeploymentRunbook.md` and `Deploy/README.md` remain the authoritative references for the full install/rebuild procedure — this guide doesn't repeat that ground, only what comes after it.

## Rollback (Backup and Restore)

BlueTrack's rollback mechanism is full-database backup/restore, not per-script "down" migrations — there is no automated "undo this specific schema change" path. Back up before a risky change, and be ready to restore and redeploy the previous build if something goes wrong.

### Taking a backup

`Deploy/Backup-BlueTrack.ps1` backs up the target database, verifies the backup is actually restorable (`RESTORE VERIFYONLY`), and writes a `.manifest.json` alongside it recording the database name, timestamp, and the git commit the backup preceded.

```powershell
.\Backup-BlueTrack.ps1 -SqlServerInstance "SQLSERVER01" -DatabaseName BlueTrack -BackupFolder "D:\Backups\BlueTrack"
```

Add `-IncludeMsdb` only if you also need SQL Server Agent job definitions (the nightly Import+Load job) protected — this backs up `msdb` too, with its own manifest entry. Think carefully before adding this: `msdb` is shared by every database on the SQL Server instance, so restoring it later is an instance-wide action, not scoped to just BlueTrack.

**This also happens automatically** as part of `Install-BlueTrack.ps1`, right before it migrates an *existing* database (a fresh, empty database has nothing worth backing up, so this is skipped automatically in that case). Pass `-BackupFolder` to control where it lands, or `-SkipPreDeployBackup` to opt out entirely for that run.

**The backup folder is locked down.** A backup is a full copy of the database, stored credentials included, so only specific accounts may read it. The installer and `Backup-BlueTrack.ps1` turn off permission inheritance on the folder (keeping its other entries), remove broad groups (Everyone, Users, Authenticated Users, Guests, Domain Users, Domain Guests) from the folder and from the backups already in it, and give **Modify** to the SQL Server service account and, from the installer, to the App Pool's account. A folder created directly under `C:\` otherwise inherits read access for every signed-in user.

### Scheduled backups and the Deployment page

BlueTrack itself can back up its database, on a schedule or on demand. Both write to the **Backup Folder** set on **Admin > Global Application Configuration**:

- **Scheduled backup time** — a daily backup at this server-local time. Blank (the default) means no scheduled backup.
- **Keep scheduled backups (days)** — after each scheduled backup, BlueTrack deletes its own scheduled backup files older than this (files named `<database>_scheduled_<UTC time>.bak`). Backups made with **Backup App** are never deleted.
- **Backup App** (**Admin > Deployment**, needs `TriggerBackup`) — backs up the database into the Backup Folder now, and downloads a zip of the API's `appsettings` files with a note of where the database backup went.

The Deployment page's **Recent backups** table lists every scheduled and manual backup: when it started (UTC), whether it was scheduled or manual (and by whom), the outcome, the file, how many old files were deleted, and a note. A failed run's reason is in the note.

**Before every backup**, scheduled or **Backup App**, BlueTrack checks whether a broad group can read the Backup Folder or a backup in it. If one can, BlueTrack removes that access (turning inheritance off, keeping the other entries) and says so in the note. If the App Pool's account isn't allowed to change the folder's permissions, the backup is **refused**, and the note names the groups and the reason. The **Backup Folder** health check on the Deployment page reports the same problem without changing anything, so you can see it before the next backup.

What the folder needs:

1. **Write access for the SQL Server service account** — SQL Server writes the file as that account.
2. **Modify for the App Pool's account**, so BlueTrack can delete expired scheduled backups. Without it the backup still succeeds and the note says the clean-up failed.
3. **A local folder, or one inside the installer's `-AllowedFileRoots`** (see "Data Feed and Backup Folders" below). With SQL Server on another machine, the path is on that machine; the backup works but the clean-up is skipped.
4. **`db_backupoperator` for the app's SQL account**, from `Database/Manual/02_BlueTrack_GrantAppServiceAccountAccess.sql` (see "Granting the app's own SQL Server access"). On an install from before this was added, re-run `.\Install-BlueTrack.ps1 -Step Db.AppPoolAccess`.

### Restoring from a backup

`Deploy/Restore-BlueTrack.ps1` restores a specific backup file — there is no "restore the latest one" shortcut, since silently picking the wrong backup is worse than requiring you to name it explicitly.

```powershell
.\Restore-BlueTrack.ps1 -SqlServerInstance "SQLSERVER01" -DatabaseName BlueTrack -BackupFilePath "D:\Backups\BlueTrack\BlueTrack_20260916-020000.bak"
```

This is destructive (`RESTORE DATABASE ... WITH REPLACE`) — you'll be asked to type `YES` to confirm interactively, or pass `-Confirm:$false` for an unattended/scripted run. **This only restores the database.** Also redeploy the application build matching the backup's manifest `GitCommit` field (if one was recorded), so the running application and the restored database stay in sync — this script does not touch IIS, the published API, or the built SPA at all.

To also restore `msdb` (only if you've confirmed a SQL Agent job definition was actually lost as a result of the BlueTrack restore), pass `-RestoreMsdbFilePath` pointing at the corresponding `msdb` backup. You'll get a second, separate warning before that step runs, naming the instance-wide blast radius again.

### What this does not solve

There are still no down-scripts for the `Database/*.sql` files — full-database restore remains the only rollback path. This is a deliberate scope boundary, not an oversight.

## Updating to a New Version

`Deploy/Update-BlueTrack.ps1` downloads a BlueTrack version from GitHub into a download folder and offers to redeploy this site from it. It needs only PowerShell and HTTPS access to `github.com`; git isn't needed on the server.

```powershell
.\Update-BlueTrack.ps1                                   # the latest release
.\Update-BlueTrack.ps1 -Release v1.2.0                   # a specific release
.\Update-BlueTrack.ps1 -Branch main -AllowUnsignedBranch # a branch, for testing only
.\Update-BlueTrack.ps1 -ListBranches -AllowUnsignedBranch
```

1. **Run it elevated** (Run as Administrator). The script refuses otherwise: what it downloads is later run as an administrator, so on every run it makes Administrators the download folder's owner and limits access to Administrators and SYSTEM.
2. **Signed releases only.** A release's tag must be an annotated tag signed with a key GitHub verifies; otherwise the script refuses it, and it downloads the exact commit the tag points to. A branch is never a signed release, so it needs `-AllowUnsignedBranch` (and shows a warning). Use branches on test environments only.
3. **Where it goes.** Each download is extracted to its own folder in the download folder (e.g. `BlueTrack-v1.2.0`), with a `download.json` recording the tag or branch and commit. Earlier versions stay for rollback; delete old ones yourself. The download folder is the `UpdateDownloadFolder` setting in this site's saved answers (`Deploy\State\answers.<SiteName>.json`); if it isn't set, pass `-DownloadFolder` or answer the prompt once, and it's saved.
4. **Redeploy.** The script copies the saved answers into the new copy's `Deploy\State` and asks whether to run that copy's `Install-BlueTrack.ps1` with them: a full install run, including the pre-deployment backup. `-RunInstaller` skips the question; otherwise it prints the command to run later.
5. **Afterwards**, use the new copy's `Deploy` folder for further installer runs and the next update, since its `State` folder now holds this site's answers and progress.

Upgrading a database built from older versions of the database scripts needs nothing extra: the installer's migration step recognizes them. A database that wasn't fully up to date with those older scripts is refused; update it from the `pre-sql-baseline` tag first. How to publish a signed release is in `Deploy/README.md` ("Updating to a new version").

## Granting `db_backupstatus_reader`

The Deployment page's SQL Server backup-status check reads `msdb.dbo.backupset` (and related tables) — something BlueTrack's own least-privileged SQL account cannot do by default, and something the application can never grant to itself (a connection can't widen its own permissions from inside itself). `Database/Manual/03_BlueTrack_GrantBackupStatusReaderRole.sql` creates a dedicated `db_backupstatus_reader` role with exactly the `SELECT` grants needed, and adds a specific account or group to it.

**Never run this through `App/Migrator`** — like `Database/Manual/04_BlueTrack_ScheduleImportLoadJob.sql`, it targets `msdb`, not the BlueTrack database. The Migrator never reads `Database/Manual/` at all; every script there is run by hand (`Database/README.md`).

### Getting a filled-in copy

The easiest path: on the **Group → Role Mapping** admin page, resolve the AD group that should be able to read backup status, then click **Generate db_backupstatus_reader Script** (see `User_Guide.md` for the exact UI steps) — this downloads a copy of the script with that group already substituted in.

Alternatively, edit `Database/Manual/03_BlueTrack_GrantBackupStatusReaderRole.sql` directly, replacing its `__TARGET_ACCOUNT__` placeholder by hand with the login/account that should be able to read backup status (e.g. `DOMAIN\BlueTrackAppPoolAccount`, or a resolved AD group's account name).

### Running it

```
sqlcmd -S <server> -C -i Grant-db_backupstatus_reader-<account>.sql
```

(or, running the tracked file directly by name instead of a generated download, substitute the account first). The role/grants are safe to re-run — only the final `ALTER ROLE ADD MEMBER` line actually changes anything on a second run, and SQL Server's own `ALTER ROLE ADD MEMBER` is itself a no-op if the member is already there.

**Two prerequisites** — the target account needs, in order:
1. **A SQL Server login on this instance.** If it doesn't exist yet, create one (for a Windows/AD account): `CREATE LOGIN [DOMAIN\AccountName] FROM WINDOWS;` — note this needs `DOMAIN\AccountName` form, not a UPN (`account@domain.com`); if you only have the UPN, resolve it first (e.g. `([System.Security.Principal.NTAccount]'account@domain.com').Translate([System.Security.Principal.NTAccount])` in PowerShell, or ask whoever manages AD).
2. **A database user for that login, in `msdb` specifically.** `ALTER ROLE ADD MEMBER` operates on database principals, and SQL Server does **not** automatically create a database user for a login — a login alone isn't enough, even though it looks like it should be. If `ALTER ROLE` fails with `"...because it does not exist or you do not have permission"` and the login genuinely exists, this is almost certainly why: `USE msdb; CREATE USER [DOMAIN\AccountName] FOR LOGIN [DOMAIN\AccountName];` first, then retry `ALTER ROLE ADD MEMBER`.

## Granting the app's own SQL Server access

BlueTrack connects to SQL Server with Windows Integrated Authentication — no SQL login password, no standing secret — as the App Pool's Windows account. `Database/Manual/02_BlueTrack_GrantAppServiceAccountAccess.sql` grants that account what the app needs, and no more than that (not `db_owner`):

- `db_datareader` + `db_datawriter`, and `EXECUTE` on the `dbo` and `web` schemas;
- `db_backupoperator`, for the Deployment page's **Backup App** and scheduled backups;
- an **append-only audit log**: the account may add audit rows but not change or delete them, and may not run the retention purge (that runs from its own SQL Agent job, below).

The installer does this for you: `.\Install-BlueTrack.ps1 -Step Db.AppPoolAccess -GrantAppPoolSqlAccess $true` works out the account, creates its login if it's missing, and runs a filled-in copy of the script (`-AppPoolSqlLogin` overrides the account). The installing user needs `securityadmin` (or `sysadmin`) on the instance. **On an existing install, re-run this step after upgrading** to pick up the backup role and the append-only audit log; it's safe to re-run.

By hand instead (replace the `__TARGET_ACCOUNT__` placeholder first — the script fails if it's left in):

```
sqlcmd -S <server> -C -d BlueTrack -i 02_BlueTrack_GrantAppServiceAccountAccess.sql
```

**Never run this through `App/Migrator`** — granting a real account real permissions is a deliberate, DBA-run action.

**Which account to grant it to**: for an IIS App Pool using the default `ApplicationPoolIdentity`, it depends on where SQL Server runs. On the **same machine** as IIS (BlueTrack's normal single-server layout), grant `IIS APPPOOL\<pool>` (e.g. `IIS APPPOOL\BlueTrack-AppPool`). On **another machine**, grant the computer account, `DOMAIN\HOSTNAME$` (standard Windows behavior, not yet tested with BlueTrack). A gMSA or service account App Pool is always that account. The same two prerequisites as `db_backupstatus_reader` above apply (a real SQL Server login, then a database user for it — this script's own `CREATE USER` step handles the second one for you).

**The misleading error message**: with SQL Server on the same machine, a missing login for `IIS APPPOOL\<pool>` is reported as `Login failed for user 'DOMAIN\HOSTNAME$'. Reason: Could not find a login matching the name provided.` — naming the *computer account*, not the account that actually connected. Granting the named computer account therefore changes nothing, even with its login existing and its SID matching exactly. The account that needs the login is the one in the 18456 event's SID: `Get-WinEvent -FilterHashtable @{LogName='Application'; Id=18456} -MaxEvents 1 | ForEach-Object { $_.UserId.Translate([System.Security.Principal.NTAccount]).Value }`. The installer's smoke test prints this for you when it fails with a 500. A gMSA App Pool (`Admin_Installation.md`, section 3) avoids the trap entirely and is still the recommended setup for least privilege.

## Roles: What People Can See, and When Changes Apply

- **`ViewDashboard` is needed to see tracked data.** Being signed in isn't enough: Account Progress, the dashboard, Risk Exceptions, the Applications list and the reports built on them all need a role holding `ViewDashboard`. If a custom role was built without it, its members see errors or empty pages there; add `ViewDashboard` to the role on **Admin > Roles & Permissions**.
- **Changes made in BlueTrack apply on everyone's next request.** Saving a role on **Roles & Permissions**, a mapping on **Group → Role Mapping**, or a provider on **Identity Providers** clears every user's cached rights.
- **Changes made outside BlueTrack apply within 15 minutes.** A user added to or removed from an AD group keeps their cached rights for at most 15 minutes, even while they keep working. A user can pick up a change at once with **Reload My Rights** on their profile page.

## Data Feed and Backup Folders

BlueTrack's server reads data feed folders (**Admin > Data Sources**) and writes the Backup Folder as itself, so which folders it may use is set at install time, not on a web page:

- **By default**, any local folder is allowed and network (UNC) shares are refused.
- **To allow a share, or to restrict BlueTrack to specific folders**, list them with the installer's `-AllowedFileRoots`, e.g. `-AllowedFileRoots 'D:\Feeds','\\fileserver\bluetrack'`. Once any folder is listed, only folders inside the listed ones are allowed. The installer writes this to the API's `appsettings.<Environment>.json` (`FileAccess:AllowedRoots`); on an existing install, re-run `.\Install-BlueTrack.ps1 -Step Db.AppSettings,Iis.WebConfig -AllowedFileRoots ...` to change it (the API reads the list when it starts; `Iis.WebConfig` recycles the App Pool so the new list applies).
- A folder outside the list is refused when you save it, and again each time it's used (the list may have changed since).
- **Folder permissions are yours to set.** The installer doesn't grant access to feed folders: the App Pool's account needs read access to each one (`IIS APPPOOL\<pool>` for a local folder, the computer account `DOMAIN\HOSTNAME$` for a share on another machine, or the gMSA). The Data Sources page's **Test** button checks access as that account. For the Backup Folder, see "Scheduled backups and the Deployment page" above.

## Settings With a Security Check

A few admin settings carry an extra check. These are the ones an operator is most likely to run into:

1. **SMTP with Basic authentication needs STARTTLS** (**Admin > Notifications**). With **Auth Method** set to **Basic (username/password)** and a **Credential** chosen, **Enable STARTTLS** must be on, or the save is refused, since the credential's password is sent to the SMTP server.
2. **Pointing a stored credential at an SMTP server needs `ManageCredentials`** as well as `ManageNotifications`. With Basic authentication and a credential chosen, changing the credential or any connection setting (host, port, STARTTLS, Auth Method, Ignore all SSL errors) is refused without it, so a stored password can't be sent to a server of someone's choosing.
3. **A field's Required Permission is enforced.** A field in Field Metadata that names a Required Permission can only be changed by someone holding that permission, on the Account Progress edit page, in bulk edit and through the Risk Exception import; otherwise the save is refused, naming the permission. The **Edit Field** page doesn't show this setting (and keeps its value when you save), so setting it is a SQL change to `web.account_progress_field_metadata.RequiredPermission`.
4. **Risk Score's Recalculate Now needs `ManageTargets`**, since it changes stored scores. Viewing the Risk Score report doesn't.
5. **CSV limits.** A CSV file may have at most **250,000 rows**, and an uploaded file at most **30 MB**. A larger file is refused; split it and import each part.

## Segregation of Duties toggle — nothing for a DBA to do

Unlike the grants above, `EnforceRiskExceptionSegregationOfDuties` (Global Application Configuration) is a plain application setting, defaulting off — no schema grant, no manual script, no `msdb` permission involved. An application admin turns it on or off directly in the UI; see `User_Guide.md`.

## The Nightly Import + Load Job

The Import+Load SQL Agent job (`Database/Manual/04_BlueTrack_ScheduleImportLoadJob.sql`, created once per environment per `Admin_DeploymentRunbook.md` step 5, or by the installer's nightly-job option) runs at 2:00 AM, two steps — Import (refreshes staging from the Privilege Cloud/Self-Hosted exports), then Load (`usp_RunFullLoad`, which also drives the risk-scoring recalculation and the AD Account Discovery nightly pass). It lives entirely in `msdb`, like `db_backupstatus_reader` above — there's no in-app UI showing whether last night's run succeeded.

**Privilege Cloud only, Self-Hosted only, or both:** the installer asks which CyberArk sources you have (`-ImportSources`: `Both`, `PrivilegeCloud` or `SelfHosted`) and sets the job's Import step to match. A source that's switched off has its staging tables emptied each night; a missing export for a source that's switched on fails the job, with a message naming the file or database. `usp_Import_All` takes the same choice for a manual import, as `@ImportPrivilegeCloud` and `@ImportSelfHosted` (both default to `1`; at least one must be on). For a Privilege-Cloud-only implementation pass `@ImportSelfHosted = 0` (no `@EVDDatabaseName` needed); for a Self-Hosted-only one pass `@ImportPrivilegeCloud = 0` (no export file parameters needed).

**Targets from account addresses:** each Load creates a Target for every address on an active CyberArk account that no Target has yet: type *Other*, risk score 0, discovery source *CyberArk ETL*, named after the address (identified as an IP address or a hostname). Each account is then linked to every Target with an identifier equal to its address, whether the Target was created this way or by hand. These address links are kept in step with the data on every run (added for new matches, removed when the account is deleted or its address changes); links made any other way, such as a CSV import, are never touched.

**Deleted accounts:** an account the CyberArk export marks as deleted is never added to BlueTrack. If BlueTrack already has it, it's kept and flagged as deleted, so its progress, exceptions and history remain; an account that disappears from the export is flagged the same way. Privilege Cloud uses the export's `Deleted` column. Self-Hosted uses the file's deletion date (`CAFDeletionDate`), counted only when it's a real date: some versions write the epoch (1970-01-01) as a placeholder, so a date before 1970-01-02, or one earlier than the account's creation date, is ignored.

**Ignored safes:** accounts in a safe matching the *ignored safe pattern* (Global Application Configuration) aren't imported, and ones already imported are flagged deleted. Regex patterns need SQL Server 2025; after upgrading SQL Server (and the database's compatibility level to 170), re-run `Database/02_BlueTrack_Baseline_EtlLoads.sql` once to turn Regex on.

**Checking whether it ran, and how it went:**
```sql
SELECT TOP 10 j.name, h.step_name, h.run_date, h.run_time, h.run_status, h.message
FROM msdb.dbo.sysjobhistory h
JOIN msdb.dbo.sysjobs j ON j.job_id = h.job_id
WHERE j.name LIKE 'BlueTrack (%) - Import and Load'
ORDER BY h.instance_id DESC;
```
`run_status`: `0` = Failed, `1` = Succeeded, `2` = Retry, `3` = Canceled, `4` = In progress (standard `sysjobhistory` semantics, not BlueTrack-specific). The job name embeds the database name (`BlueTrack (BlueTrack) - Import and Load` for a database named `BlueTrack`), so jobs for two databases can coexist on the same SQL Server instance without colliding.

**Re-running it manually** (e.g. after fixing a bad export file): either restart the job through SQL Server Agent (`EXEC msdb.dbo.sp_start_job @job_name = 'BlueTrack (BlueTrack) - Import and Load';`), or run the same two steps directly against the target database for more control/visibility — see `Admin_DeploymentRunbook.md`'s "First Data Load" section for the exact `usp_Import_All`/`usp_RunFullLoad` call shape and its real prerequisites (for Self-Hosted, the EVD database must be reachable on the same instance; for Privilege Cloud, the SQL Server *service account*, not your own login, needs read access to the export folder).

**Never edit `Database/Manual/04_BlueTrack_ScheduleImportLoadJob.sql` for a routine schedule change** — its export folder path and EVD database name are hardcoded values for a specific environment (`Admin_DeploymentRunbook.md`'s "Environment-Specific Items" list; the installer fills in a temporary copy instead). To change the 2:00 AM run time, use `sp_update_schedule` against the existing job's schedule; there's no need to touch the script at all.

## The Audit Log Purge Job

`RetentionDays` (Global Application Configuration) is backed by a real nightly purge — the `usp_PurgeAuditLog` procedure (from `Database/06_BlueTrack_Baseline_WebLogic.sql`, applied by the Migrator like every other `Database/*.sql` script) and `Database/Manual/05_BlueTrack_ScheduleAuditLogPurgeJob.sql` (the SQL Agent job scheduling it, which you run by hand with `sqlcmd`, like the Import+Load job above).

Runs nightly at **3:00 AM**, one hour after Import+Load, so a long ETL run never overlaps the purge. Each run deletes `web.audit_field_change` rows before their parent `web.audit_event` rows (FK dependency) older than `RetentionDays` days, and records exactly one row in `web.audit_purge_log` either way:

- **`Status = 'Succeeded'`**, with `RowsPurged` = how many `audit_event` rows were deleted (`0` is a valid, real outcome — nothing was old enough yet).
- **`Status = 'Skipped'`**, `RowsPurged = NULL`, `ErrorMessage` explaining why — this is what happens every night until an admin sets a real `RetentionDays` value on Global Application Configuration. **Installing the job does not, by itself, start deleting anything** — `RetentionDays` is empty by design on a fresh install, so nothing purges until a human picks a real number.
- **`Status = 'Failed'`**, with `ErrorMessage` set — the transaction rolled back (nothing partially deleted); check the message, fix the underlying issue, and either wait for the next nightly run or re-run manually (below).

**Checking the purge history:**
```sql
SELECT TOP 10 PurgeBatchId, CutoffDate, RowsPurged, StartedAt, CompletedAt, Status, ErrorMessage
FROM web.audit_purge_log
ORDER BY StartedAt DESC;
```

**Re-running it manually** (e.g. right after setting `RetentionDays` for the first time, rather than waiting for 3:00 AM): `EXEC dbo.usp_PurgeAuditLog;` directly against the target database, signed in as yourself (the app's own SQL account is denied this procedure), or restart the job through SQL Server Agent (`EXEC msdb.dbo.sp_start_job @job_name = 'BlueTrack (BlueTrack) - Audit Log Purge';`).

## Changing Identity Provider or Secrets Store Backend After Go-Live

`Admin_DeploymentRunbook.md`'s "Environment-Specific Items" covers configuring these correctly during initial setup; this note is about doing it again, later, on a running environment.

- **Identity Providers**: enabling/disabling OIDC, or changing its Authority/Client ID/secret, needs an **app pool restart** to take effect (OIDC is set up when the app starts) — the change won't apply until you recycle the app pool after saving it on the Identity Providers admin page. SAML is read fresh on every request, so no restart is needed there. Certificate thumbprint fields (SAML) refer to certificates already installed in the Windows Certificate Store (`LocalMachine\My`) on the box running BlueTrack — install the certificate there first, or the thumbprint you enter in the UI won't resolve to anything real. **A real gap worth knowing about**: if OIDC's Secret was never actually set, the scheme silently fails to register at startup with no health-check warning — confirm a real sign-in works after restarting, don't just trust a healthy Deployment page. See `Claude_Docs/Design_Admin-Deployment-Management.md`'s "Completing the trust" section for the exact redirect URI/ACS URL/metadata endpoint values a real IdP (Okta, Entra ID, Ping, Authentik) needs from BlueTrack, and how those map onto each vendor's own terminology — not repeated here.
- **Secrets Store backend**: switching the active backend (Secrets Store Configuration admin page) doesn't migrate anything — Safe/Folder/Object (or backend-specific equivalents) that resolved correctly under the old backend may not exist, or may mean something different, under the new one. Use the page's own **Test Connection** tool against a known-good credential immediately after cutting over, before considering the change complete, since a broken lookup here silently breaks every feature that resolves a privileged-account secret. If cutting over *to* `WindowsDpapi` (or changing its `ProtectionScope` between Machine/User), remember DPAPI's own disaster-recovery limitation: ciphertext encrypted under one machine's DPAPI keys cannot be decrypted on a different machine — this matters for backup/restore-based disaster recovery specifically, not routine operation.

## Replacing the Bootstrap Admin Group

Every fresh install maps `BUILTIN\Administrators` (SID `S-1-5-32-544`) to the Admin role, deliberately, so the environment is usable immediately (`Database/05_BlueTrack_Baseline_WebSeed.sql`) — `Admin_DeploymentRunbook.md` already flags leaving this in place permanently as something a real production environment shouldn't do. Doing the replacement safely, without locking yourself out mid-change:

1. On the **Group → Role Mapping** admin page, resolve and add the *real* AD/Entra group that should hold Admin — **before** touching the `BUILTIN\Administrators` mapping. Confirm at least one real person is actually a member of that group and can sign in and reach the Admin hub.
2. Only then delete the `BUILTIN\Administrators` → Admin mapping (same page). It applies on everyone's next request.
3. If something goes wrong before step 2 is confirmed working, the local machine's own `BUILTIN\Administrators` membership is still your way back in — don't remove local admin rights from your own operator account as part of this same change.

## See also

- `Admin_DeploymentRunbook.md` — the full step-by-step procedure for building or rebuilding an environment, including the nightly job's own initial creation (step 5) and the environment-specific items an initial identity provider/secrets store/admin group setup must not skip.
- `Deploy/README.md` — everything `Install-BlueTrack.ps1`, `Update-BlueTrack.ps1` and the standalone `Backup-BlueTrack.ps1`/`Restore-BlueTrack.ps1` scripts do, including parameters not repeated here.
- `Database/README.md` — every `Database/Manual/` script, when it applies and how to run it.
- `Claude_Docs/Design_Deployment-Methodology.md` — the rollback mechanism's design rationale and the deployment steps this guide's rollback section fits into.
- `Claude_Docs/Design_Admin-Deployment-Management.md` — the Deployment page and the `db_backupstatus_reader` role.
- `Claude_Docs/Design_Audit-Logging.md` — the retention/purge mechanism's full design and data model.
- `Claude_Docs/Design_Secrets-Storage.md` / `Design_Credentials-Management.md` — DPAPI's `ProtectionScope` and disaster-recovery limitation in full.
- `Database/Import_Load_Process_Guide.docx` — the authoritative day-to-day Import/Load reference this guide's nightly-job section only summarizes operationally.
- `User_Guide.md` — the in-app admin side of everything referenced here that does have a UI (Identity Providers, Secrets Store Configuration, Group → Role Mapping, Global Application Configuration).
