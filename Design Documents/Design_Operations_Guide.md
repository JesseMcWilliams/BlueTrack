# Operations Guide

**Blueprint Progress Tracking Web Interface**

## Scope

Task-oriented instructions for whoever deploys and operates BlueTrack (a DBA, or whoever holds elevated SQL Server access) — distinct from `Design_Deployment_Runbook.md`, which walks through building or rebuilding an environment from scratch. This guide is about **day 2**: things a DBA/operator does occasionally on a running environment, outside of a full install, that have no UI of their own inside the application (or where the UI is only half the story).

**Scope note (updated 2026-09-21)**: covers the rollback mechanism, granting `db_backupstatus_reader`, the nightly Import+Load job, the audit log purge job, changing identity provider/secrets store configuration on a running environment, and replacing the bootstrap admin group. `Design_Deployment_Runbook.md` and `Deploy/README.md` remain the authoritative references for the full install/rebuild procedure — this guide doesn't repeat that ground, only what comes after it.

## Rollback (Backup and Restore)

BlueTrack's rollback mechanism is full-database backup/restore, not per-script "down" migrations (`Design_Deployment_Methodology.md`'s Option B) — there is no automated "undo this specific schema change" path. Back up before a risky change, and be ready to restore and redeploy the previous build if something goes wrong.

### Taking a backup

`Deploy/Backup-BlueTrack.ps1` backs up the target database, verifies the backup is actually restorable (`RESTORE VERIFYONLY`), and writes a `.manifest.json` alongside it recording the database name, timestamp, and the git commit the backup preceded.

```powershell
.\Backup-BlueTrack.ps1 -SqlServerInstance "SQLSERVER01" -DatabaseName BlueTrack -BackupFolder "D:\Backups\BlueTrack"
```

Add `-IncludeMsdb` only if you also need SQL Server Agent job definitions (the nightly Import+Load job) protected — this backs up `msdb` too, with its own manifest entry. Think carefully before adding this: `msdb` is shared by every database on the SQL Server instance, so restoring it later is an instance-wide action, not scoped to just BlueTrack.

**This also happens automatically** as part of `Install-BlueTrack.ps1`, right before it migrates an *existing* database (a fresh, empty database has nothing worth backing up, so this is skipped automatically in that case). Pass `-BackupFolder` to control where it lands, or `-SkipPreDeployBackup` to opt out entirely for that run.

### Restoring from a backup

`Deploy/Restore-BlueTrack.ps1` restores a specific backup file — there is no "restore the latest one" shortcut, since silently picking the wrong backup is worse than requiring you to name it explicitly.

```powershell
.\Restore-BlueTrack.ps1 -SqlServerInstance "SQLSERVER01" -DatabaseName BlueTrack -BackupFilePath "D:\Backups\BlueTrack\BlueTrack_20260916-020000.bak"
```

This is destructive (`RESTORE DATABASE ... WITH REPLACE`) — you'll be asked to type `YES` to confirm interactively, or pass `-Confirm:$false` for an unattended/scripted run. **This only restores the database.** Also redeploy the application build matching the backup's manifest `GitCommit` field (if one was recorded), so the running application and the restored database stay in sync — this script does not touch IIS, the published API, or the built SPA at all.

To also restore `msdb` (only if you've confirmed a SQL Agent job definition was actually lost as a result of the BlueTrack restore), pass `-RestoreMsdbFilePath` pointing at the corresponding `msdb` backup. You'll get a second, separate warning before that step runs, naming the instance-wide blast radius again.

### What this does not solve

There are still no down-scripts for the numbered `Database/*.sql` files — full-database restore remains the only rollback path. This is the deliberate scope of Option B, not an oversight; per-script rollback for the highest-risk schema changes remains a possible future investment, not something built here.

## Granting `db_backupstatus_reader`

The Deployment admin page's SQL Server backup-status check reads `msdb.dbo.backupset` (and related tables) — something BlueTrack's own least-privileged SQL account cannot do by default, and something the application can never grant to itself (a connection can't widen its own permissions from inside itself). `Database/38_BlueTrack_GrantBackupStatusReaderRole.sql` creates a dedicated `db_backupstatus_reader` role with exactly the `SELECT` grants needed, and adds a specific account or group to it.

**Never run this through `App/Migrator`** — like `14_BlueTrack_ScheduleImportLoadJob.sql`, it targets `msdb`, not the BlueTrack database, and Migrator always excludes it for that structural reason (see the script's own header and `Database/README.md`).

### Getting a filled-in copy

The easiest path: on the **Group / Role Mapping** admin page, resolve the AD group that should be able to read backup status, then click **Generate db_backupstatus_reader Script** (see `Design_User_Guide.md` for the exact UI steps) — this downloads a copy of the script with that group already substituted in.

Alternatively, edit `Database/38_BlueTrack_GrantBackupStatusReaderRole.sql` directly, replacing its `__TARGET_ACCOUNT__` placeholder by hand with the login/account that should be able to read backup status (e.g. `DOMAIN\BlueTrackAppPoolAccount`, or a resolved AD group's account name).

### Running it

```
sqlcmd -S <server> -C -i Grant-db_backupstatus_reader-<account>.sql
```

(or, running the tracked file directly by name instead of a generated download, substitute the account first). The role/grants are safe to re-run — only the final `ALTER ROLE ADD MEMBER` line actually changes anything on a second run, and SQL Server's own `ALTER ROLE ADD MEMBER` is itself a no-op if the member is already there.

**Two real prerequisites, confirmed directly running this against a real environment (2026-09-21)**: the target account needs, in order:
1. **A SQL Server login on this instance.** If it doesn't exist yet, create one (for a Windows/AD account): `CREATE LOGIN [DOMAIN\AccountName] FROM WINDOWS;` — note this needs `DOMAIN\AccountName` form, not a UPN (`account@domain.com`); if you only have the UPN, resolve it first (e.g. `([System.Security.Principal.NTAccount]'account@domain.com').Translate([System.Security.Principal.NTAccount])` in PowerShell, or ask whoever manages AD).
2. **A database user for that login, in `msdb` specifically.** `ALTER ROLE ADD MEMBER` operates on database principals, and SQL Server does **not** automatically create a database user for a login — a login alone isn't enough, even though it looks like it should be. If `ALTER ROLE` fails with `"...because it does not exist or you do not have permission"` and the login genuinely exists, this is almost certainly why: `USE msdb; CREATE USER [DOMAIN\AccountName] FOR LOGIN [DOMAIN\AccountName];` first, then retry `ALTER ROLE ADD MEMBER`.

## Segregation of Duties toggle — nothing for a DBA to do

Unlike the two features above, `EnforceRiskExceptionSegregationOfDuties` (Global Application Configuration) is a plain application setting, defaulting off — no schema grant, no manual script, no `msdb` permission involved. An application admin turns it on or off directly in the UI; see `Design_User_Guide.md`.

## The Nightly Import + Load Job

The Import+Load SQL Agent job (`Database/14_BlueTrack_ScheduleImportLoadJob.sql`, created once per environment per `Design_Deployment_Runbook.md` step 5) runs at 2:00 AM, two steps — Import (refreshes staging from the Privilege Cloud/Self-Hosted exports), then Load (`usp_RunFullLoad`, which also drives the risk-scoring recalculation and the AD Account Discovery nightly pass). It lives entirely in `msdb`, like `db_backupstatus_reader` above — there's no in-app UI showing whether last night's run succeeded.

**Checking whether it ran, and how it went:**
```sql
SELECT TOP 10 j.name, h.step_name, h.run_date, h.run_time, h.run_status, h.message
FROM msdb.dbo.sysjobhistory h
JOIN msdb.dbo.sysjobs j ON j.job_id = h.job_id
WHERE j.name LIKE 'BlueTrack (%) - Import and Load'
ORDER BY h.instance_id DESC;
```
`run_status`: `0` = Failed, `1` = Succeeded, `2` = Retry, `3` = Canceled, `4` = In progress (standard `sysjobhistory` semantics, not BlueTrack-specific). The job name embeds the database name (`BlueTrack (BlueTrack) - Import and Load` for the real database, `BlueTrack (BlueTrack Test) - Import and Load` for the test one) — both can coexist on the same SQL Server instance without colliding, per that script's own header.

**Re-running it manually** (e.g. after fixing a bad export file): either restart the job through SQL Server Agent (`EXEC msdb.dbo.sp_start_job @job_name = 'BlueTrack (BlueTrack) - Import and Load';`), or run the same two steps directly against the target database for more control/visibility — see `Design_Deployment_Runbook.md`'s "First Data Load" section for the exact `usp_Import_All`/`usp_RunFullLoad` call shape and its real prerequisites (the EVD database must be reachable on the same instance; the SQL Server *service account*, not your own login, needs read access to the export folder).

**Never touch `14` itself for a routine schedule change** — its export folder path and EVD database name are real, hardcoded values for a specific host (`Design_Deployment_Runbook.md`'s "Environment-Specific Items" list); editing and re-running it is how you'd update those for this environment, not how you'd, say, change the 2:00 AM run time (that's a plain `sp_update_schedule` against the existing schedule, no need to touch the tracked file at all).

## The Audit Log Purge Job

`RetentionDays` (Global Application Configuration) is backed by a real nightly purge as of 2026-09-21 — `Database/39_BlueTrack_AuditLogPurgeProcedure.sql` (`usp_PurgeAuditLog`, built to `Design_Audit_Logging.md`'s original D-62 design) and `Database/40_BlueTrack_ScheduleAuditLogPurgeJob.sql` (the SQL Agent job scheduling it). Same `msdb`-only, never-through-`App/Migrator` pattern as the Import+Load job above and `db_backupstatus_reader` — `39` is a normal DbUp-managed script, only `40` needs manual/`sqlcmd` installation.

Runs nightly at **3:00 AM**, one hour after Import+Load, so a long ETL run never overlaps the purge. Each run deletes `web.audit_field_change` rows before their parent `web.audit_event` rows (FK dependency) older than `RetentionDays` days, and records exactly one row in `web.audit_purge_log` either way:

- **`Status = 'Succeeded'`**, with `RowsPurged` = how many `audit_event` rows were deleted (`0` is a valid, real outcome — nothing was old enough yet).
- **`Status = 'Skipped'`**, `RowsPurged = NULL`, `ErrorMessage` explaining why — this is what happens every night until an admin sets a real `RetentionDays` value on Global Application Configuration. **Installing the job does not, by itself, start deleting anything** — `RetentionDays` is `NULL` by design on a fresh install (no default was ever decided; see `08_BlueTrack_WebSchema.sql`'s own seed comment), so nothing purges until a human picks a real number.
- **`Status = 'Failed'`**, with `ErrorMessage` set — the transaction rolled back (nothing partially deleted); check the message, fix the underlying issue, and either wait for the next nightly run or re-run manually (below).

**Checking the purge history:**
```sql
SELECT TOP 10 PurgeBatchId, CutoffDate, RowsPurged, StartedAt, CompletedAt, Status, ErrorMessage
FROM web.audit_purge_log
ORDER BY StartedAt DESC;
```

**Re-running it manually** (e.g. right after setting `RetentionDays` for the first time, rather than waiting for 3:00 AM): `EXEC dbo.usp_PurgeAuditLog;` directly against the target database, or restart the job through SQL Server Agent (`EXEC msdb.dbo.sp_start_job @job_name = 'BlueTrack (BlueTrack) - Audit Log Purge';`).

## Changing Identity Provider or Secrets Store Backend After Go-Live

`Design_Deployment_Runbook.md`'s "Environment-Specific Items" covers configuring these correctly during initial setup; this note is about doing it again, later, on a running environment.

- **Identity Providers**: enabling/disabling OIDC, or changing its Authority/Client ID/secret, needs an **app pool restart** to take effect (`AuthenticationExtensions.cs` registers OIDC's scheme at app startup) — the change won't apply until you recycle the app pool after saving it on the Identity Providers admin page. SAML is read fresh on every request, so no restart is needed there. Certificate thumbprint fields (SAML) refer to certificates already installed in the Windows Certificate Store (`LocalMachine\My`) on the box running BlueTrack — install the certificate there first, or the thumbprint you enter in the UI won't resolve to anything real.
- **Secrets Store backend**: switching the active backend (Secrets Store Configuration admin page) doesn't migrate anything — Safe/Folder/Object (or backend-specific equivalents) that resolved correctly under the old backend may not exist, or may mean something different, under the new one. Use the page's own **Test Connection** tool against a known-good credential immediately after cutting over, before considering the change complete, since a broken lookup here silently breaks every feature that resolves a privileged-account secret. If cutting over *to* `WindowsDpapi` (or changing its `ProtectionScope` between Machine/User, D-106), remember DPAPI's own disaster-recovery limitation (D-65): ciphertext encrypted under one machine's DPAPI keys cannot be decrypted on a different machine — this matters for backup/restore-based disaster recovery specifically, not routine operation.

## Replacing the Bootstrap Admin Group

Every fresh install maps `BUILTIN\Administrators` (SID `S-1-5-32-544`) to the Admin role, deliberately, so the environment is usable immediately (`09_BlueTrack_WebSeed.sql`) — `Design_Deployment_Runbook.md` already flags leaving this in place permanently as something a real production environment shouldn't do. Doing the replacement safely, without locking yourself out mid-change:

1. On the **Group / Role Mapping** admin page, resolve and add the *real* AD/Entra group that should hold Admin — **before** touching the `BUILTIN\Administrators` mapping. Confirm at least one real person is actually a member of that group and can sign in and reach the Admin hub.
2. Only then delete the `BUILTIN\Administrators` → Admin mapping (same page).
3. If something goes wrong before step 2 is confirmed working, the local machine's own `BUILTIN\Administrators` membership is still your way back in — don't remove local admin rights from your own operator account as part of this same change.

## See also

- `Design_Deployment_Runbook.md` — the full step-by-step procedure for building or rebuilding an environment, including the nightly job's own initial creation (step 5) and the environment-specific items an initial identity provider/secrets store/admin group setup must not skip.
- `Deploy/README.md` — everything `Install-BlueTrack.ps1` and the standalone `Backup-BlueTrack.ps1`/`Restore-BlueTrack.ps1` scripts do, including parameters not repeated here.
- `Design_Deployment_Methodology.md` — the rollback mechanism's own design rationale (Option B) and the deployment steps this guide's rollback section fits into.
- `Design_Admin_Deployment_Management.md` — the Deployment page and the original design of the `db_backupstatus_reader` role (D-107).
- `Design_Audit_Logging.md` — the retention/purge mechanism's full design and data model (D-62).
- `Design_Secrets_Storage.md` / `Design_Credentials_Management.md` — DPAPI's `ProtectionScope` and disaster-recovery limitation (D-65/D-106) in full.
- `Database/Import_Load_Process_Guide.docx` — the authoritative day-to-day Import/Load reference this guide's nightly-job section only summarizes operationally.
- `Design_User_Guide.md` — the in-app admin side of everything referenced here that does have a UI (Identity Providers, Secrets Store Configuration, Group/Role Mapping, Global Application Configuration).
